namespace EmailScraper.Models;

public sealed class MessageRecord
{
    public string ProviderMessageId { get; set; } = "";

    public string? ProviderThreadId { get; set; }

    public string? MessageId { get; set; }

    public string? InReplyTo { get; set; }

    public string? References { get; set; }

    public string? Date { get; set; }

    public string? Subject { get; set; }

    public string? From { get; set; }

    public string? To { get; set; }

    public string? Cc { get; set; }

    public string? Bcc { get; set; }

    public string MessageType { get; set; } = "Email";

    public string? ReactionEmoji { get; set; }

    public string FilePath { get; set; } = "";
}
