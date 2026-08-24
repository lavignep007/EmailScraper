using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

const string ApplicationName = "Gmail Archive";

var config = await LoadConfigurationAsync();

if (config.EmailAddresses.Count == 0)
{
    Console.WriteLine("No email addresses configured.");
    return;
}

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
Console.WriteLine("[Enter] Exit");

Console.Write("Selection: ");

var postSyncChoice =
    Console.ReadLine()
        ?.Trim()
        .ToUpperInvariant();

if (postSyncChoice == "P")
{
    await MimeParser.ParseArchiveAsync(
        config.DatabasePath,
        config.ArchivePath);
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

public sealed class AppConfig
{
    public string ArchivePath { get; set; }
        = "./archive";

    public string DatabasePath { get; set; }
        = "./archive/archive.db";

    public List<string> EmailAddresses { get; set; }
        = [];
}

public sealed class DatabaseMessage
{
    public long Id { get; set; }

    public string GmailId { get; set; } = "";

    public int? ParserVersion { get; set; }
}

public sealed class MessageRecord
{
    public string GmailId { get; set; } = "";

    public string? GmailThreadId { get; set; }

    public string? MessageId { get; set; }

    public string? InReplyTo { get; set; }

    public string? References { get; set; }

    public string? Date { get; set; }

    public string? Subject { get; set; }

    public string? From { get; set; }

    public string? To { get; set; }

    public string? Cc { get; set; }

    public string? Bcc { get; set; }

    public string MessageType { get; set; }
        = "Email";

    public string? ReactionEmoji { get; set; }

    public string FilePath { get; set; } = "";
}

using System.Security.Cryptography;
using MimeKit;

public static class MimeParser
{
    public const int CurrentParserVersion = 1;

    public static async Task ParseArchiveAsync(
        string databasePath,
        string archivePath)
    {
        var messagesPath =
            Path.Combine(
                archivePath,
                "messages");

        if (!Directory.Exists(messagesPath))
        {
            Console.WriteLine(
                $"Messages directory not found: {messagesPath}");

            return;
        }

        var files =
            Directory.EnumerateFiles(
                messagesPath,
                "*.eml",
                SearchOption.AllDirectories)
            .ToList();

        Console.WriteLine();
        Console.WriteLine(
            "==========================================");

        Console.WriteLine(
            " MIME EXTRACTION - V2");

        Console.WriteLine(
            "==========================================");

        Console.WriteLine();

        Console.WriteLine(
            $"EML files found: {files.Count:N0}");

        Console.WriteLine();

        long processed = 0;
        long skipped = 0;
        long errors = 0;

        foreach (var file in files)
        {
            try
            {
                var gmailId =
                    Path.GetFileNameWithoutExtension(file)
                        .Split('_')
                        .Last();

                var message =
                    await Database
                        .GetMessageByGmailIdAsync(
                            databasePath,
                            gmailId);

                if (message == null)
                {
                    Console.WriteLine(
                        $"WARNING: No database record for:");

                    Console.WriteLine(
                        $"  {file}");

                    continue;
                }

                if (message.ParserVersion ==
                    CurrentParserVersion)
                {
                    skipped++;
                    continue;
                }

                await ParseOneAsync(
                    databasePath,
                    archivePath,
                    file,
                    message.Id);

                processed++;

                Console.Write(
                    $"\rProcessed: {processed:N0}  " +
                    $"Skipped: {skipped:N0}  " +
                    $"Errors: {errors:N0}");
            }
            catch (Exception ex)
            {
                errors++;

                Console.WriteLine();
                Console.WriteLine(
                    $"ERROR: {file}");

                Console.WriteLine(
                    $"  {ex.Message}");
            }
        }

        Console.WriteLine();

        Console.WriteLine();
        Console.WriteLine(
            "V2 extraction finished.");

        Console.WriteLine(
            $"Processed: {processed:N0}");

        Console.WriteLine(
            $"Skipped:   {skipped:N0}");

        Console.WriteLine(
            $"Errors:    {errors:N0}");
    }


    private static async Task ParseOneAsync(
        string databasePath,
        string archivePath,
        string filePath,
        long messageId)
    {
        var message =
            await Task.Run(
                () => MimeMessage.Load(filePath));

        var attachmentsPath =
            Path.Combine(
                archivePath,
                "attachments");

        Directory.CreateDirectory(
            attachmentsPath);

        var attachmentCount = 0;

        foreach (var part in
                 EnumerateParts(message.Body))
        {
            if (part is not MimePart mimePart)
            {
                continue;
            }

            var isReaction =
                string.Equals(
                    mimePart.ContentType.MimeType,
                    "text/vnd.google.email-reaction+json",
                    StringComparison.OrdinalIgnoreCase);

            if (isReaction)
            {
                await ProcessReactionAsync(
                    databasePath,
                    messageId,
                    mimePart);

                continue;
            }

            var isAttachment =
                mimePart.IsAttachment;

            var isInline =
                mimePart.IsAttachment == false &&
                !string.IsNullOrWhiteSpace(
                    mimePart.ContentId);

            /*
             * An inline MIME part is useful to us even
             * though Gmail/MimeKit doesn't classify it
             * as an attachment.
             */

            if (!isAttachment &&
                !isInline)
            {
                continue;
            }

            var result =
                await SaveAttachmentAsync(
                    attachmentsPath,
                    mimePart);

            var attachmentId =
                await Database.GetOrCreateAttachmentAsync(
                    databasePath,
                    result.Sha256,
                    result.FileName,
                    result.ContentType,
                    result.Size,
                    result.FilePath);

            await Database.AddMessageAttachmentAsync(
                databasePath,
                messageId,
                attachmentId,
                mimePart.ContentId,
                isInline);

            attachmentCount++;
        }

        await Database.MarkParsedAsync(
            databasePath,
            messageId,
            CurrentParserVersion);

        Console.Write(
            $"  attachments={attachmentCount}");
    }


    private static async Task ProcessReactionAsync(
        string databasePath,
        long messageId,
        MimePart part)
    {
        using var stream =
            new MemoryStream();

        await part.Content.DecodeToAsync(
            stream);

        stream.Position = 0;

        using var reader =
            new StreamReader(
                stream);

        var json =
            await reader.ReadToEndAsync();

        string? emoji = null;

        try
        {
            using var document =
                System.Text.Json.JsonDocument.Parse(
                    json);

            if (document.RootElement.TryGetProperty(
                    "emoji",
                    out var emojiElement))
            {
                emoji =
                    emojiElement.GetString();
            }
        }
        catch
        {
            // Leave emoji null if the JSON is malformed.
        }

        await Database.UpdateReactionAsync(
            databasePath,
            messageId,
            emoji);
    }


    private static async Task<AttachmentResult>
        SaveAttachmentAsync(
            string attachmentsPath,
            MimePart part)
    {
        var fileName =
            part.FileName;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName =
                "attachment";
        }

        fileName =
            SanitizeFileName(fileName);

        using var input =
            new MemoryStream();

        await part.Content.DecodeToAsync(
            input);

        var bytes =
            input.ToArray();

        var hash =
            SHA256.HashData(bytes);

        var sha256 =
            Convert.ToHexString(hash)
                .ToLowerInvariant();

        var extension =
            Path.GetExtension(fileName);

        var physicalFileName =
            string.IsNullOrWhiteSpace(extension)
                ? sha256
                : $"{sha256}{extension}";

        var filePath =
            Path.Combine(
                attachmentsPath,
                physicalFileName);

        if (!File.Exists(filePath))
        {
            await File.WriteAllBytesAsync(
                filePath,
                bytes);
        }

        return new AttachmentResult
        {
            Sha256 = sha256,
            FileName = fileName,
            ContentType =
                part.ContentType.MimeType,
            Size = bytes.LongLength,
            FilePath = filePath
        };
    }


    private static IEnumerable<MimeEntity>
        EnumerateParts(
            MimeEntity? entity)
    {
        if (entity == null)
        {
            yield break;
        }

        yield return entity;

        if (entity is Multipart multipart)
        {
            foreach (var child in multipart)
            {
                foreach (var descendant
                         in EnumerateParts(child))
                {
                    yield return descendant;
                }
            }
        }
    }


    private static string
        SanitizeFileName(
            string value)
    {
        foreach (var c in
                 Path.GetInvalidFileNameChars())
        {
            value =
                value.Replace(c, '_');
        }

        return value.Trim();
    }


    private sealed class AttachmentResult
    {
        public string Sha256 { get; set; } = "";

        public string FileName { get; set; } = "";

        public string ContentType { get; set; } = "";

        public long Size { get; set; }

        public string FilePath { get; set; } = "";
    }
}

using Microsoft.Data.Sqlite;
public static class Database
{
    public static async Task InitializeAsync(
    string databasePath)
    {
        var directory =
            Path.GetDirectoryName(
                Path.GetFullPath(databasePath));

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection =
            new SqliteConnection(
                $"Data Source={databasePath}");

        await connection.OpenAsync();

        // Existing V1/V1.1 migrations

        await AddColumnIfMissingAsync(
            connection,
            "Messages",
            "BccAddresses",
            "TEXT");

        await AddColumnIfMissingAsync(
            connection,
            "Messages",
            "MessageType",
            "TEXT NOT NULL DEFAULT 'Email'");

        await AddColumnIfMissingAsync(
            connection,
            "Messages",
            "ReactionEmoji",
            "TEXT");

        // V2

        await AddColumnIfMissingAsync(
            connection,
            "Messages",
            "ParsedUtc",
            "TEXT");

        await AddColumnIfMissingAsync(
            connection,
            "Messages",
            "ParserVersion",
            "INTEGER");

        var command =
            connection.CreateCommand();

        command.CommandText = """
        CREATE TABLE IF NOT EXISTS SyncState
        (
            Key TEXT PRIMARY KEY,
            Value TEXT
        );

        CREATE TABLE IF NOT EXISTS Attachments
        (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,

            Sha256 TEXT NOT NULL UNIQUE,

            FileName TEXT,

            ContentType TEXT,

            Size INTEGER NOT NULL,

            FilePath TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS MessageAttachments
        (
            MessageId INTEGER NOT NULL,

            AttachmentId INTEGER NOT NULL,

            ContentId TEXT,

            IsInline INTEGER NOT NULL DEFAULT 0,

            PRIMARY KEY
            (
                MessageId,
                AttachmentId,
                ContentId
            ),

            FOREIGN KEY
            (
                MessageId
            )
            REFERENCES Messages(Id),

            FOREIGN KEY
            (
                AttachmentId
            )
            REFERENCES Attachments(Id)
        );

        CREATE INDEX IF NOT EXISTS
            IX_Attachments_Sha256
            ON Attachments(Sha256);

        CREATE INDEX IF NOT EXISTS
            IX_MessageAttachments_AttachmentId
            ON MessageAttachments(AttachmentId);

        CREATE INDEX IF NOT EXISTS
            IX_MessageAttachments_MessageId
            ON MessageAttachments(MessageId);

        CREATE INDEX IF NOT EXISTS
            IX_Messages_ParserVersion
            ON Messages(ParserVersion);
        """;

        await command.ExecuteNonQueryAsync();
    }


    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        var command =
            connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM pragma_table_info('{table}')
            WHERE name = $column;
            """;

        command.Parameters.AddWithValue(
            "$column",
            column);

        var result =
            await command.ExecuteScalarAsync();

        var exists =
            Convert.ToInt64(result) > 0;

        if (exists)
        {
            return;
        }

        command =
            connection.CreateCommand();

        command.CommandText =
            $"ALTER TABLE {table} " +
            $"ADD COLUMN {column} {definition};";

        await command.ExecuteNonQueryAsync();
    }


    public static async Task<bool>
        MessageExistsAsync(
            string databasePath,
            string gmailId)
    {
        await using var connection =
            new SqliteConnection(
                $"Data Source={databasePath}");

        await connection.OpenAsync();

        var command =
            connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*)
            FROM Messages
            WHERE GmailId = $gmailId
            """;

        command.Parameters.AddWithValue(
            "$gmailId",
            gmailId);

        var result =
            await command.ExecuteScalarAsync();

        return Convert.ToInt64(result) > 0;
    }


    public static async Task InsertMessageAsync(
        string databasePath,
        MessageRecord message)
    {
        await using var connection =
            new SqliteConnection(
                $"Data Source={databasePath}");

        await connection.OpenAsync();

        var command =
            connection.CreateCommand();

        command.CommandText = """
            INSERT INTO Messages
            (
                GmailId,
                GmailThreadId,
                MessageId,
                InReplyTo,
                ReferencesHeader,
                Date,
                Subject,
                FromAddress,
                ToAddresses,
                CcAddresses,
                BccAddresses,
                MessageType,
                ReactionEmoji,
                FilePath,
                ImportedUtc
            )
            VALUES
            (
                $gmailId,
                $gmailThreadId,
                $messageId,
                $inReplyTo,
                $references,
                $date,
                $subject,
                $from,
                $to,
                $cc,
                $bcc,
                $messageType,
                $reactionEmoji,
                $filePath,
                $importedUtc
            );
            """;

        command.Parameters.AddWithValue(
            "$gmailId",
            message.GmailId);

        command.Parameters.AddWithValue(
            "$gmailThreadId",
            (object?)message.GmailThreadId
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$messageId",
            (object?)message.MessageId
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$inReplyTo",
            (object?)message.InReplyTo
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$references",
            (object?)message.References
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$date",
            (object?)message.Date
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$subject",
            (object?)message.Subject
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$from",
            (object?)message.From
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$to",
            (object?)message.To
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$cc",
            (object?)message.Cc
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$bcc",
            (object?)message.Bcc
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$messageType",
            message.MessageType);

        command.Parameters.AddWithValue(
            "$reactionEmoji",
            (object?)message.ReactionEmoji
                ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$filePath",
            message.FilePath);

        command.Parameters.AddWithValue(
            "$importedUtc",
            DateTimeOffset.UtcNow
                .ToString("O"));

        await command.ExecuteNonQueryAsync();
    }


    public static async Task<string?>
        GetSyncStateAsync(
            string databasePath,
            string key)
    {
        await using var connection =
            new SqliteConnection(
                $"Data Source={databasePath}");

        await connection.OpenAsync();

        var command =
            connection.CreateCommand();

        command.CommandText = """
            SELECT Value
            FROM SyncState
            WHERE Key = $key
            """;

        command.Parameters.AddWithValue(
            "$key",
            key);

        var result =
            await command.ExecuteScalarAsync();

        return result == null ||
               result == DBNull.Value
            ? null
            : result.ToString();
    }


    public static async Task SetSyncStateAsync(
        string databasePath,
        string key,
        string value)
    {
        await using var connection =
            new SqliteConnection(
                $"Data Source={databasePath}");

        await connection.OpenAsync();

        var command =
            connection.CreateCommand();

        command.CommandText = """
            INSERT INTO SyncState
                (Key, Value)
            VALUES
                ($key, $value)
            ON CONFLICT(Key)
            DO UPDATE SET
                Value = excluded.Value;
            """;

        command.Parameters.AddWithValue(
            "$key",
            key);

        command.Parameters.AddWithValue(
            "$value",
            value);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<DatabaseMessage?>
    GetMessageByGmailIdAsync(
        string databasePath,
        string gmailId)
    {
        await using var connection =
            new SqliteConnection(
                $"Data Source={databasePath}");

        await connection.OpenAsync();

        var command =
            connection.CreateCommand();

        command.CommandText = """
        SELECT
            Id,
            GmailId,
            ParserVersion
        FROM Messages
        WHERE GmailId = $gmailId;
        """;

        command.Parameters.AddWithValue(
            "$gmailId",
            gmailId);

        await using var reader =
            await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new DatabaseMessage
        {
            Id =
                reader.GetInt64(0),

            GmailId =
                reader.GetString(1),

            ParserVersion =
                reader.IsDBNull(2)
                    ? null
                    : reader.GetInt32(2)
        };
    }

    public static async Task MarkParsedAsync(
    string databasePath,
    long messageId,
    int parserVersion)
    {
        await using var connection =
            new SqliteConnection(
                $"Data Source={databasePath}");

        await connection.OpenAsync();

        var command =
            connection.CreateCommand();

        command.CommandText = """
        UPDATE Messages
        SET
            ParsedUtc = $parsedUtc,
            ParserVersion = $parserVersion
        WHERE Id = $messageId;
        """;

        command.Parameters.AddWithValue(
            "$parsedUtc",
            DateTimeOffset.UtcNow.ToString("O"));

        command.Parameters.AddWithValue(
            "$parserVersion",
            parserVersion);

        command.Parameters.AddWithValue(
            "$messageId",
            messageId);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long>
    GetOrCreateAttachmentAsync(
        string databasePath,
        string sha256,
        string fileName,
        string contentType,
        long size,
        string filePath)
    {
        await using var connection =
            new SqliteConnection(
                $"Data Source={databasePath}");

        await connection.OpenAsync();

        var command =
            connection.CreateCommand();

        command.CommandText = """
        INSERT INTO Attachments
        (
            Sha256,
            FileName,
            ContentType,
            Size,
            FilePath
        )
        VALUES
        (
            $sha256,
            $fileName,
            $contentType,
            $size,
            $filePath
        )
        ON CONFLICT(Sha256)
        DO NOTHING;

        SELECT Id
        FROM Attachments
        WHERE Sha256 = $sha256;
        """;

        command.Parameters.AddWithValue(
            "$sha256",
            sha256);

        command.Parameters.AddWithValue(
            "$fileName",
            fileName);

        command.Parameters.AddWithValue(
            "$contentType",
            contentType);

        command.Parameters.AddWithValue(
            "$size",
            size);

        command.Parameters.AddWithValue(
            "$filePath",
            filePath);

        var result =
            await command.ExecuteScalarAsync();

        return Convert.ToInt64(result);
    }

    public static async Task
    AddMessageAttachmentAsync(
        string databasePath,
        long messageId,
        long attachmentId,
        string? contentId,
        bool isInline)
    {
        await using var connection =
            new SqliteConnection(
                $"Data Source={databasePath}");

        await connection.OpenAsync();

        var command =
            connection.CreateCommand();

        command.CommandText = """
        INSERT OR IGNORE INTO
            MessageAttachments
        (
            MessageId,
            AttachmentId,
            ContentId,
            IsInline
        )
        VALUES
        (
            $messageId,
            $attachmentId,
            $contentId,
            $isInline
        );
        """;

        command.Parameters.AddWithValue(
            "$messageId",
            messageId);

        command.Parameters.AddWithValue(
            "$attachmentId",
            attachmentId);

        command.Parameters.AddWithValue(
            "$contentId",
            (object?)contentId ??
            DBNull.Value);

        command.Parameters.AddWithValue(
            "$isInline",
            isInline ? 1 : 0);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task UpdateReactionAsync(
    string databasePath,
    long messageId,
    string? emoji)
    {
        await using var connection =
            new SqliteConnection(
                $"Data Source={databasePath}");

        await connection.OpenAsync();

        var command =
            connection.CreateCommand();

        command.CommandText = """
        UPDATE Messages
        SET
            MessageType = 'Reaction',
            ReactionEmoji = $emoji
        WHERE Id = $messageId;
        """;

        command.Parameters.AddWithValue(
            "$emoji",
            (object?)emoji ??
            DBNull.Value);

        command.Parameters.AddWithValue(
            "$messageId",
            messageId);

        await command.ExecuteNonQueryAsync();
    }
}