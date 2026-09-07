namespace Knowz.SelfHosted.API.Models;

public record CreateKnowledgeRequest(
    string Content,
    string? Title = null,
    string? Type = null,
    string? VaultId = null,
    List<string>? Tags = null,
    string? Source = null,
    List<Guid>? AttachmentFileRecordIds = null);

public record UpdateKnowledgeRequest(
    string? Title = null,
    string? Content = null,
    string? Source = null,
    List<string>? Tags = null,
    string? VaultId = null,
    string? SummaryRefinementGuidance = null);

public record CreateVaultRequest(
    string Name,
    string? Description = null,
    string? ParentVaultId = null,
    string? VaultType = null);

public record AskQuestionRequest(
    string Question,
    string? VaultId = null,
    bool ResearchMode = false);

public record UpdateVaultRequest(
    string? Name = null,
    string? Description = null);

public record CreateInboxItemRequest(string Body);

public record UpdateInboxItemRequest(string Body);

public record ConvertInboxItemRequest(string? VaultId = null, List<string>? Tags = null);

public record BatchConvertRequest(List<Guid> Ids, string? VaultId = null, List<string>? Tags = null);

public record BatchMoveKnowledgeRequest(List<Guid> KnowledgeIds, Guid TargetVaultId);

public record ChatMessageRequest(string Role, string Content);
public record ChatRequest(
    string Question,
    List<ChatMessageRequest>? ConversationHistory = null,
    string? VaultId = null,
    bool ResearchMode = false,
    int MaxTurns = 10,
    string? KnowledgeId = null);

public record CreateCommentRequest(
    string Body,
    string? AuthorName = null,
    Guid? ParentCommentId = null,
    string? Sentiment = null);

public record UpdateCommentRequest(
    string? Body = null,
    string? Sentiment = null);

public record AmendKnowledgeRequest(string Instruction);

public record VerifyApiKeyRequest(string? ApiKey = null);

public record CreateRelationshipRequest(
    Guid TargetKnowledgeId,
    string? RelationshipType = null,
    double? Confidence = null,
    double? Weight = null,
    bool? IsBidirectional = null,
    string? Metadata = null);

public record QuickCreateKnowledgeRequest(
    string Content,
    string? VaultId = null);
