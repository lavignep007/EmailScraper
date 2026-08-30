using MimeKit;
using System.Security.Cryptography;
using System.Text;

namespace EmailScraper.Archive;

public static class MimeParser
{
    public const int CurrentParserVersion = 4;

    public static async Task ParseArchiveAsync(
        string databasePath,
        string archivePath)
    {
        var messages = await Database.GetMessagesForEmlValidationAsync(databasePath);

        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" MIME EXTRACTION - V2");
        Console.WriteLine("==========================================");
        Console.WriteLine();
        Console.WriteLine($"Messages found: {messages.Count:N0}");
        Console.WriteLine();

        long processed = 0;
        long skipped = 0;
        long errors = 0;

        foreach (var message in messages)
        {
            var file = message.FilePath;

            try
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    Console.WriteLine($"WARNING: EML not found for {message.SourceKey}/" +
                        $"{message.ProviderMessageId}: {file}");
                    errors++;
                    continue;
                }

                if (message.ParserVersion == CurrentParserVersion)
                {
                    skipped++;
                    continue;
                }

                await ParseOneAsync(databasePath, archivePath, file, message.Id);

                processed++;

                Console.Write($"\rProcessed: {processed:N0}  Skipped: {skipped:N0}  Errors: {errors:N0}");
            }
            catch (Exception ex)
            {
                errors++;

                Console.WriteLine();
                Console.WriteLine($"ERROR: {file}");
                Console.WriteLine($"  {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("V2 extraction finished.");
        Console.WriteLine($"Processed: {processed:N0}");
        Console.WriteLine($"Skipped:   {skipped:N0}");
        Console.WriteLine($"Errors:    {errors:N0}");
    }

    private static async Task ParseOneAsync(
        string databasePath,
        string archivePath,
        string filePath,
        long messageId)
    {
        var rawBytes = await File.ReadAllBytesAsync(filePath);
        var message = await Task.Run(() => MimeMessage.Load(new MemoryStream(rawBytes)));
        var messageIdRaw = ExtractRawHeader(rawBytes, "Message-ID");

        var metadata = new MessageRecord
        {
            MessageId = message.MessageId,
            MessageIdRaw = messageIdRaw,
            MimeMessageIdCanonical = IsValidMessageId(message.MessageId) ? message.MessageId : null,
            InReplyTo = message.InReplyTo,
            References = message.References.Count == 0 ? null : string.Join(" ", message.References),
            Date = message.Date.ToString("O"),
            Subject = string.IsNullOrWhiteSpace(message.Subject) ? null : message.Subject,
            SubjectDecodedExact = message.Subject,
            SubjectSearchNormalized = NormalizeForSearch(message.Subject),
            From = FormatAddresses(message.From),
            To = FormatAddresses(message.To),
            Cc = FormatAddresses(message.Cc),
            Bcc = FormatAddresses(message.Bcc)
        };

        await Database.UpdateMessageMetadataAsync(databasePath, messageId, metadata);
        // Rebuild occurrence-level MIME classifications and filenames from the
        // immutable EML. Blob rows remain deduplicated and untouched.
        await Database.ClearMessageAttachmentsAsync(databasePath, messageId);

        var attachmentsPath = Path.Combine(archivePath, "attachments");

        Directory.CreateDirectory(attachmentsPath);

        var attachmentCount = 0;

        foreach (var part in EnumerateParts(message.Body))
        {
            if (part is not MimePart mimePart)
                continue;

            var isReaction = string.Equals(mimePart.ContentType.MimeType, "text/vnd.google.email-reaction+json",
                StringComparison.OrdinalIgnoreCase);

            if (isReaction)
            {
                await ProcessReactionAsync(databasePath, messageId, mimePart);

                continue;
            }

            var hasFileName = !string.IsNullOrWhiteSpace(mimePart.FileName);
            var dispositionInline = string.Equals(
                mimePart.ContentDisposition?.Disposition, "inline", StringComparison.OrdinalIgnoreCase);
            var isInline = dispositionInline || !string.IsNullOrWhiteSpace(mimePart.ContentId);
            var isBodyAlternative = mimePart is TextPart &&
                !hasFileName && !mimePart.IsAttachment &&
                (string.Equals(mimePart.ContentType.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mimePart.ContentType.MimeType, "text/html", StringComparison.OrdinalIgnoreCase));
            var isAttachment = !dispositionInline && (mimePart.IsAttachment || hasFileName);

            /*
             * An inline MIME part is useful to us even
             * though Gmail/MimeKit doesn't classify it
             * as an attachment.
             */

            if (isBodyAlternative || (!isAttachment && !isInline))
                continue;

            var result = await SaveAttachmentAsync(attachmentsPath, mimePart);

            var relativePath = Path.GetRelativePath(archivePath, result.FilePath).Replace('\\', '/');
            var attachmentId = await Database.GetOrCreateAttachmentAsync(databasePath, result.Sha256,
                result.FileName, result.ContentType, result.Size, result.FilePath, relativePath);

            await Database.AddMessageAttachmentAsync(databasePath, messageId, attachmentId,
                mimePart.ContentId, isInline, result.FileName,
                mimePart.ContentDisposition?.ToString(), null,
                isInline
                    ? (string.IsNullOrWhiteSpace(mimePart.ContentId)
                        ? "InlineResourceUnreferenced"
                        : "InlineResource")
                    : "Attachment");

            attachmentCount++;
        }

        await Database.MarkParsedAsync(databasePath, messageId, CurrentParserVersion);

        Console.Write($"  attachments={attachmentCount}");
    }

    private static async Task ProcessReactionAsync(
        string databasePath,
        long messageId,
        MimePart part)
    {
        using var stream = new MemoryStream();

        await part.Content.DecodeToAsync(stream);

        stream.Position = 0;

        using var reader = new StreamReader(stream);

        var json = await reader.ReadToEndAsync();

        string? emoji = null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("emoji", out var emojiElement))
                emoji = emojiElement.GetString();
        }
        catch
        {
            // Leave emoji null if the JSON is malformed.
        }

        await Database.UpdateReactionAsync(databasePath, messageId, emoji);
    }

    private static async Task<AttachmentResult> SaveAttachmentAsync(
        string attachmentsPath,
        MimePart part)
    {
        var fileName = part.FileName;

        if (string.IsNullOrWhiteSpace(fileName)) fileName = "attachment";

        fileName = SanitizeFileName(fileName);

        using var input = new MemoryStream();

        await part.Content.DecodeToAsync(input);

        var bytes = input.ToArray();
        var hash = SHA256.HashData(bytes);
        var sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        // The physical blob name must be content-addressed, not occurrence-addressed.
        // Derive the extension from the MIME type so two filenames for the same
        // bytes always resolve to the same path.
        var extension = ExtensionForContentType(part.ContentType.MimeType);
        var physicalFileName = string.IsNullOrWhiteSpace(extension) ? sha256 : $"{sha256}{extension}";
        var filePath = Path.Combine(attachmentsPath, physicalFileName);

        if (!File.Exists(filePath))
            await File.WriteAllBytesAsync(filePath, bytes);

        return new AttachmentResult
        {
            Sha256 = sha256,
            FileName = fileName,
            ContentType = part.ContentType.MimeType,
            Size = bytes.LongLength,
            FilePath = filePath
        };
    }

    private static IEnumerable<MimeEntity> EnumerateParts(
        MimeEntity? entity)
    {
        if (entity == null) yield break;

        yield return entity;

        if (entity is Multipart multipart)
            foreach (var child in multipart)
                foreach (var descendant in EnumerateParts(child))
                    yield return descendant;
    }

    private static string SanitizeFileName(
        string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');

        return value.Trim();
    }

    private static string ExtensionForContentType(string? contentType) =>
        contentType?.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "text/plain" => ".txt",
            "text/html" => ".html",
            "application/zip" => ".zip",
            _ => ".bin"
        };

    private static string? ExtractRawHeader(byte[] bytes, string headerName)
    {
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var header = headerName + ":";
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            var line = text[start..end].TrimEnd('\r');
            if (line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[header.Length..];
                var next = end + 1;
                while (next < text.Length && (text[next] == ' ' || text[next] == '\t'))
                {
                    var continuationEnd = text.IndexOf('\n', next);
                    if (continuationEnd < 0) continuationEnd = text.Length;
                    value += "\r\n" + text[next..continuationEnd].TrimEnd('\r');
                    next = continuationEnd + 1;
                }
                return value.Trim();
            }
            if (line.Length == 0) break;
            start = end + 1;
        }
        return null;
    }

    private static bool IsValidMessageId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Count(c => c == '@') == 1 &&
        value.Contains('@') &&
        !value.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or ';' or ',' or ':' or '/');

    private static string? NormalizeForSearch(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Normalize(NormalizationForm.FormC).ToLowerInvariant();

    private static string? FormatAddresses(
        InternetAddressList? addresses)
    {
        if (addresses == null || addresses.Count == 0) return null;

        return string.Join(", ", addresses.Mailboxes.Select(FormatMailbox));
    }

    private static string FormatMailbox(
        MailboxAddress mailbox)
    {
        if (string.IsNullOrWhiteSpace(mailbox.Name)) return mailbox.Address;

        return $"{mailbox.Name} <{mailbox.Address}>";
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
