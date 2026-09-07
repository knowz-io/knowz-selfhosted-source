using System.Net.Http.Headers;
using System.Text.Json;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Azure.Core;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class AzureAttachmentAIProvider : IAttachmentAIProvider
{
    private const string VisionSystemPrompt =
        "You analyze software diagrams, architecture images, screenshots, and scanned documents. " +
        "Describe the image in clear prose, explain relationships or flow when visible, and extract visible text accurately.";

    private static readonly string[] CognitiveServicesScope = { "https://cognitiveservices.azure.com/.default" };

    private readonly string? _visionEndpoint;
    private readonly string? _visionApiKey;
    private readonly string? _openAiEndpoint;
    private readonly string? _openAiApiKey;
    private readonly string? _openAiDeploymentName;
    private readonly string? _docIntelligenceEndpoint;
    private readonly string? _docIntelligenceApiKey;
    private readonly ILogger<AzureAttachmentAIProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _tokenCredential;

    // Bearer-token cache for Vision REST (TokenCredential dedupes concurrent token requests,
    // but caching here avoids the inter-thread allocation + ExpiresOn check on every call).
    private AccessToken? _cachedCognitiveToken;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public AzureAttachmentAIProvider(
        IConfiguration configuration,
        ILogger<AzureAttachmentAIProvider> logger,
        IHttpClientFactory httpClientFactory,
        TokenCredential tokenCredential)
    {
        _visionEndpoint = configuration["AzureAIVision:Endpoint"];
        _visionApiKey = configuration["AzureAIVision:ApiKey"];
        _openAiEndpoint = configuration["AzureOpenAI:Endpoint"];
        _openAiApiKey = configuration["AzureOpenAI:ApiKey"];
        _openAiDeploymentName = configuration["AzureOpenAI:DeploymentName"];
        _docIntelligenceEndpoint = configuration["AzureDocumentIntelligence:Endpoint"];
        _docIntelligenceApiKey = configuration["AzureDocumentIntelligence:ApiKey"];
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _tokenCredential = tokenCredential;
    }

    public string ProviderName =>
        HasVisionConfig ? "AzureAIVision" :
        HasDocIntelligenceConfig ? "AzureDocumentIntelligence" :
        "AzureOpenAI";

    public bool HasVisionCapability => HasVisionConfig;

    public bool HasDocumentIntelligenceCapability => HasDocIntelligenceConfig;

    public bool HasModelSynthesisCapability => HasOpenAIConfig;

    // MI swap SH_ENTERPRISE_MI_SWAP §2.5: capabilities derive from endpoint presence only.
    // The TokenCredential is a singleton; whether it can acquire a token is determined at
    // first call (surfaced as an `AuthorizationFailed` 401 from the Azure SDK, not here).
    private bool HasVisionConfig =>
        !string.IsNullOrWhiteSpace(_visionEndpoint);

    private bool HasOpenAIConfig =>
        !string.IsNullOrWhiteSpace(_openAiEndpoint) &&
        !string.IsNullOrWhiteSpace(_openAiDeploymentName);

    private bool HasDocIntelligenceConfig =>
        !string.IsNullOrWhiteSpace(_docIntelligenceEndpoint);

    private async ValueTask<string> GetCognitiveBearerTokenAsync(CancellationToken ct)
    {
        var cached = _cachedCognitiveToken;
        if (cached.HasValue && cached.Value.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return cached.Value.Token;
        }

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = _cachedCognitiveToken;
            if (cached.HasValue && cached.Value.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return cached.Value.Token;
            }

            var token = await _tokenCredential
                .GetTokenAsync(new TokenRequestContext(CognitiveServicesScope), ct)
                .ConfigureAwait(false);
            _cachedCognitiveToken = token;
            return token.Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<VisionAnalysisResult> AnalyzeImageAsync(
        byte[] imageBytes, string contentType, CancellationToken ct = default)
    {
        if (HasVisionConfig)
        {
            var visionResult = await AnalyzeWithAzureAIVisionAsync(imageBytes, contentType, ct);
            if (!visionResult.Success)
                return visionResult;

            if (HasOpenAIConfig)
            {
                var synthesizedDescription = await TrySynthesizeVisionDescriptionAsync(
                    imageBytes, contentType, visionResult, ct);

                if (!string.IsNullOrWhiteSpace(synthesizedDescription))
                {
                    visionResult = visionResult with { Caption = synthesizedDescription };
                }
            }

            return visionResult;
        }

        if (HasOpenAIConfig)
            return await AnalyzeWithGpt4VAsync(imageBytes, contentType, ct);

        return new VisionAnalysisResult(
            Success: false,
            ErrorMessage: "No Azure vision configuration available (AzureAIVision or AzureOpenAI).",
            NotAvailable: true);
    }

    public async Task<DocumentExtractionResult> ExtractDocumentAsync(
        byte[] documentBytes, string contentType, CancellationToken ct = default)
    {
        if (!HasDocIntelligenceConfig)
        {
            return new DocumentExtractionResult(
                Success: false,
                ErrorMessage: "Azure Document Intelligence is not configured.",
                NotAvailable: true);
        }

        try
        {
            // API-key-first; MI fallback for enterprise-mode deploys.
            var client = !string.IsNullOrWhiteSpace(_docIntelligenceApiKey)
                ? new DocumentIntelligenceClient(
                    new Uri(_docIntelligenceEndpoint!),
                    new AzureKeyCredential(_docIntelligenceApiKey))
                : new DocumentIntelligenceClient(
                    new Uri(_docIntelligenceEndpoint!),
                    _tokenCredential);

            var bytesSource = BinaryData.FromBytes(documentBytes);
            var operation = await client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                "prebuilt-layout",
                bytesSource,
                cancellationToken: ct);

            var result = operation.Value;
            var layoutData = new
            {
                PageCount = result.Pages.Count,
                Tables = result.Tables.Select(table => new
                {
                    table.RowCount,
                    table.ColumnCount,
                    PageNumber = table.BoundingRegions.Count > 0 ? table.BoundingRegions[0].PageNumber : 0,
                    Cells = table.Cells.Select(cell => new
                    {
                        cell.RowIndex,
                        cell.ColumnIndex,
                        cell.RowSpan,
                        cell.ColumnSpan,
                        cell.Content,
                        IsHeader = cell.Kind == DocumentTableCellKind.ColumnHeader ||
                                   cell.Kind == DocumentTableCellKind.RowHeader
                    }).ToList(),
                    MarkdownContent = BuildTableMarkdown(table)
                }).ToList(),
                KeyValuePairs = result.KeyValuePairs
                    .Where(kvp => kvp.Key != null)
                    .Select(kvp => new
                    {
                        Key = kvp.Key!.Content,
                        Value = kvp.Value?.Content,
                        kvp.Confidence,
                        PageNumber = kvp.Key!.BoundingRegions.Count > 0 ? kvp.Key.BoundingRegions[0].PageNumber : 0
                    }).ToList(),
                Paragraphs = result.Paragraphs.Select(paragraph => new
                {
                    paragraph.Content,
                    Role = paragraph.Role?.ToString(),
                    PageNumber = paragraph.BoundingRegions.Count > 0 ? paragraph.BoundingRegions[0].PageNumber : 0
                }).ToList()
            };

            return new DocumentExtractionResult(
                Success: true,
                ExtractedText: result.Content,
                LayoutDataJson: JsonSerializer.Serialize(layoutData));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure Document Intelligence extraction failed");
            return new DocumentExtractionResult(false, ErrorMessage: ex.Message);
        }
    }

    private async Task<VisionAnalysisResult> AnalyzeWithAzureAIVisionAsync(
        byte[] imageBytes, string contentType, CancellationToken ct)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            // Caption is not supported in all Azure AI Vision regions (e.g. eastus2).
            // GPT-4V synthesis below (TrySynthesizeVisionDescriptionAsync) generates a
            // richer caption anyway when Azure OpenAI is configured.
            var features = "tags,objects,read";
            var requestUrl = $"{_visionEndpoint!.TrimEnd('/')}/computervision/imageanalysis:analyze?api-version=2024-02-01&features={features}";

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            // API-key-first auth (external-mode deploys). Falls back to MI token
            // when no key is configured (enterprise MI-only deploys).
            if (!string.IsNullOrWhiteSpace(_visionApiKey))
            {
                request.Headers.Add("Ocp-Apim-Subscription-Key", _visionApiKey);
            }
            else
            {
                var bearer = await GetCognitiveBearerTokenAsync(ct).ConfigureAwait(false);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            request.Content = new ByteArrayContent(imageBytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

            var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var caption = TryGetCaption(root);
            var tags = ParseTags(root);
            var objects = ParseObjects(root);
            var ocrText = ParseReadText(root);

            return new VisionAnalysisResult(
                Success: true,
                Caption: caption,
                ExtractedText: ocrText,
                Tags: tags.Count > 0 ? tags : null,
                Objects: objects.Count > 0 ? objects : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure AI Vision analysis failed, falling back to GPT-4V");
            if (HasOpenAIConfig)
                return await AnalyzeWithGpt4VAsync(imageBytes, contentType, ct);
            return new VisionAnalysisResult(false, ErrorMessage: ex.Message);
        }
    }

    private async Task<VisionAnalysisResult> AnalyzeWithGpt4VAsync(
        byte[] imageBytes, string contentType, CancellationToken ct)
    {
        try
        {
            var chatClient = CreateChatClient();

            var imageData = BinaryData.FromBytes(imageBytes);
            var imagePart = ChatMessageContentPart.CreateImagePart(imageData, contentType);
            var textPart = ChatMessageContentPart.CreateTextPart("Analyze this image.");

            var messages = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage(VisionSystemPrompt),
                new UserChatMessage(textPart, imagePart)
            };

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 4096
            };

            var result = await chatClient.CompleteChatAsync(messages, options, ct);
            var extractedText = result.Value.Content[0].Text;

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                return new VisionAnalysisResult(
                    false,
                    ErrorMessage: "Azure OpenAI returned no text for image analysis");
            }

            return new VisionAnalysisResult(
                Success: true,
                Caption: extractedText,
                ExtractedText: extractedText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure OpenAI GPT-4V image analysis failed");
            return new VisionAnalysisResult(false, ErrorMessage: ex.Message);
        }
    }

    private async Task<string?> TrySynthesizeVisionDescriptionAsync(
        byte[] imageBytes,
        string contentType,
        VisionAnalysisResult visionResult,
        CancellationToken ct)
    {
        try
        {
            var chatClient = CreateChatClient();
            var prompt = BuildVisionSynthesisPrompt(visionResult);

            var imageData = BinaryData.FromBytes(imageBytes);
            var imagePart = ChatMessageContentPart.CreateImagePart(imageData, contentType);
            var textPart = ChatMessageContentPart.CreateTextPart(prompt);

            var messages = new List<ChatMessage>
            {
                ChatMessage.CreateSystemMessage(VisionSystemPrompt),
                new UserChatMessage(textPart, imagePart)
            };

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 700
            };

            var result = await chatClient.CompleteChatAsync(messages, options, ct);
            var text = result.Value.Content[0].Text?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Azure OpenAI vision synthesis failed; keeping raw Azure Vision output");
            return null;
        }
    }

    private ChatClient CreateChatClient()
    {
        // API-key-first; MI fallback for enterprise-mode deploys.
        var client = !string.IsNullOrWhiteSpace(_openAiApiKey)
            ? new AzureOpenAIClient(new Uri(_openAiEndpoint!), new AzureKeyCredential(_openAiApiKey))
            : new AzureOpenAIClient(new Uri(_openAiEndpoint!), _tokenCredential);

        return client.GetChatClient(_openAiDeploymentName);
    }

    private static string? TryGetCaption(JsonElement root)
    {
        if (root.TryGetProperty("captionResult", out var captionResult) &&
            captionResult.TryGetProperty("text", out var captionText))
        {
            return captionText.GetString();
        }

        return null;
    }

    private static List<string> ParseTags(JsonElement root)
    {
        var tags = new List<string>();

        if (root.TryGetProperty("tagsResult", out var tagsResult) &&
            tagsResult.TryGetProperty("values", out var tagValues))
        {
            foreach (var tag in tagValues.EnumerateArray())
            {
                if (tag.TryGetProperty("confidence", out var conf) &&
                    conf.GetDouble() >= 0.8 &&
                    tag.TryGetProperty("name", out var tagName))
                {
                    var value = tagName.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && !tags.Contains(value))
                        tags.Add(value);
                }
            }
        }

        return tags;
    }

    private static List<string> ParseObjects(JsonElement root)
    {
        var objects = new List<string>();

        if (root.TryGetProperty("objectsResult", out var objectsResult) &&
            objectsResult.TryGetProperty("values", out var values))
        {
            foreach (var obj in values.EnumerateArray())
            {
                if (obj.TryGetProperty("tags", out var tags))
                {
                    foreach (var tag in tags.EnumerateArray())
                    {
                        if (tag.TryGetProperty("name", out var name))
                        {
                            var value = name.GetString();
                            if (!string.IsNullOrWhiteSpace(value) && !objects.Contains(value))
                                objects.Add(value);
                        }
                    }
                }
            }
        }

        return objects;
    }

    private static string? ParseReadText(JsonElement root)
    {
        if (root.TryGetProperty("readResult", out var readResult) &&
            readResult.TryGetProperty("blocks", out var blocks))
        {
            var lines = new List<string>();
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.TryGetProperty("lines", out var blockLines))
                {
                    foreach (var line in blockLines.EnumerateArray())
                    {
                        if (line.TryGetProperty("text", out var lineText))
                        {
                            var value = lineText.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                                lines.Add(value);
                        }
                    }
                }
            }

            if (lines.Count > 0)
                return string.Join("\n", lines);
        }

        return null;
    }

    private static string BuildVisionSynthesisPrompt(VisionAnalysisResult visionResult)
    {
        var parts = new List<string>
        {
            "Use the image and the structured vision signals below to produce a concise, accurate description.",
            "Prioritize software architecture, diagram flow, labeled components, and relationships when present."
        };

        if (!string.IsNullOrWhiteSpace(visionResult.Caption))
            parts.Add($"Caption: {visionResult.Caption}");

        if (!string.IsNullOrWhiteSpace(visionResult.ExtractedText))
            parts.Add($"OCR text:\n{visionResult.ExtractedText}");

        if (visionResult.Tags is { Count: > 0 })
            parts.Add($"Tags: {string.Join(", ", visionResult.Tags)}");

        if (visionResult.Objects is { Count: > 0 })
            parts.Add($"Objects: {string.Join(", ", visionResult.Objects)}");

        parts.Add("Return 1 short paragraph. Do not invent labels that are not visible.");
        return string.Join("\n\n", parts);
    }

    private static string? BuildTableMarkdown(DocumentTable table)
    {
        if (table.Cells.Count == 0)
            return null;

        var rows = new string?[table.RowCount][];
        for (var rowIndex = 0; rowIndex < table.RowCount; rowIndex++)
            rows[rowIndex] = new string?[table.ColumnCount];

        foreach (var cell in table.Cells)
        {
            if (cell.RowIndex < table.RowCount && cell.ColumnIndex < table.ColumnCount)
                rows[cell.RowIndex][cell.ColumnIndex] = cell.Content;
        }

        var sb = new System.Text.StringBuilder();
        var headerRow = rows[0];
        sb.AppendLine($"| {string.Join(" | ", headerRow.Select(c => c ?? string.Empty))} |");
        sb.AppendLine($"| {string.Join(" | ", Enumerable.Repeat("---", table.ColumnCount))} |");

        for (var rowIndex = 1; rowIndex < rows.Length; rowIndex++)
            sb.AppendLine($"| {string.Join(" | ", rows[rowIndex].Select(c => c ?? string.Empty))} |");

        return sb.ToString().TrimEnd();
    }
}
