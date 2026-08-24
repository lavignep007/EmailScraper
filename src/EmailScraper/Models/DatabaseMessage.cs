namespace EmailScraper.Models;

public sealed class DatabaseMessage
{
    public long Id { get; set; }

    public string GmailId { get; set; } = "";

    public int? ParserVersion { get; set; }
}
