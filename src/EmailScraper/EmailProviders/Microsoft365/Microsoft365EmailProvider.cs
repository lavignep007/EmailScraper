using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace EmailScraper.EmailProviders.Microsoft365;

public sealed class Microsoft365EmailProvider : IEmailProvider
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";
    private readonly AppConfig appConfig;
    private readonly EmailSourceConfig source;
    private readonly HttpClient httpClient;
    private readonly MicrosoftIdentityClient identity;
    private HashSet<string>? lastUnsentIds;
    private string? outboxFolderId;

    private Microsoft365EmailProvider(
        AppConfig appConfig,
        EmailSourceConfig source,
        HttpClient httpClient,
        MicrosoftIdentityClient identity)
    {
        this.appConfig = appConfig;
        this.source = source;
        this.httpClient = httpClient;
        this.identity = identity;
    }

    public string SourceKey => source.Id;

    public string Name => $"{source.Name} (Microsoft 365)";

    public string MailboxAddress => source.MailboxAddress;

    public static async Task<IEmailProvider> CreateAsync(
        AppConfig appConfig,
        EmailSourceConfig source)
    {
        if (string.IsNullOrWhiteSpace(source.ClientId))
            throw new InvalidOperationException(
                $"Microsoft 365 source '{source.Id}' requires ClientId.");

        var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var identity = new MicrosoftIdentityClient(httpClient, source);
        var provider = new Microsoft365EmailProvider(appConfig, source, httpClient, identity);
        var account = await provider.GetJsonAsync<GraphAccount>(
            $"{GraphRoot}/me?$select=mail,userPrincipalName");
        var authenticatedAddress = string.IsNullOrWhiteSpace(account.Mail)
            ? account.UserPrincipalName
            : account.Mail;

        if (!string.IsNullOrWhiteSpace(source.MailboxAddress) &&
            !string.Equals(authenticatedAddress, source.MailboxAddress, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Microsoft 365 source '{source.Id}' authenticated as '{authenticatedAddress}', " +
                $"but MailboxAddress is '{source.MailboxAddress}'.");

        return provider;
    }

    public Task<string?> GetSyncCheckpointAsync() =>
        Database.GetSyncStateAsync(appConfig.DatabasePath, StateKey("LastSuccessfulSyncUtc"));

    public Task<SynchronizationResult> FullSyncAsync() => SynchronizeAsync("FULL");

    public Task<SynchronizationResult> IncrementalSyncAsync(string syncCheckpoint) =>
        SynchronizeAsync("INCREMENTAL");

    public async Task RefreshDeliveryStatesAsync()
    {
        lastUnsentIds ??= await GetCurrentUnsentIdsAsync();
        var changes = await Database.ReconcileUnsentMessagesAsync(
            appConfig.DatabasePath,
            source.Id,
            lastUnsentIds);

        Console.WriteLine();
        Console.WriteLine($"Microsoft 365 delivery state - {source.Name}");
        Console.WriteLine($"Currently unsent:       {changes.CurrentlyUnsent:N0}");
        Console.WriteLine($"Newly marked unsent:    {changes.NewlyUnsent:N0}");
        Console.WriteLine($"Newly marked delivered: {changes.NewlyDelivered:N0}");
    }

    public async Task RepairCorruptEmlFilesAsync()
    {
        var messages = await Database.GetMessagesForEmlValidationAsync(
            appConfig.DatabasePath,
            source.Id);
        var corrupt = messages
            .Where(x => string.IsNullOrWhiteSpace(x.FilePath) ||
                !File.Exists(x.FilePath) || new FileInfo(x.FilePath).Length == 0)
            .ToList();

        Console.WriteLine($"Repairing {corrupt.Count:N0} Microsoft 365 EML file(s) for {source.Name}...");
        foreach (var message in corrupt)
        {
            var bytes = await GetBytesAsync(
                $"{GraphRoot}/me/messages/{Uri.EscapeDataString(message.ProviderMessageId)}/$value");
            var directory = Path.GetDirectoryName(message.FilePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(message.FilePath, bytes);
        }
    }

    public async Task ValidateAsync()
    {
        var remote = (await EnumerateMessagesAsync()).Messages
            .Select(x => x.Id)
            .ToHashSet(StringComparer.Ordinal);
        var local = await Database.GetAllProviderMessageIdsAsync(
            appConfig.DatabasePath,
            source.Id);

        Console.WriteLine();
        Console.WriteLine($"MICROSOFT 365 RECONCILIATION - {source.Name}");
        Console.WriteLine($"Remote mailbox messages: {remote.Count:N0}");
        Console.WriteLine($"Local archived messages: {local.Count:N0}");

        if (source.FilterAddresses.Count > 0)
        {
            Console.WriteLine("Exact missing/extra comparison is skipped because this source has participant filters.");
            return;
        }

        var missing = remote.Except(local).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var extra = local.Except(remote).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Console.WriteLine($"Missing locally:         {missing.Count:N0}");
        Console.WriteLine($"Extra locally:           {extra.Count:N0}");
        foreach (var id in missing) Console.WriteLine($"MISSING LOCAL: {id}");
        foreach (var id in extra) Console.WriteLine($"EXTRA LOCAL: {id}");
    }

    private async Task<SynchronizationResult> SynchronizeAsync(string mode)
    {
        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine($" {mode} SYNCHRONIZATION - {source.Name}");
        Console.WriteLine("==========================================");
        Console.WriteLine(source.FilterAddresses.Count == 0
            ? "Filter: all messages in the mailbox"
            : $"Participant filter: {string.Join(", ", source.FilterAddresses)}");

        var listing = await EnumerateMessagesAsync();
        lastUnsentIds = listing.UnsentIds;
        long downloaded = 0;
        long skipped = 0;
        long excluded = 0;
        long processed = 0;

        foreach (var message in listing.Messages)
        {
            processed++;

            try
            {
                if (await Database.MessageExistsAsync(
                    appConfig.DatabasePath, source.Id, message.Id))
                {
                    skipped++;
                    continue;
                }

                var raw = await GetBytesAsync(
                    $"{GraphRoot}/me/messages/{Uri.EscapeDataString(message.Id)}/$value");
                var imported = await RawMessageImporter.ImportAsync(
                    appConfig,
                    source,
                    message.Id,
                    message.ConversationId,
                    raw,
                    listing.UnsentIds.Contains(message.Id),
                    message.InternetMessageId);
                if (imported) downloaded++;
                else excluded++;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"Skipping Microsoft 365 message {message.Id}: it no longer exists.");
            }

            Console.Write($"\rProcessed: {processed:N0}/{listing.Messages.Count:N0}  " +
                $"Downloaded: {downloaded:N0}  Skipped: {skipped:N0}  Excluded: {excluded:N0}");
        }

        await Database.SetSyncStateAsync(
            appConfig.DatabasePath,
            StateKey("LastSuccessfulSyncUtc"),
            DateTimeOffset.UtcNow.ToString("O"));

        Console.WriteLine();
        Console.WriteLine($"Microsoft 365 synchronization finished for {source.Name}.");
        Console.WriteLine($"Discovered: {listing.Messages.Count:N0}");
        Console.WriteLine($"Downloaded: {downloaded:N0}");
        Console.WriteLine($"Already archived: {skipped:N0}");
        Console.WriteLine($"Excluded by participant filter: {excluded:N0}");
        return new SynchronizationResult(downloaded);
    }

    private async Task<HashSet<string>> GetCurrentUnsentIdsAsync() =>
        (await EnumerateMessagesAsync()).UnsentIds;

    private async Task<GraphListing> EnumerateMessagesAsync()
    {
        outboxFolderId ??= (await GetJsonAsync<GraphFolder>(
            $"{GraphRoot}/me/mailFolders/outbox?$select=id")).Id;
        var messages = new List<GraphMessage>();
        var unsent = new HashSet<string>(StringComparer.Ordinal);
        string? url = $"{GraphRoot}/me/messages?" +
            "$select=id,conversationId,isDraft,parentFolderId,internetMessageId&$top=1000";

        while (!string.IsNullOrWhiteSpace(url))
        {
            var page = await GetJsonAsync<GraphMessagePage>(url);
            foreach (var message in page.Value)
            {
                if (string.IsNullOrWhiteSpace(message.Id)) continue;
                messages.Add(message);
                if (message.IsDraft ||
                    string.Equals(message.ParentFolderId, outboxFolderId, StringComparison.Ordinal))
                    unsent.Add(message.Id);
            }

            url = page.NextLink;
        }

        return new GraphListing(messages, unsent);
    }

    private async Task<T> GetJsonAsync<T>(string url)
    {
        using var response = await SendAsync(url);
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new InvalidOperationException($"Microsoft Graph returned an empty response for {url}.");
    }

    private async Task<byte[]> GetBytesAsync(string url)
    {
        using var response = await SendAsync(url);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(string url)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                await identity.GetAccessTokenAsync(forceRefresh: attempt > 0));
            request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
            var response = await httpClient.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException(
                    $"Microsoft Graph request failed ({(int)status}): {detail}",
                    null,
                    status);
            }

            return response;
        }

        throw new InvalidOperationException("Microsoft Graph authentication retry failed.");
    }

    private string StateKey(string name) => $"Source:{source.Id}:Microsoft365:{name}";

    private sealed record GraphListing(
        List<GraphMessage> Messages,
        HashSet<string> UnsentIds);

    private sealed class GraphAccount
    {
        [JsonPropertyName("mail")]
        public string? Mail { get; set; }

        [JsonPropertyName("userPrincipalName")]
        public string? UserPrincipalName { get; set; }
    }

    private sealed class GraphFolder
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
    }

    private sealed class GraphMessagePage
    {
        [JsonPropertyName("value")]
        public List<GraphMessage> Value { get; set; } = [];

        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; set; }
    }

    private sealed class GraphMessage
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("conversationId")]
        public string? ConversationId { get; set; }

        [JsonPropertyName("isDraft")]
        public bool IsDraft { get; set; }

        [JsonPropertyName("parentFolderId")]
        public string? ParentFolderId { get; set; }

        [JsonPropertyName("internetMessageId")]
        public string? InternetMessageId { get; set; }
    }
}
