using MimeKit;
using System.Security.Cryptography;

public static class MimeParser
{
    public const int CurrentParserVersion = 2;

    public static async Task ParseArchiveAsync(
        string databasePath,
        string archivePath)
    {
        var messagesPath = Path.Combine(archivePath, "messages");

        if (!Directory.Exists(messagesPath))
        {
            Console.WriteLine($"Messages directory not found: {messagesPath}");
            return;
        }

        var files = Directory
            .EnumerateFiles(messagesPath, "*.eml", SearchOption.AllDirectories)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" MIME EXTRACTION - V2");
        Console.WriteLine("==========================================");
        Console.WriteLine();
        Console.WriteLine($"EML files found: {files.Count:N0}");
        Console.WriteLine();

        long processed = 0;
        long skipped = 0;
        long errors = 0;

        foreach (var file in files)
        {
            try
            {
                var gmailId = Path
                    .GetFileNameWithoutExtension(file)
                    .Split('_')
                    .Last();

                var message = await Database.GetMessageByGmailIdAsync(databasePath, gmailId);

                if (message == null)
                {
                    Console.WriteLine("WARNING: No database record for:");
                    Console.WriteLine($"  {file}");

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
        var message = await Task.Run(() => MimeMessage.Load(filePath));

        var metadata = new MessageRecord
        {
            MessageId = message.MessageId,
            InReplyTo = message.InReplyTo,
            References = message.References.Count == 0 ? null : string.Join(" ", message.References),
            Date = message.Date.ToString("O"),
            Subject = string.IsNullOrWhiteSpace(message.Subject) ? null : message.Subject,
            From = FormatAddresses(message.From),
            To = FormatAddresses(message.To),
            Cc = FormatAddresses(message.Cc),
            Bcc = FormatAddresses(message.Bcc)
        };

        await Database.UpdateMessageMetadataAsync(databasePath, messageId, metadata);

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

            var isAttachment = mimePart.IsAttachment;
            var isInline = !mimePart.IsAttachment && !string.IsNullOrWhiteSpace(mimePart.ContentId);

            /*
             * An inline MIME part is useful to us even
             * though Gmail/MimeKit doesn't classify it
             * as an attachment.
             */

            if (!isAttachment &&
                !isInline)
                continue;

            var result = await SaveAttachmentAsync(attachmentsPath, mimePart);

            var attachmentId = await Database.GetOrCreateAttachmentAsync(databasePath, result.Sha256,
                result.FileName, result.ContentType, result.Size, result.FilePath);

            await Database.AddMessageAttachmentAsync(databasePath, messageId, attachmentId,
                mimePart.ContentId, isInline);

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
        var extension = Path.GetExtension(fileName);
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
