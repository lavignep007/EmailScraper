namespace EmailScraper.Models;

public sealed class PerspectiveArchiveConfig
{
    public string Name { get; set; } = "";

    public string ArchivePath { get; set; } = "";

    public List<string> EmailAddresses { get; set; } = [];
}
