namespace Knowz.SelfHosted.Application.DTOs;

public record SearchResultResponse(
    Guid KnowledgeId,
    string Title,
    string? Content,
    string? Summary,
    string? VaultName,
    string? TopicName,
    IEnumerable<string>? Tags,
    string? KnowledgeType,
    string? FilePath,
    double Score);

public record SearchResponse(IEnumerable<SearchResultResponse> Items, int TotalResults);

public record AskAnswerResponse(string Answer, IEnumerable<SourceRef> Sources, double Confidence, bool AiUnavailable = false);

public record SourceRef(Guid KnowledgeId, string Title = "");

public record FilePatternResponse(string Pattern, List<FilePatternItem>? Items, int Count);

public record FilePatternItem(Guid Id, string Title, string? FilePath, string Type, DateTime CreatedAt);

// Chat with History DTOs
public record ChatMessageDto(string Role, string Content);
public record ChatResponse(string Answer, IEnumerable<SourceRef> Sources, double Confidence, bool AiUnavailable = false);

// Streaming DTOs
public record StreamingAskResult(IEnumerable<SourceRef> Sources, double Confidence, IAsyncEnumerable<string> TokenStream, bool AiUnavailable = false);
public record StreamingChatResult(IEnumerable<SourceRef> Sources, double Confidence, IAsyncEnumerable<string> TokenStream, bool AiUnavailable = false);
