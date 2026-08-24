using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Data.Sqlite;
using QuestPDF.Infrastructure;
using System.Text;
using System.Text.Json;

const string ApplicationName = "Gmail Archive";

var config = await LoadConfigurationAsync();

if (config.EmailAddresses.Count == 0)
{
    Console.WriteLine("No email addresses configured.");
    return;
}

QuestPDF.Settings.License =
    LicenseType.Community;

Directory.CreateDirectory(config.ArchivePath);
Directory.CreateDirectory(
    Path.Combine(config.ArchivePath, "messages"));

Directory.CreateDirectory("./token");

await Database.InitializeAsync(config.DatabasePath);

var gmail = await CreateGmailServiceAsync();

Console.WriteLine();
Console.WriteLine("==========================================");
Console.WriteLine(" Gmail Archive - V1.1");
Console.WriteLine("==========================================");
Console.WriteLine();

Console.WriteLine("Tracking:");

foreach (var address in config.EmailAddresses)
{
    Console.WriteLine($"  {address}");
}

Console.WriteLine();

var lastHistoryId =
    await Database.GetSyncStateAsync(
        config.DatabasePath,
        "LastHistoryId");

if (string.IsNullOrWhiteSpace(lastHistoryId))
{
    Console.WriteLine(
        "No synchronization state found.");

    Console.WriteLine(
        "This will be the initial FULL synchronization.");

    Console.WriteLine();

    await FullSyncAsync(
        gmail,
        config);
}
else
{
    Console.WriteLine(
        $"Last history ID: {lastHistoryId}");

    Console.WriteLine();

    Console.WriteLine(
        "[I] Incremental synchronization (default)");

    Console.WriteLine(
        "[F] Full synchronization");

    Console.WriteLine();

    Console.Write("Selection: ");

    var input =
        Console.ReadLine()?.Trim().ToUpperInvariant();

    if (input == "F")
    {
        await FullSyncAsync(
            gmail,
            config);
    }
    else
    {
        await IncrementalSyncAsync(
            gmail,
            config,
            lastHistoryId);
    }
}

Console.WriteLine();
Console.WriteLine("[P] Parse local EML archive");
Console.WriteLine("[R] Repair missing/empty EML files");
Console.WriteLine("[T] Rebuild message threads");
Console.WriteLine("[V] View thread diagnostic");
Console.WriteLine("[O] Organize archive");
Console.WriteLine("[D] Generate PDFs");
Console.WriteLine("[X] Build search index");
Console.WriteLine("[S] Search archive");
Console.WriteLine("[C] Validate archive");
Console.WriteLine("[Enter] Exit");

Console.Write("Selection: ");

var postSyncChoice =
    Console.ReadLine()
        ?.Trim()
        .ToUpperInvariant();

switch (postSyncChoice)
{
    case "P":
        await MimeParser.ParseArchiveAsync(
            config.DatabasePath,
            config.ArchivePath);
            
        break;


    case "R":        
        await RepairCorruptEmlFilesAsync(
            gmail,
            config);

        break;

    case "T":
        await ThreadBuilder.BuildAsync(
            config.DatabasePath);

        break;

    case "V":
        await ThreadDiagnostic.ShowAsync(
            config.DatabasePath);

        break;

    case "O":
        await ArchiveOrganizer.RunAsync(
            config.DatabasePath,
            config.ArchivePath);
       
        break;

    case "D":
        await PdfGenerator.GenerateAsync(
            config.DatabasePath,
            config.ArchivePath);

        break;

    case "X":
        await SearchIndexer.BuildAsync(
            config.DatabasePath);

        break;

    case "C":
        var gmailQuery =
            BuildGmailQuery(config.EmailAddresses);

        await ArchiveValidator.ValidateAsync(
            gmail,
            config.DatabasePath,
            gmailQuery);

        break;

    case "S":
    {
        Console.Write(
            "Search: ");

        var query =
            Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(query))
        {
            break;
        }

        var results =
            await Database.SearchAsync(
                config.DatabasePath,
                query);

        Console.WriteLine();

        Console.WriteLine(
            $"Results: {results.Count:N0}");

        Console.WriteLine();

        foreach (var result in results)
        {
            Console.WriteLine(
                $"Message #{result.MessageId}" +
                (result.ThreadId.HasValue
                    ? $"  Thread #{result.ThreadId}"
                    : ""));

            Console.WriteLine(
                $"  Date   : {result.Date}");

            Console.WriteLine(
                $"  From   : {result.From}");

            Console.WriteLine(
                $"  Subject: {result.Subject}");

            Console.WriteLine(
                $"  {result.Snippet}");

            Console.WriteLine();
        }

        break;        
    } 
}

Console.WriteLine();
Console.WriteLine("Done.");


// ============================================================
// Configuration
// ============================================================
static async Task<AppConfig> LoadConfigurationAsync()
{
    const string file = "appsettings.json";

    if (!File.Exists(file))
    {
        throw new FileNotFoundException(
            $"Configuration file '{file}' was not found.");
    }

    await using var stream =
        File.OpenRead(file);

    var config =
        await JsonSerializer.DeserializeAsync<AppConfig>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

    return config
        ?? throw new InvalidOperationException(
            "Could not load configuration.");
}

// ============================================================
// Gmail authentication
// ============================================================
static async Task<GmailService> CreateGmailServiceAsync()
{
    if (!File.Exists("credentials.json"))
    {
        throw new FileNotFoundException(
            "credentials.json was not found.");
    }

    using var stream =
        new FileStream(
            "credentials.json",
            FileMode.Open,
            FileAccess.Read);

    var credential =
        await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets
                .FromStream(stream)
                .Secrets,

            new[]
            {
                GmailService.Scope.GmailReadonly
            },

            "user",

            CancellationToken.None,

            new FileDataStore(
                "./token",
                true));

    return new GmailService(
        new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = ApplicationName
        });
}

// ============================================================
// FULL SYNC
// ============================================================
static async Task FullSyncAsync(
    GmailService gmail,
    AppConfig config)
{
    Console.WriteLine();
    Console.WriteLine("==========================================");
    Console.WriteLine(" FULL SYNCHRONIZATION");
    Console.WriteLine("==========================================");
    Console.WriteLine();

    /*
     * Capture the history ID BEFORE starting the full
     * synchronization.
     *
     * Anything that happens after this point will be
     * picked up by the next incremental synchronization.
     */

    var profile =
        await gmail.Users.GetProfile("me")
            .ExecuteAsync();

    var startingHistoryId =
        profile.HistoryId;

    Console.WriteLine(
        $"Starting history ID: {startingHistoryId}");

    Console.WriteLine();

    var query =
        BuildGmailQuery(
            config.EmailAddresses);

    Console.WriteLine("Gmail query:");
    Console.WriteLine($"  {query}");
    Console.WriteLine();

    string? pageToken = null;

    long discovered = 0;
    long downloaded = 0;
    long skipped = 0;

    do
    {
        Console.WriteLine(
            $"Requesting Gmail page... " +
            $"discovered={discovered:N0}, " +
            $"downloaded={downloaded:N0}, " +
            $"skipped={skipped:N0}");

        var request =
            gmail.Users.Messages.List("me");

        request.Q = query;
        request.IncludeSpamTrash = true;
        request.MaxResults = 500;
        request.PageToken = pageToken;

        var response =
            await request.ExecuteAsync();

        if (response.Messages is null)
        {
            break;
        }

        foreach (var messageRef in response.Messages)
        {
            discovered++;

            try
            {
                if (await Database.MessageExistsAsync(
                        config.DatabasePath,
                        messageRef.Id))
                {
                    skipped++;
                    continue;
                }

                var imported =
                    await DownloadMessageAsync(
                        gmail,
                        config,
                        messageRef.Id);

                if (imported)
                {
                    downloaded++;
                }

                Console.Write(
                    $"\rDiscovered: {discovered:N0}  " +
                    $"Downloaded: {downloaded:N0}  " +
                    $"Skipped: {skipped:N0}");
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"ERROR processing {messageRef.Id}:");

                Console.WriteLine(ex.Message);
            }
        }

        pageToken =
            response.NextPageToken;

    } while (!string.IsNullOrWhiteSpace(pageToken));

    Console.WriteLine();

    /*
     * We deliberately store the history ID captured BEFORE
     * the full sync.
     *
     * The next incremental sync will therefore catch anything
     * that happened while this full sync was running.
     */

    if (startingHistoryId.HasValue)
    {
        await Database.SetSyncStateAsync(
            config.DatabasePath,
            "LastHistoryId",
            startingHistoryId.Value.ToString());
    }

    await Database.SetSyncStateAsync(
        config.DatabasePath,
        "LastFullSyncUtc",
        DateTimeOffset.UtcNow.ToString("O"));

    Console.WriteLine();

    Console.WriteLine(
        $"Full synchronization finished.");

    Console.WriteLine(
        $"Discovered:        {discovered:N0}");

    Console.WriteLine(
        $"Downloaded:        {downloaded:N0}");

    Console.WriteLine(
        $"Already archived:  {skipped:N0}");
}

// ============================================================
// INCREMENTAL SYNC
// ============================================================
static async Task IncrementalSyncAsync(
    GmailService gmail,
    AppConfig config,
    string startHistoryId)
{
    Console.WriteLine();
    Console.WriteLine("==========================================");
    Console.WriteLine(" INCREMENTAL SYNCHRONIZATION");
    Console.WriteLine("==========================================");
    Console.WriteLine();

    Console.WriteLine(
        $"Checking changes after history ID " +
        $"{startHistoryId}...");

    Console.WriteLine();

    string? pageToken = null;

    var messageIds =
        new HashSet<string>();

    string? newestHistoryId = null;

    try
    {
        do
        {
            var request =
                gmail.Users.History.List("me");

            request.StartHistoryId =
                ulong.Parse(startHistoryId);

            request.HistoryTypes =
                UsersResource.HistoryResource
                    .ListRequest.HistoryTypesEnum
                    .MessageAdded;

            request.MaxResults = 500;

            request.PageToken =
                pageToken;

            var response =
                await request.ExecuteAsync();

            if (response.HistoryId.HasValue)
            {
                newestHistoryId =
                    response.HistoryId.Value.ToString();
            }

            if (response.History != null)
            {
                foreach (var history in
                         response.History)
                {
                    if (history.MessagesAdded == null)
                    {
                        continue;
                    }

                    foreach (var added
                             in history.MessagesAdded)
                    {
                        var id =
                            added.Message?.Id;

                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            messageIds.Add(id);
                        }
                    }
                }
            }

            pageToken =
                response.NextPageToken;

        } while (!string.IsNullOrWhiteSpace(
            pageToken));
    }
    catch (GoogleApiException ex)
        when (ex.Error?.Code == 404)
    {
        Console.WriteLine();
        Console.WriteLine(
            "The stored Gmail history ID is no " +
            "longer available.");

        Console.WriteLine(
            "Gmail requires us to perform a FULL " +
            "synchronization.");

        Console.WriteLine();

        await FullSyncAsync(
            gmail,
            config);

        return;
    }

    Console.WriteLine(
        $"New Gmail messages found: " +
        $"{messageIds.Count:N0}");

    if (messageIds.Count == 0)
    {
        /*
         * Even if there were no new messages, we still
         * advance the history ID.
         */

        if (!string.IsNullOrWhiteSpace(
                newestHistoryId))
        {
            await Database.SetSyncStateAsync(
                config.DatabasePath,
                "LastHistoryId",
                newestHistoryId);
        }

        await Database.SetSyncStateAsync(
            config.DatabasePath,
            "LastIncrementalSyncUtc",
            DateTimeOffset.UtcNow.ToString("O"));

        Console.WriteLine(
            "Nothing new to archive.");

        return;
    }

    long downloaded = 0;
    long skipped = 0;

    foreach (var messageId in messageIds)
    {
        try
        {
            if (await Database.MessageExistsAsync(
                    config.DatabasePath,
                    messageId))
            {
                skipped++;
                continue;
            }

            var imported =
                await DownloadMessageAsync(
                    gmail,
                    config,
                    messageId);

            if (imported)
            {
                downloaded++;
            }
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Console.WriteLine(
                $"Skipping Gmail message {messageId}: " +
                "message no longer exists.");
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"ERROR processing {messageId}:");

            Console.WriteLine(
                ex.Message);
        }
    }

    /*
     * Only advance the stored history ID after we've
     * processed the changes successfully.
     */

    if (!string.IsNullOrWhiteSpace(
            newestHistoryId))
    {
        await Database.SetSyncStateAsync(
            config.DatabasePath,
            "LastHistoryId",
            newestHistoryId);
    }

    await Database.SetSyncStateAsync(
        config.DatabasePath,
        "LastIncrementalSyncUtc",
        DateTimeOffset.UtcNow.ToString("O"));

    Console.WriteLine();

    Console.WriteLine(
        $"Incremental synchronization finished.");

    Console.WriteLine(
        $"New messages downloaded: {downloaded:N0}");

    Console.WriteLine(
        $"Already archived:        {skipped:N0}");
}

// ============================================================
// Gmail query
// ============================================================
static string BuildGmailQuery(
    IReadOnlyCollection<string> addresses)
{
    var terms =
        new List<string>();

    foreach (var address in addresses)
    {
        terms.Add($"from:{address}");
        terms.Add($"to:{address}");
        terms.Add($"cc:{address}");
        terms.Add($"bcc:{address}");
    }

    return "{" +
           string.Join(
               " ",
               terms) +
           "}";
}

// ============================================================
// Download one complete message
// ============================================================
static async Task<bool> DownloadMessageAsync(
    GmailService gmail,
    AppConfig config,
    string gmailMessageId)
{
    var request =
        gmail.Users.Messages.Get(
            "me",
            gmailMessageId);

    request.Format =
        UsersResource.MessagesResource
            .GetRequest.FormatEnum.Raw;

    var message =
        await request.ExecuteAsync();

    if (string.IsNullOrWhiteSpace(
            message.Raw))
    {
        throw new InvalidOperationException(
            $"Message {gmailMessageId} contained " +
            $"no RAW data.");
    }

    var rawBytes =
        DecodeBase64Url(message.Raw);

    var headers =
        ParseHeaders(rawBytes);

    var date =
        GetHeader(headers, "Date");

    var subject =
        GetHeader(headers, "Subject");

    var messageId =
        GetHeader(headers, "Message-ID");

    var from =
        GetHeader(headers, "From");

    var to =
        GetHeader(headers, "To");

    var cc =
        GetHeader(headers, "Cc");

    var bcc =
        GetHeader(headers, "Bcc");

    var inReplyTo =
        GetHeader(headers, "In-Reply-To");

    var references =
        GetHeader(headers, "References");

    /*
     * We don't fully parse MIME yet.
     *
     * V2 will properly inspect the MIME tree and detect
     * text/vnd.google.email-reaction+json.
     *
     * For now, we classify it by looking for the MIME
     * content type in the raw message.
     */

    var isReaction =
        ContainsReactionMimeType(rawBytes);

    var messageType =
        isReaction
            ? "Reaction"
            : "Email";

    var reactionEmoji =
        isReaction
            ? TryExtractReactionEmoji(rawBytes)
            : null;

    /*
     * Verify that this message actually involves one
     * of our configured addresses.
     *
     * This is especially important for incremental sync,
     * because history.list isn't filtered by our people.
     */

    if (!IsRelevantMessage(
            config.EmailAddresses,
            from,
            to,
            cc,
            bcc))
    {
        return false;
    }

    var year =
        TryGetYear(date);

    var yearDirectory =
        Path.Combine(
            config.ArchivePath,
            "messages",
            year.ToString());

    Directory.CreateDirectory(
        yearDirectory);

    var safeSubject =
        SanitizeFileName(
            string.IsNullOrWhiteSpace(subject)
                ? messageType
                : subject);

    var timestamp =
        TryFormatDate(date);

    var fileName =
        $"{timestamp}_" +
        $"{safeSubject}_" +
        $"{gmailMessageId}.eml";

    var filePath =
        Path.Combine(
            yearDirectory,
            fileName);

    await File.WriteAllBytesAsync(
        filePath,
        rawBytes);

    await Database.InsertMessageAsync(
        config.DatabasePath,
        new MessageRecord
        {
            GmailId = gmailMessageId,
            GmailThreadId = message.ThreadId,
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
            ReactionEmoji = reactionEmoji,
            FilePath = filePath
        });

    return true;
}

// ============================================================
// Relevance check
// ============================================================
static bool IsRelevantMessage(
    IReadOnlyCollection<string> addresses,
    params string?[] headers)
{
    foreach (var header in headers)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            continue;
        }

        foreach (var address in addresses)
        {
            if (header.Contains(
                    address,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
    }

    return false;
}

// ============================================================
// Reaction detection
// ============================================================
static bool ContainsReactionMimeType(
    byte[] raw)
{
    var text =
        Encoding.UTF8.GetString(raw);

    return text.Contains(
        "text/vnd.google.email-reaction+json",
        StringComparison.OrdinalIgnoreCase);
}

static string? TryExtractReactionEmoji(
    byte[] raw)
{
    /*
     * This is intentionally only a temporary/simple
     * extraction for V1.1.
     *
     * V2 will use a real MIME parser and correctly decode
     * quoted-printable/base64/etc.
     */

    var text =
        Encoding.UTF8.GetString(raw);

    const string marker =
        "\"emoji\":\"";

    var index =
        text.IndexOf(
            marker,
            StringComparison.Ordinal);

    if (index < 0)
    {
        return null;
    }

    index += marker.Length;

    var end =
        text.IndexOf(
            '"',
            index);

    if (end < 0)
    {
        return null;
    }

    return text[index..end];
}

// ============================================================
// MIME header parsing
// ============================================================
static Dictionary<string, string> ParseHeaders(
    byte[] raw)
{
    var text =
        Encoding.UTF8.GetString(raw);

    var separator =
        text.IndexOf(
            "\r\n\r\n",
            StringComparison.Ordinal);

    if (separator < 0)
    {
        separator =
            text.IndexOf(
                "\n\n",
                StringComparison.Ordinal);
    }

    var headerText =
        separator >= 0
            ? text[..separator]
            : text;

    var headers =
        new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

    string? currentName = null;

    foreach (var rawLine in
             headerText.Split('\n'))
    {
        var line =
            rawLine.TrimEnd('\r');

        if (line.StartsWith(" ") ||
            line.StartsWith("\t"))
        {
            if (currentName != null)
            {
                headers[currentName] +=
                    " " + line.Trim();
            }

            continue;
        }

        var colon =
            line.IndexOf(':');

        if (colon <= 0)
        {
            continue;
        }

        currentName =
            line[..colon].Trim();

        var value =
            line[(colon + 1)..].Trim();

        headers[currentName] =
            value;
    }

    return headers;
}

static string? GetHeader(
    Dictionary<string, string> headers,
    string name)
{
    return headers.TryGetValue(
        name,
        out var value)
            ? value
            : null;
}

// ============================================================
// General helpers
// ============================================================
static byte[] DecodeBase64Url(
    string value)
{
    value =
        value
            .Replace('-', '+')
            .Replace('_', '/');

    switch (value.Length % 4)
    {
        case 2:
            value += "==";
            break;

        case 3:
            value += "=";
            break;
    }

    return Convert.FromBase64String(value);
}

static int TryGetYear(
    string? date)
{
    if (DateTimeOffset.TryParse(
            date,
            out var parsed))
    {
        return parsed.Year;
    }

    return 0;
}

static string TryFormatDate(
    string? date)
{
    if (DateTimeOffset.TryParse(
            date,
            out var parsed))
    {
        return parsed
            .ToLocalTime()
            .ToString(
                "yyyy-MM-dd_HHmmss");
    }

    return "unknown-date";
}

static string SanitizeFileName(
    string value)
{
    foreach (var c in
             Path.GetInvalidFileNameChars())
    {
        value =
            value.Replace(
                c,
                '_');
    }

    value =
        value
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    while (value.Contains("  "))
    {
        value =
            value.Replace(
                "  ",
                " ");
    }

    value =
        value
            .Trim()
            .TrimEnd('.');

    if (value.Length > 150)
    {
        value =
            value[..150];
    }

    return value;
}

static string GetRelativePath(
    string archivePath,
    string emlPath)
{
    return Path.GetRelativePath(
        archivePath,
        emlPath)
        .Replace(
            Path.DirectorySeparatorChar,
            '/');
}

static async Task RepairCorruptEmlFilesAsync(
    GmailService gmail,
    AppConfig config)
{
    Console.WriteLine();
    Console.WriteLine(
        "==========================================");

    Console.WriteLine(
        " EML VALIDATION / REPAIR");

    Console.WriteLine(
        "==========================================");

    Console.WriteLine();

    var messages =
        await Database
            .GetMessagesForEmlValidationAsync(
                config.DatabasePath);

    var corrupt =
        messages
            .Where(x =>
                string.IsNullOrWhiteSpace(
                    x.FilePath) ||
                !File.Exists(
                    x.FilePath) ||
                new FileInfo(
                    x.FilePath).Length == 0)
            .ToList();

    Console.WriteLine(
        $"Messages in database: {messages.Count:N0}");

    Console.WriteLine(
        $"Missing/empty EMLs:    {corrupt.Count:N0}");

    Console.WriteLine();

    if (corrupt.Count == 0)
    {
        Console.WriteLine(
            "No corrupt EML files found.");

        return;
    }

    var repaired = 0;
    var errors = 0;

    foreach (var item in corrupt)
    {
        try
        {
            Console.WriteLine(
                $"Repairing message {item.Id}...");

            var request =
                gmail.Users.Messages.Get(
                    "me",
                    item.GmailId);

            request.Format =
                UsersResource.MessagesResource
                    .GetRequest.FormatEnum.Raw;

            var gmailMessage =
                await request.ExecuteAsync();

            if (string.IsNullOrWhiteSpace(
                    gmailMessage.Raw))
            {
                throw new InvalidOperationException(
                    "Gmail returned no RAW data.");
            }

            var rawBytes =
                DecodeBase64Url(
                    gmailMessage.Raw);

            if (rawBytes.Length == 0)
            {
                throw new InvalidOperationException(
                    "Decoded Gmail RAW data is empty.");
            }

            /*
             * If FilePath is missing completely, that's a
             * separate database integrity problem.
             *
             * For the current corruption we're repairing
             * existing zero-byte files, so do not silently
             * invent a new location here.
             */
            if (string.IsNullOrWhiteSpace(
                    item.FilePath))
            {
                throw new InvalidOperationException(
                    "Database FilePath is empty.");
            }

            var directory =
                Path.GetDirectoryName(
                    item.FilePath);

            if (!string.IsNullOrWhiteSpace(
                    directory))
            {
                Directory.CreateDirectory(
                    directory);
            }

            /*
             * Write to a temporary file first.
             *
             * This prevents another zero-byte archive file
             * if the program crashes while writing.
             */
            var tempPath =
                item.FilePath + ".repair";

            await File.WriteAllBytesAsync(
                tempPath,
                rawBytes);

            /*
             * Make sure our temporary download is valid
             * before replacing the archive copy.
             */
            var tempInfo =
                new FileInfo(
                    tempPath);

            if (tempInfo.Length == 0)
            {
                throw new InvalidOperationException(
                    "Temporary repaired EML is empty.");
            }

            File.Move(
                tempPath,
                item.FilePath,
                overwrite: true);

            /*
             * Gmail gives us ThreadId while we're here.
             * Restore it in SQLite.
             */
            await Database
                .UpdateGmailThreadIdAsync(
                    config.DatabasePath,
                    item.Id,
                    gmailMessage.ThreadId);

            Console.WriteLine(
                $"  OK - {rawBytes.Length:N0} bytes");

            repaired++;
        }
        catch (Exception ex)
        {
            errors++;

            Console.WriteLine(
                $"  ERROR: {ex.Message}");
        }
    }

    Console.WriteLine();

    Console.WriteLine(
        $"Repaired: {repaired:N0}");

    Console.WriteLine(
        $"Errors:   {errors:N0}");

    Console.WriteLine();
}
