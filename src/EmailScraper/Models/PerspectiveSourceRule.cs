namespace EmailScraper.Models;

public sealed record PerspectiveSourceRule(
    string SourceKey,
    IReadOnlyCollection<string> EmailAddresses);
