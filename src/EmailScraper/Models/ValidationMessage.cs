namespace EmailScraper.Models;

public sealed class ValidationMessage
{
    public long Id { get; set; }

    public string ProviderMessageId { get; set; } = "";

    public string? MessageId { get; set; }

    public string FilePath { get; set; } = "";
}
