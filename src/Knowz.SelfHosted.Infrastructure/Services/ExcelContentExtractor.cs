using Knowz.Core.Entities;
using Knowz.SelfHosted.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Knowz.SelfHosted.Infrastructure.Services;

public class ExcelContentExtractor : IFileContentExtractor
{
    internal const int MaxExtractionChars = 10_000_000; // 10M chars

    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-excel"
    };

    private const string XlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string XlsMimeType = "application/vnd.ms-excel";

    private readonly ILogger<ExcelContentExtractor> _logger;

    public ExcelContentExtractor(ILogger<ExcelContentExtractor> logger)
    {
        _logger = logger;
    }

    public bool CanExtract(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        return SupportedTypes.Contains(contentType);
    }

    public async Task<FileExtractionResult> ExtractAsync(
        FileRecord fileRecord, Stream fileStream, CancellationToken ct = default)
    {
        if (!CanExtract(fileRecord.ContentType))
            return new FileExtractionResult(false, ErrorMessage: "Unsupported content type");

        // Legacy XLS format cannot be read by OpenXml — requires Document Intelligence fallback
        if (string.Equals(fileRecord.ContentType, XlsMimeType, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Legacy XLS format for {FileRecordId} ({FileName}) requires Document Intelligence fallback",
                fileRecord.Id, fileRecord.FileName);
            return new FileExtractionResult(false,
                ErrorMessage: "Legacy XLS format is not supported by native extraction; Document Intelligence fallback required");
        }

        try
        {
            // OpenXML requires a seekable stream; buffer if needed
            Stream readStream = fileStream;
            MemoryStream? memoryStream = null;
            if (!fileStream.CanSeek)
            {
                memoryStream = new MemoryStream();
                await fileStream.CopyToAsync(memoryStream, ct);
                memoryStream.Position = 0;
                readStream = memoryStream;
            }

            try
            {
                using var document = SpreadsheetDocument.Open(readStream, false);
                var workbookPart = document.WorkbookPart;
                if (workbookPart?.Workbook?.Sheets == null)
                    return new FileExtractionResult(false, ErrorMessage: "Excel file has no sheets");

                var sheets = workbookPart.Workbook.Sheets.Elements<Sheet>().ToList();
                var textParts = new List<string>();
                var totalChars = 0;
                var truncated = false;
                var hasContent = false;

                foreach (var sheet in sheets)
                {
                    ct.ThrowIfCancellationRequested();

                    if (truncated) break;

                    var sheetName = sheet.Name?.Value ?? "Sheet";
                    var relationshipId = sheet.Id?.Value;
                    if (string.IsNullOrEmpty(relationshipId))
                        continue;

                    if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                        continue;

                    var sheetData = worksheetPart.Worksheet?.GetFirstChild<SheetData>();
                    if (sheetData == null)
                        continue;

                    // Get shared strings table for resolving shared string cell values
                    var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

                    // Collect rows for this sheet first, only add header if there's content
                    var sheetRows = new List<string>();
                    foreach (var row in sheetData.Elements<Row>())
                    {
                        ct.ThrowIfCancellationRequested();

                        var cellValues = new List<string>();
                        foreach (var cell in row.Elements<Cell>())
                        {
                            var cellText = GetCellText(cell, sharedStrings);
                            if (!string.IsNullOrEmpty(cellText))
                                cellValues.Add(cellText);
                        }

                        if (cellValues.Count > 0)
                            sheetRows.Add(string.Join("\t", cellValues));
                    }

                    if (sheetRows.Count == 0)
                        continue;

                    hasContent = true;

                    // Add sheet header (account for newline separator)
                    var header = $"--- {sheetName} ---";
                    var headerCost = header.Length + (textParts.Count > 0 ? 1 : 0); // +1 for \n separator
                    if (totalChars + headerCost > MaxExtractionChars)
                    {
                        truncated = true;
                        break;
                    }
                    textParts.Add(header);
                    totalChars += headerCost;

                    foreach (var rowText in sheetRows)
                    {
                        if (truncated) break;

                        var rowCost = rowText.Length + 1; // +1 for \n separator
                        if (totalChars + rowCost > MaxExtractionChars)
                        {
                            var remaining = MaxExtractionChars - totalChars - 1; // -1 for separator
                            if (remaining > 0)
                                textParts.Add(rowText[..remaining]);

                            _logger.LogWarning(
                                "Excel {FileRecordId} ({FileName}) exceeded 10M char extraction limit, content truncated",
                                fileRecord.Id, fileRecord.FileName);
                            truncated = true;
                            break;
                        }

                        textParts.Add(rowText);
                        totalChars += rowCost;
                    }
                }

                if (!hasContent)
                    return new FileExtractionResult(false,
                        ErrorMessage: "Excel file contains no extractable text");

                var text = string.Join("\n", textParts);
                NativeDocumentExtractionMetadata.ApplySuccess(fileRecord);
                return new FileExtractionResult(true, ExtractedText: text);
            }
            finally
            {
                if (memoryStream != null)
                    await memoryStream.DisposeAsync();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Excel extraction failed for FileRecord {Id}", fileRecord.Id);
            return new FileExtractionResult(false, ErrorMessage: ex.Message);
        }
    }

    private static string GetCellText(Cell cell, SharedStringTable? sharedStrings)
    {
        var value = cell.CellValue?.Text;
        if (string.IsNullOrEmpty(value))
        {
            // Check for InlineString
            var inlineString = cell.InlineString?.Text?.Text;
            return inlineString ?? string.Empty;
        }

        // If the cell type is SharedString, resolve via the shared strings table
        if (cell.DataType?.Value == CellValues.SharedString && sharedStrings != null)
        {
            if (int.TryParse(value, out var idx))
            {
                var element = sharedStrings.ElementAtOrDefault(idx);
                return element?.InnerText ?? value;
            }
        }

        return value;
    }
}
