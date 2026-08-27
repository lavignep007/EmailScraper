using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.Text;

namespace EmailScraper.EmailProviders.Gmail;

public sealed class GmailEmailProvider : IEmailProvider
{
    private const string ApplicationName = "Email Scraper";
    private const string LastHistoryIdKey = "LastHistoryId";

    private readonly AppConfig config;
    private readonly GmailService gmail;

    private GmailEmailProvider(AppConfig config, GmailService gmail)
    {
        this.config = config;
        this.gmail = gmail;
    }

    public string Name => "Gmail";

    public static async Task<IEmailProvider> CreateAsync(AppConfig config)
    {
        var credentialsPath = Path.Combine(AppContext.BaseDirectory, "credentials.json");

        if (!File.Exists(credentialsPath))
            throw new FileNotFoundException(
                $"credentials.json was not found at '{credentialsPath}'.",
                credentialsPath);

        using var stream = new FileStream(credentialsPath, FileMode.Open, FileAccess.Read);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            [GmailService.Scope.GmailReadonly],
            "user",
            CancellationToken.None,
            new FileDataStore("./token", true));

        var gmail = new GmailService(
            new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

        return new GmailEmailProvider(config, gmail);
    }

    public Task<string?> GetSyncCheckpointAsync() =>
        Database.GetSyncStateAsync(config.DatabasePath, LastHistoryIdKey);

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

        var query = BuildQuery(config.EmailAddresses);
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
                    if (await Database.MessageExistsAsync(config.DatabasePath, messageRef.Id))
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
                config.DatabasePath,
                LastHistoryIdKey,
                startingHistoryId.Value.ToString());

        await Database.SetSyncStateAsync(
            config.DatabasePath,
            "LastFullSyncUtc",
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
                await Database.SetSyncStateAsync(config.DatabasePath, LastHistoryIdKey, newestHistoryId);

            await Database.SetSyncStateAsync(
                config.DatabasePath,
                "LastIncrementalSyncUtc",
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
                if (await Database.MessageExistsAsync(config.DatabasePath, messageId))
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
            await Database.SetSyncStateAsync(config.DatabasePath, LastHistoryIdKey, newestHistoryId);

        await Database.SetSyncStateAsync(
            config.DatabasePath,
            "LastIncrementalSyncUtc",
            DateTimeOffset.UtcNow.ToString("O"));

        Console.WriteLine();
        Console.WriteLine("Incremental synchronization finished.");
        Console.WriteLine($"New messages downloaded: {downloaded:N0}");
        Console.WriteLine($"Already archived: {skipped:N0}");

        return new SynchronizationResult(downloaded);
    }

    public Task ValidateAsync() =>
        ArchiveValidator.ValidateAsync(gmail, config.DatabasePath, BuildQuery(config.EmailAddresses));

    public async Task RefreshDeliveryStatesAsync()
    {
        Console.WriteLine();
        Console.WriteLine("Refreshing draft and scheduled-message state from Gmail...");

        var unsentIds = new HashSet<string>(StringComparer.Ordinal);
        await AddMatchingMessageIdsAsync("in:drafts", unsentIds);
        await AddMatchingMessageIdsAsync("in:scheduled", unsentIds);

        var changes = await Database.ReconcileUnsentMessagesAsync(
            config.DatabasePath,
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

        var messages = await Database.GetMessagesForEmlValidationAsync(config.DatabasePath);
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
                await Database.UpdateProviderThreadIdAsync(config.DatabasePath, item.Id, gmailMessage.ThreadId);

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
        var headers = ParseHeaders(rawBytes);
        var date = GetHeader(headers, "Date");
        var subject = GetHeader(headers, "Subject");
        var messageId = GetHeader(headers, "Message-ID");
        var from = GetHeader(headers, "From");
        var to = GetHeader(headers, "To");
        var cc = GetHeader(headers, "Cc");
        var bcc = GetHeader(headers, "Bcc");
        var inReplyTo = GetHeader(headers, "In-Reply-To");
        var references = GetHeader(headers, "References");
        var isReaction = ContainsReactionMimeType(rawBytes);
        var messageType = isReaction ? "Reaction" : "Email";
        var reactionEmoji = isReaction ? TryExtractReactionEmoji(rawBytes) : null;

        if (!IsRelevantMessage(config.EmailAddresses, from, to, cc, bcc)) return false;

        var year = TryGetYear(date);
        var yearDirectory = Path.Combine(config.ArchivePath, "messages", year.ToString());
        Directory.CreateDirectory(yearDirectory);

        var safeSubject = SanitizeFileName(string.IsNullOrWhiteSpace(subject) ? messageType : subject);
        var timestamp = TryFormatDate(date);
        var fileName = $"{timestamp}_{safeSubject}_{gmailMessageId}.eml";
        var filePath = Path.Combine(yearDirectory, fileName);

        await File.WriteAllBytesAsync(filePath, rawBytes);
        await Database.InsertMessageAsync(config.DatabasePath, new MessageRecord
        {
            ProviderMessageId = gmailMessageId,
            ProviderThreadId = message.ThreadId,
            MessageId = messageId,
            InReplyTo = inReplyTo,
            References = references,
            Date = date,
            Subject = subject,
            From = from,
            To = to,
            Cc = cc,
            Bcc = bcc,
            MessageType = messageType,
            IsUnsent = IsUnsent(message.LabelIds),
            ReactionEmoji = reactionEmoji,
            FilePath = filePath
        });

        return true;
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

    private static bool IsRelevantMessage(
        IReadOnlyCollection<string> addresses,
        params string?[] headers)
    {
        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header)) continue;

            foreach (var address in addresses)
                if (header.Contains(address, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool ContainsReactionMimeType(byte[] raw) =>
        Encoding.UTF8.GetString(raw).Contains(
            "text/vnd.google.email-reaction+json",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsent(IEnumerable<string>? labelIds) =>
        labelIds?.Any(label =>
            string.Equals(label, "DRAFT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(label, "SCHEDULED", StringComparison.OrdinalIgnoreCase)) == true;

    private static string? TryExtractReactionEmoji(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        const string marker = "\"emoji\":\"";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;

        index += marker.Length;
        var end = text.IndexOf('"', index);
        return end < 0 ? null : text[index..end];
    }

    private static Dictionary<string, string> ParseHeaders(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        var separator = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (separator < 0) separator = text.IndexOf("\n\n", StringComparison.Ordinal);

        var headerText = separator >= 0 ? text[..separator] : text;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentName = null;

        foreach (var rawLine in headerText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (currentName is not null) headers[currentName] += " " + line.Trim();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            currentName = line[..colon].Trim();
            headers[currentName] = line[(colon + 1)..].Trim();
        }

        return headers;
    }

    private static string? GetHeader(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var value) ? value : null;

    private static byte[] DecodeBase64Url(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        if (value.Length % 4 == 2) value += "==";
        else if (value.Length % 4 == 3) value += "=";
        return Convert.FromBase64String(value);
    }

    private static int TryGetYear(string? date) =>
        DateTimeOffset.TryParse(date, out var parsed) ? parsed.Year : 0;

    private static string TryFormatDate(string? date) =>
        DateTimeOffset.TryParse(date, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd_HHmmss")
            : "unknown-date";

    private static string SanitizeFileName(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
            value = value.Replace(character, '_');

        value = value.Replace('\r', ' ').Replace('\n', ' ');
        while (value.Contains("  ")) value = value.Replace("  ", " ");

        value = value.Trim().TrimEnd('.');
        return value.Length > 150 ? value[..150] : value;
    }
}
