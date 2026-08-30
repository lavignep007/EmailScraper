namespace EmailScraper.Models;

public sealed class AppConfig
{
    public string ArchivePath { get; set; } = "./archive";

    public string DatabasePath { get; set; } = "./archive/archive.db";

    public List<EmailSourceConfig> EmailSources { get; set; } = [];

    public List<PerspectiveArchiveConfig> PerspectiveArchives { get; set; } = [];
}
