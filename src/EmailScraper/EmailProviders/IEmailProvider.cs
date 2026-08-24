namespace EmailScraper.EmailProviders;

public interface IEmailProvider
{
    string Name { get; }

    Task<string?> GetSyncCheckpointAsync();

    Task FullSyncAsync();

    Task IncrementalSyncAsync(string syncCheckpoint);

    Task RepairCorruptEmlFilesAsync();

    Task ValidateAsync();
}
