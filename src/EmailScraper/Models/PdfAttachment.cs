namespace EmailScraper.Models;

public sealed class PdfAttachment
{
    public string FileName { get; set; } = "";

    public string ContentType { get; set; } = "";

    public long Size { get; set; }

    public string FilePath { get; set; } = "";

    public string? RelativePath { get; set; }

    public string? OriginalFileName { get; set; }

    public string? ContentDisposition { get; set; }

    public string? PartRole { get; set; }

    public bool IsInline { get; set; }
}
