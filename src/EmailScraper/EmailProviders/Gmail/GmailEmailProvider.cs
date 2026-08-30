using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace EmailScraper.EmailProviders.Gmail;

public sealed class GmailEmailProvider : IEmailProvider
{
    private const string ApplicationName = "Email Scraper";
    private const string LastHistoryIdKey = "LastHistoryId";

    private readonly AppConfig appConfig;
    private readonly EmailSourceConfig source;
    private readonly GmailService gmail;

    private GmailEmailProvider(
        AppConfig appConfig,
        EmailSourceConfig source,
        GmailService gmail)
    {
        this.appConfig = appConfig;
        this.source = source;
        this.gmail = gmail;
    }

    public string SourceKey => source.Id;

    public string Name => $"{source.Name} (Gmail)";

    public string MailboxAddress => source.MailboxAddress;

    public static async Task<IEmailProvider> CreateAsync(
        AppConfig appConfig,
        EmailSourceConfig source)
    {
        var credentialsPath = ResolveRuntimePath(source.CredentialsPath);

        if (!File.Exists(credentialsPath))
            throw new FileNotFoundException(
                $"credentials.json was not found at '{credentialsPath}'.",
                credentialsPath);

        using var stream = new FileStream(credentialsPath, FileMode.Open, FileAccess.Read);
        var tokenPath = string.IsNullOrWhiteSpace(source.TokenPath)
            ? Path.Combine(AppContext.BaseDirectory, "token", source.Id, "gmail")
            : ResolveRuntimePath(source.TokenPath);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            [GmailService.Scope.GmailReadonly],
            source.Id,
            CancellationToken.None,
            new FileDataStore(tokenPath, true));

        var gmail = new GmailService(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

        var profile = await gmail.Users.GetProfile("me").ExecuteAsync();
        if (!string.IsNullOrWhiteSpace(source.MailboxAddress) &&
            !string.Equals(profile.EmailAddress, source.MailboxAddress, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Gmail source '{source.Id}' authenticated as '{profile.EmailAddress}', " +
                $"but MailboxAddress is '{source.MailboxAddress}'.");

        return new GmailEmailProvider(appConfig, source, gmail);
    }

    public Task<string?> GetSyncCheckpointAsync() =>
        Database.GetSyncStateAsync(appConfig.DatabasePath, StateKey(LastHistoryIdKey));

    public async Task<SynchronizationResult> FullSyncAsync()
    {
        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" FULL SYNCHRONIZATION");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        // Capture the checkpoint first so a later incremental sync sees
        // messages that arrive while the full synchronization is running.
        var profile = await gmail.Users.GetProfile("me").ExecuteAsync();
        var startingHistoryId = profile.HistoryId;

        Console.WriteLine($"Starting history ID: {startingHistoryId}");
        Console.WriteLine();

        var query = BuildQuery(source.FilterAddresses);
        Console.WriteLine("Gmail query:");
        Console.WriteLine($"  {query}");
        Console.WriteLine();

        string? pageToken = null;
        long discovered = 0;
        long downloaded = 0;
        long skipped = 0;

        do
        {
            Console.WriteLine($"Requesting Gmail page... discovered={discovered:N0}, " +
                $"downloaded={downloaded:N0}, skipped={skipped:N0}");

            var request = gmail.Users.Messages.List("me");
            request.Q = query;
            request.IncludeSpamTrash = true;
            request.MaxResults = 500;
            request.PageToken = pageToken;

            var response = await request.ExecuteAsync();
            if (response.Messages is null) break;

            foreach (var messageRef in response.Messages)
            {
                discovered++;

                try
                {
                    if (await Database.MessageExistsAsync(appConfig.DatabasePath, source.Id, messageRef.Id))
                    {
                        skipped++;
                        continue;
                    }

                    if (await DownloadMessageAsync(messageRef.Id)) downloaded++;

                    Console.Write($"\rDiscovered: {discovered:N0}  Downloaded: {downloaded:N0}  Skipped: {skipped:N0}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine($"ERROR processing {messageRef.Id}:");
                    Console.WriteLine(ex.Message);
                }
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        Console.WriteLine();

        if (startingHistoryId.HasValue)
            await Database.SetSyncStateAsync(
                appConfig.DatabasePath,
                StateKey(LastHistoryIdKey),
                startingHistoryId.Value.ToString());

        await Database.SetSyncStateAsync(
            appConfig.DatabasePath,
            StateKey("LastFullSyncUtc"),
            DateTimeOffset.UtcNow.ToString("O"));

        Console.WriteLine();
        Console.WriteLine("Full synchronization finished.");
        Console.WriteLine($"Discovered: {discovered:N0}");
        Console.WriteLine($"Downloaded: {downloaded:N0}");
        Console.WriteLine($"Already archived: {skipped:N0}");

        return new SynchronizationResult(downloaded);
    }

    public async Task<SynchronizationResult> IncrementalSyncAsync(string syncCheckpoint)
    {
        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" INCREMENTAL SYNCHRONIZATION");
        Console.WriteLine("==========================================");
        Console.WriteLine();
        Console.WriteLine($"Checking changes after history ID {syncCheckpoint}...");
        Console.WriteLine();

        string? pageToken = null;
        var messageIds = new HashSet<string>();
        string? newestHistoryId = null;

        try
        {
            do
            {
                var request = gmail.Users.History.List("me");
                request.StartHistoryId = ulong.Parse(syncCheckpoint);
                request.HistoryTypes = UsersResource.HistoryResource
                    .ListRequest.HistoryTypesEnum.MessageAdded;
                request.MaxResults = 500;
                request.PageToken = pageToken;

                var response = await request.ExecuteAsync();

                if (response.HistoryId.HasValue)
                    newestHistoryId = response.HistoryId.Value.ToString();

                if (response.History is not null)
                {
                    foreach (var history in response.History)
                    {
                        if (history.MessagesAdded is null) continue;

                        foreach (var added in history.MessagesAdded)
                        {
                            var id = added.Message?.Id;
                            if (!string.IsNullOrWhiteSpace(id)) messageIds.Add(id);
                        }
                    }
                }

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrWhiteSpace(pageToken));
        }
        catch (GoogleApiException ex) when (ex.Error?.Code == 404)
        {
            Console.WriteLine();
            Console.WriteLine("The stored Gmail history ID is no longer available.");
            Console.WriteLine("Gmail requires us to perform a FULL synchronization.");
            Console.WriteLine();
            return await FullSyncAsync();
        }

        Console.WriteLine($"New Gmail messages found: {messageIds.Count:N0}");

        if (messageIds.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(newestHistoryId))
                await Database.SetSyncStateAsync(
                    appConfig.DatabasePath, StateKey(LastHistoryIdKey), newestHistoryId);

            await Database.SetSyncStateAsync(
                appConfig.DatabasePath,
                StateKey("LastIncrementalSyncUtc"),
                DateTimeOffset.UtcNow.ToString("O"));

            Console.WriteLine("Nothing new to archive.");
            return new SynchronizationResult(0);
        }

        long downloaded = 0;
        long skipped = 0;

        foreach (var messageId in messageIds)
        {
            try
            {
                if (await Database.MessageExistsAsync(appConfig.DatabasePath, source.Id, messageId))
                {
                    skipped++;
                    continue;
                }

                if (await DownloadMessageAsync(messageId)) downloaded++;
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.WriteLine($"Skipping Gmail message {messageId}: message no longer exists.");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"ERROR processing {messageId}:");
                Console.WriteLine(ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(newestHistoryId))
            await Database.SetSyncStateAsync(
                appConfig.DatabasePath, StateKey(LastHistoryIdKey), newestHistoryId);

        await Database.SetSyncStateAsync(
            appConfig.DatabasePath,
            StateKey("LastIncrementalSyncUtc"),
            DateTimeOffset.UtcNow.ToString("O"));

        Console.WriteLine();
        Console.WriteLine("Incremental synchronization finished.");
        Console.WriteLine($"New messages downloaded: {downloaded:N0}");
        Console.WriteLine($"Already archived: {skipped:N0}");

        return new SynchronizationResult(downloaded);
    }

    public Task ValidateAsync() =>
        ArchiveValidator.ValidateGmailSourceAsync(
            gmail,
            appConfig.DatabasePath,
            source.Id,
            BuildQuery(source.FilterAddresses));

    public async Task RefreshDeliveryStatesAsync()
    {
        Console.WriteLine();
        Console.WriteLine("Refreshing draft and scheduled-message state from Gmail...");

        var unsentIds = new HashSet<string>(StringComparer.Ordinal);
        await AddMatchingMessageIdsAsync("in:drafts", unsentIds);
        await AddMatchingMessageIdsAsync("in:scheduled", unsentIds);

        var changes = await Database.ReconcileUnsentMessagesAsync(
            appConfig.DatabasePath,
            source.Id,
            unsentIds);

        Console.WriteLine($"Currently unsent:       {changes.CurrentlyUnsent:N0}");
        Console.WriteLine($"Newly marked unsent:    {changes.NewlyUnsent:N0}");
        Console.WriteLine($"Newly marked delivered: {changes.NewlyDelivered:N0}");
    }

    private async Task AddMatchingMessageIdsAsync(
        string query,
        ISet<string> destination)
    {
        string? pageToken = null;

        do
        {
            var request = gmail.Users.Messages.List("me");
            request.Q = query;
            request.IncludeSpamTrash = true;
            request.MaxResults = 500;
            request.PageToken = pageToken;

            var response = await request.ExecuteAsync();
            if (response.Messages is not null)
            {
                foreach (var message in response.Messages)
                    if (!string.IsNullOrWhiteSpace(message.Id))
                        destination.Add(message.Id);
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));
    }

    public async Task RepairCorruptEmlFilesAsync()
    {
        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" EML VALIDATION / REPAIR");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        var messages = await Database.GetMessagesForEmlValidationAsync(
            appConfig.DatabasePath,
            source.Id);
        var corrupt = messages
            .Where(x => string.IsNullOrWhiteSpace(x.FilePath) ||
                !File.Exists(x.FilePath) || new FileInfo(x.FilePath).Length == 0)
            .ToList();

        Console.WriteLine($"Messages in database: {messages.Count:N0}");
        Console.WriteLine($"Missing/empty EMLs:    {corrupt.Count:N0}");
        Console.WriteLine();

        if (corrupt.Count == 0)
        {
            Console.WriteLine("No corrupt EML files found.");
            return;
        }

        var repaired = 0;
        var errors = 0;

        foreach (var item in corrupt)
        {
            try
            {
                Console.WriteLine($"Repairing message {item.Id}...");
                var gmailMessage = await GetRawMessageAsync(item.ProviderMessageId);
                var rawBytes = DecodeBase64Url(gmailMessage.Raw!);

                if (rawBytes.Length == 0)
                    throw new InvalidOperationException("Decoded Gmail RAW data is empty.");

                if (string.IsNullOrWhiteSpace(item.FilePath))
                    throw new InvalidOperationException("Database FilePath is empty.");

                var directory = Path.GetDirectoryName(item.FilePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                var tempPath = item.FilePath + ".repair";
                await File.WriteAllBytesAsync(tempPath, rawBytes);

                if (new FileInfo(tempPath).Length == 0)
                    throw new InvalidOperationException("Temporary repaired EML is empty.");

                File.Move(tempPath, item.FilePath, overwrite: true);
                await Database.UpdateProviderThreadIdAsync(
                    appConfig.DatabasePath, item.Id, gmailMessage.ThreadId);

                Console.WriteLine($"  OK - {rawBytes.Length:N0} bytes");
                repaired++;
            }
            catch (Exception ex)
            {
                errors++;
                Console.WriteLine($"  ERROR: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Repaired: {repaired:N0}");
        Console.WriteLine($"Errors:   {errors:N0}");
        Console.WriteLine();
    }

    private async Task<bool> DownloadMessageAsync(string gmailMessageId)
    {
        var message = await GetRawMessageAsync(gmailMessageId);
        var rawBytes = DecodeBase64Url(message.Raw!);
        return await RawMessageImporter.ImportAsync(
            appConfig,
            source,
            gmailMessageId,
            message.ThreadId,
            rawBytes,
            IsUnsent(message.LabelIds));
    }

    private async Task<Google.Apis.Gmail.v1.Data.Message> GetRawMessageAsync(string gmailMessageId)
    {
        var request = gmail.Users.Messages.Get("me", gmailMessageId);
        request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
        var message = await request.ExecuteAsync();

        if (string.IsNullOrWhiteSpace(message.Raw))
            throw new InvalidOperationException($"Message {gmailMessageId} contained no RAW data.");

        return message;
    }

    internal static string BuildQuery(IReadOnlyCollection<string> addresses)
    {
        if (addresses.Count == 0) return "in:anywhere";

        var terms = new List<string>();

        foreach (var address in addresses)
        {
            terms.Add($"from:{address}");
            terms.Add($"to:{address}");
            terms.Add($"cc:{address}");
            terms.Add($"bcc:{address}");
        }

        return "{" + string.Join(" ", terms) + "}";
    }

    private static bool IsUnsent(IEnumerable<string>? labelIds) =>
        labelIds?.Any(label =>
            string.Equals(label, "DRAFT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(label, "SCHEDULED", StringComparison.OrdinalIgnoreCase)) == true;

    private static byte[] DecodeBase64Url(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        if (value.Length % 4 == 2) value += "==";
        else if (value.Length % 4 == 3) value += "=";
        return Convert.FromBase64String(value);
    }

    private string StateKey(string name) => $"Source:{source.Id}:Gmail:{name}";

    private static string ResolveRuntimePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}
