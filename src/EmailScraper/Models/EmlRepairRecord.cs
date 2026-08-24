namespace EmailScraper.Models;

public sealed class EmlRepairRecord
{
    public long Id { get; set; }

    public string GmailId { get; set; } = "";

    public string FilePath { get; set; } = "";
}
