namespace EmailScraper.Models;

public sealed class EmailSourceConfig
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Provider { get; set; } = "";

    public string MailboxAddress { get; set; } = "";

    public List<string> FilterAddresses { get; set; } = [];

    public Dictionary<string, List<string>> PerspectiveFilters { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string CredentialsPath { get; set; } = "credentials.json";

    public string TokenPath { get; set; } = "";

    public string ClientId { get; set; } = "";

    public string TenantId { get; set; } = "common";
}
