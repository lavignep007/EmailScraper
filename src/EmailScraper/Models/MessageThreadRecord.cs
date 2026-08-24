namespace EmailScraper.Models;

public sealed class MessageThreadRecord
{
    public long ThreadId { get; set; }

    public long MessageId { get; set; }

    public long? ParentMessageId { get; set; }

    public int SortOrder { get; set; }
}
