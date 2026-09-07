using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class PlatformAttachmentAIProvider : IAttachmentAIProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PlatformAttachmentAIProvider> _logger;

    private static readonly JsonSerializerOptions s_serializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions s_deserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PlatformAttachmentAIProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<PlatformAttachmentAIProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string ProviderName => "Platform";

    public async Task<VisionAnalysisResult> AnalyzeImageAsync(
        byte[] imageBytes, string contentType, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                imageBase64 = Convert.ToBase64String(imageBytes),
                contentType
            };

            using var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var json = JsonSerializer.Serialize(payload, s_serializeOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync("/api/v1/ai-services/vision", httpContent, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var envelope = JsonSerializer.Deserialize<PlatformApiResponse<PlatformVisionData>>(
                body, s_deserializeOptions);

            if (envelope is not { Success: true, Data: not null })
            {
                var errors = envelope?.Errors != null ? string.Join(", ", envelope.Errors) : "Unknown error";
                return new VisionAnalysisResult(false, ErrorMessage: $"Platform vision API failure: {errors}");
            }

            var data = envelope.Data;
            // Platform returns Description (not Caption), Tags as VisionTag[] (not string[]),
            // and no Objects field. Normalize to self-hosted VisionAnalysisResult shape.
            var tagNames = data.Tags?.Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
            // Platform VideoFrameAnalysis has DetectedObjects but image VisionResponse does not —
            // Objects will be null for image analysis from the platform.
            return new VisionAnalysisResult(
                Success: true,
                Caption: data.Description,
                ExtractedText: data.ExtractedText,
                Tags: tagNames,
                Objects: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Platform vision analysis failed");
            return new VisionAnalysisResult(false, ErrorMessage: ex.Message);
        }
    }

    public async Task<DocumentExtractionResult> ExtractDocumentAsync(
        byte[] documentBytes, string contentType, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                documentBase64 = Convert.ToBase64String(documentBytes),
                contentType
            };

            using var client = _httpClientFactory.CreateClient("KnowzPlatformClient");
            var json = JsonSerializer.Serialize(payload, s_serializeOptions);
            var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(
                "/api/v1/ai-services/document-understanding", httpContent, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var envelope = JsonSerializer.Deserialize<PlatformApiResponse<PlatformDocData>>(
                body, s_deserializeOptions);

            if (envelope is not { Success: true, Data: not null })
            {
                var errors = envelope?.Errors != null ? string.Join(", ", envelope.Errors) : "Unknown error";
                return new DocumentExtractionResult(false,
                    ErrorMessage: $"Platform document extraction failure: {errors}");
            }

            var data = envelope.Data;
            return new DocumentExtractionResult(
                Success: true,
                ExtractedText: data.ExtractedText,
                LayoutDataJson: data.LayoutDataJson);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Platform document extraction failed");
            return new DocumentExtractionResult(false, ErrorMessage: ex.Message);
        }
    }

    internal record PlatformApiResponse<T>(bool Success, T? Data, List<string>? Errors);

    // Matches the real platform VisionResponse contract (Knowz.Shared.DTOs.AiServices.VisionDtos)
    internal record PlatformVisionData(
        string? Description,              // platform uses Description, not Caption
        PlatformVisionTag[]? Tags,        // platform uses VisionTag[] with Name+Confidence
        string? ExtractedText,
        string[]? Faces);

    internal record PlatformVisionTag(string Name, double Confidence);

    internal record PlatformDocData(string? ExtractedText, string? LayoutDataJson);
}
