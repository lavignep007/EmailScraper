namespace EmailScraper.Models;

public sealed class DatabaseMessage
{
    public long Id { get; set; }

    public string ProviderMessageId { get; set; } = "";

    public int? ParserVersion { get; set; }
}
