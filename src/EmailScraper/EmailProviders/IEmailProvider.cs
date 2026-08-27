namespace EmailScraper.EmailProviders;

public interface IEmailProvider
{
    string Name { get; }

    Task<string?> GetSyncCheckpointAsync();

    Task<SynchronizationResult> FullSyncAsync();

    Task<SynchronizationResult> IncrementalSyncAsync(string syncCheckpoint);

    Task RepairCorruptEmlFilesAsync();

    Task RefreshDeliveryStatesAsync();

    Task ValidateAsync();
}
