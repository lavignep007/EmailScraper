namespace EmailScraper.Models;

public sealed class AppConfig
{
    public string EmailProvider { get; set; } = "Gmail";

    public string ArchivePath { get; set; } = "./archive";

    public string DatabasePath { get; set; } = "./archive/archive.db";

    public List<string> EmailAddresses { get; set; } = [];
}
