namespace EmailScraper.Models;

public sealed class ThreadRecord
{
    public long Id { get; set; }

    public string ThreadKey { get; set; } = "";

    public string? Subject { get; set; }

    public string? FirstDate { get; set; }

    public string? LastDate { get; set; }

    public string? Name { get; internal set; }
}
