namespace EmailScraper.Models;

public sealed class ExistingThread
{
    public long Id { get; set; }

    public string ThreadKey { get; set; } = "";

    public string RevisionHash { get; set; } = "";

    public List<long> MessageIds { get; set; } = [];
}
