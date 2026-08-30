namespace EmailScraper.EmailProviders;

public interface IEmailProvider
{
    string SourceKey { get; }

    string Name { get; }

    string MailboxAddress { get; }

    Task<string?> GetSyncCheckpointAsync();

    Task<SynchronizationResult> FullSyncAsync();

    Task<SynchronizationResult> IncrementalSyncAsync(string syncCheckpoint);

    Task RepairCorruptEmlFilesAsync();

    Task RefreshDeliveryStatesAsync();

    Task ValidateAsync();
}
