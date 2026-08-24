public sealed class SearchSourceMessage
{
    public long MessageId { get; set; }

    public long? ThreadId { get; set; }

    public string? Date { get; set; }

    public string? Subject { get; set; }

    public string? From { get; set; }

    public string? To { get; set; }

    public string? Cc { get; set; }

    public string FilePath { get; set; } = "";

    public string? AttachmentNames { get; set; }
}