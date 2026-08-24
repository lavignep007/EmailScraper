namespace EmailScraper.Models;

public sealed class SearchResult
{
    public long MessageId { get; set; }

    public long? ThreadId { get; set; }

    public string? Date { get; set; }

    public string? Subject { get; set; }

    public string? From { get; set; }

    public string? Snippet { get; set; }

    public double Rank { get; set; }
}
