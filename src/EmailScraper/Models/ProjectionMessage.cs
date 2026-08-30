namespace EmailScraper.Models;

public sealed class ProjectionMessage
{
    public string SourceKey { get; set; } = "";

    public string Provider { get; set; } = "";

    public string ProviderMessageId { get; set; } = "";

    public string? ProviderThreadId { get; set; }

    public string? MessageId { get; set; }

    public string? MessageIdRaw { get; set; }

    public string? MimeMessageIdCanonical { get; set; }

    public string? GraphInternetMessageIdRaw { get; set; }

    public string? InReplyTo { get; set; }

    public string? References { get; set; }

    public string? Date { get; set; }

    public string? Subject { get; set; }

    public string? SubjectDecodedExact { get; set; }

    public string? From { get; set; }

    public string? To { get; set; }

    public string? Cc { get; set; }

    public string? Bcc { get; set; }

    public string MessageType { get; set; } = "Email";

    public bool IsUnsent { get; set; }

    public string? ReactionEmoji { get; set; }

    public string FilePath { get; set; } = "";
}
