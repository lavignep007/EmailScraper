namespace EmailScraper.Models;

public sealed class PdfMessage
{
    public long Id { get; set; }

    public long ThreadId { get; set; }

    public int SortOrder { get; set; }

    public long? ParentMessageId { get; set; }

    public string MessageType { get; set; } = "Email";

    public string? ReactionEmoji { get; set; }

    public string? Date { get; set; }

    public string? Subject { get; set; }

    public string? From { get; set; }

    public string? To { get; set; }

    public string? Cc { get; set; }

    public string? Bcc { get; set; }

    public string? RelativePath { get; set; }

    public string? DisplayName { get; set; }

    public string? ThreadName { get; set; }
}
