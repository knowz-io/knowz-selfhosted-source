namespace Knowz.SelfHosted.Application.DTOs;

public record CommentResponse(
    Guid Id,
    Guid KnowledgeId,
    Guid? ParentCommentId,
    string AuthorName,
    string Body,
    bool IsAnswer,
    string? Sentiment,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<CommentResponse>? Replies,
    int AttachmentCount);

/// <summary>
/// Result of deleting a comment. Reports how many attached files were preserved
/// (kept in storage) vs permanently deleted.
/// WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 — FEAT_CommentDeleteAttachmentChoice
/// </summary>
public record CommentDeleteResult
{
    public int FilesPreserved { get; init; }
    public int FilesDeleted { get; init; }
    public List<string> PreservedFileNames { get; init; } = new();
    public List<string> DeletedFileNames { get; init; } = new();
}
