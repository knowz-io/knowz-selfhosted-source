using Knowz.Core.Entities;
using Knowz.Core.Enums;

namespace Knowz.SelfHosted.Infrastructure.Services;

internal static class NativeDocumentExtractionMetadata
{
    public static void ApplySuccess(FileRecord fileRecord)
    {
        fileRecord.TextExtractionStatus = (int)TextExtractionStatus.Completed;
        fileRecord.TextExtractedAt ??= DateTime.UtcNow;
        fileRecord.TextExtractionError = null;
        fileRecord.AttachmentAIProvider ??= "NativeFallback";
    }
}
