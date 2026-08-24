public sealed class ThreadViewRow
{
    public long ThreadId { get; set; }

    public string? ThreadSubject { get; set; }

    public string? FirstDate { get; set; }

    public string? LastDate { get; set; }

    public int SortOrder { get; set; }

    public long? ParentMessageId { get; set; }

    public long MessageId { get; set; }

    public string MessageType { get; set; } = "";

    public string? Date { get; set; }

    public string? From { get; set; }

    public string? To { get; set; }

    public string? Subject { get; set; }

    public string? InternetMessageId { get; set; }

    public string? InReplyTo { get; set; }

    public string? References { get; set; }

    public string? ReactionEmoji { get; set; }
}
