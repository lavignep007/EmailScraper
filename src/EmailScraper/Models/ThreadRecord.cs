namespace EmailScraper.Models;

public sealed class ThreadRecord
{
    public long Id { get; set; }

    public string ThreadKey { get; set; } = "";

    public string? ProviderThreadId { get; set; }

    public string? Subject { get; set; }

    public string? FirstDate { get; set; }

    public string? LastDate { get; set; }

    public string? Name { get; internal set; }

    public string RevisionHash { get; set; } = "";

    public string? PdfRevisionHash { get; set; }

    public bool IsPartial { get; set; }

    public int MissingAncestorCount { get; set; }
}
