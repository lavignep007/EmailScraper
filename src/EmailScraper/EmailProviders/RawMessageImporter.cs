using MimeKit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmailScraper.EmailProviders;

public static class RawMessageImporter
{
    public static async Task<bool> ImportAsync(
        AppConfig appConfig,
        EmailSourceConfig source,
        string providerMessageId,
        string? providerThreadId,
        byte[] rawBytes,
        bool isUnsent,
        string? graphInternetMessageIdRaw = null)
    {
        using var stream = new MemoryStream(rawBytes, writable: false);
        var mime = await MimeMessage.LoadAsync(stream);
        var from = FormatAddresses(mime.From);
        var to = FormatAddresses(mime.To);
        var cc = FormatAddresses(mime.Cc);
        var bcc = FormatAddresses(mime.Bcc);
        var messageIdRaw = ExtractRawHeader(rawBytes, "Message-ID");

        if (!IsRelevant(source.FilterAddresses, mime.From, mime.To, mime.Cc, mime.Bcc))
            return false;

        var isReaction = ContainsReactionMimeType(rawBytes);
        var messageType = isReaction ? "Reaction" : "Email";
        var date = mime.Date == DateTimeOffset.MinValue ? null : mime.Date.ToString("O");
        var year = mime.Date == DateTimeOffset.MinValue ? 0 : mime.Date.Year;
        var yearDirectory = Path.Combine(appConfig.ArchivePath, "messages", year.ToString());
        Directory.CreateDirectory(yearDirectory);

        var timestamp = mime.Date == DateTimeOffset.MinValue
            ? "unknown-date"
            : mime.Date.ToLocalTime().ToString("yyyy-MM-dd_HHmmss");
        var subject = string.IsNullOrWhiteSpace(mime.Subject) ? messageType : mime.Subject;
        var safeSubject = SanitizeFileName(subject, 100);
        var safeProviderId = SanitizeProviderId(providerMessageId);
        var desiredPath = Path.Combine(
            yearDirectory,
            $"{timestamp}_{safeSubject}_{safeProviderId}.eml");
        var filePath = await WriteUniqueAsync(desiredPath, rawBytes);

        try
        {
            await Database.InsertMessageAsync(appConfig.DatabasePath, new MessageRecord
            {
                SourceKey = source.Id,
                Provider = source.Provider,
                ProviderMessageId = providerMessageId,
                ProviderThreadId = providerThreadId,
                MessageId = NullIfWhiteSpace(mime.MessageId),
                MessageIdRaw = messageIdRaw,
                MimeMessageIdCanonical = IsValidMessageId(mime.MessageId) ? mime.MessageId : null,
                GraphInternetMessageIdRaw = graphInternetMessageIdRaw,
                InReplyTo = NullIfWhiteSpace(mime.InReplyTo),
                References = mime.References.Count == 0
                    ? null
                    : string.Join(" ", mime.References),
                Date = date,
                Subject = NullIfWhiteSpace(mime.Subject),
                SubjectDecodedExact = mime.Subject,
                SubjectSearchNormalized = NormalizeForSearch(mime.Subject),
                From = from,
                To = to,
                Cc = cc,
                Bcc = bcc,
                MessageType = messageType,
                IsUnsent = isUnsent,
                ReactionEmoji = isReaction ? TryExtractReactionEmoji(rawBytes) : null,
                FilePath = filePath
            });
        }
        catch
        {
            File.Delete(filePath);
            throw;
        }

        return true;
    }

    internal static bool IsRelevant(
        IReadOnlyCollection<string> filters,
        params InternetAddressList[] addressLists)
    {
        if (filters.Count == 0) return true;

        var normalizedFilters = filters
            .Select(NormalizeAddress)
            .Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return addressLists
            .SelectMany(x => x.Mailboxes)
            .Select(x => NormalizeAddress(x.Address))
            .Any(normalizedFilters.Contains);
    }

    private static async Task<string> WriteUniqueAsync(string desiredPath, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(desiredPath)!;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);

        for (var suffix = 0; ; suffix++)
        {
            var fileName = suffix == 0
                ? stem + extension
                : $"{stem}_{suffix}{extension}";
            var candidate = Path.Combine(directory, fileName);

            try
            {
                await using var output = new FileStream(
                    candidate,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    useAsync: true);
                await output.WriteAsync(bytes);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // A different message already owns this display filename.
            }
        }
    }

    private static string SanitizeProviderId(string providerMessageId)
    {
        var sanitized = SanitizeFileName(providerMessageId, 64);
        if (string.Equals(sanitized, providerMessageId, StringComparison.Ordinal) &&
            providerMessageId.Length <= 64)
            return sanitized;

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(providerMessageId)))[..10].ToLowerInvariant();
        return $"{sanitized}_{hash}";
    }

    private static string SanitizeFileName(string value, int maximumLength)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
            value = value.Replace(character, '_');

        value = value.Replace('\r', ' ').Replace('\n', ' ');
        while (value.Contains("  ")) value = value.Replace("  ", " ");
        value = value.Trim().TrimEnd('.');
        if (value.Length == 0) value = "message";
        return value.Length > maximumLength ? value[..maximumLength] : value;
    }

    private static string NormalizeAddress(string value) => value.Trim().ToLowerInvariant();

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? FormatAddresses(InternetAddressList addresses) =>
        addresses.Count == 0
            ? null
            : string.Join(", ", addresses.Mailboxes.Select(FormatMailbox));

    private static string FormatMailbox(MailboxAddress mailbox) =>
        string.IsNullOrWhiteSpace(mailbox.Name)
            ? mailbox.Address
            : $"{mailbox.Name} <{mailbox.Address}>";

    private static bool ContainsReactionMimeType(byte[] raw) =>
        Encoding.UTF8.GetString(raw).Contains(
            "text/vnd.google.email-reaction+json",
            StringComparison.OrdinalIgnoreCase);

    private static string? TryExtractReactionEmoji(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        const string marker = "\"emoji\":\"";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) return null;

        index += marker.Length;
        var end = text.IndexOf('"', index);
        if (end < 0) return null;

        try
        {
            return JsonSerializer.Deserialize<string>($"\"{text[index..end]}\"");
        }
        catch (JsonException)
        {
            return text[index..end];
        }
    }

    private static string? ExtractRawHeader(byte[] bytes, string headerName)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var header = headerName + ":";
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            var line = text[start..end].TrimEnd('\r');
            if (line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
                return line[header.Length..].Trim();
            if (line.Length == 0) break;
            start = end + 1;
        }
        return null;
    }

    private static bool IsValidMessageId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Count(c => c == '@') == 1 &&
        !value.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or ';' or ',' or ':' or '/');

    private static string? NormalizeForSearch(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Normalize(NormalizationForm.FormC).ToLowerInvariant();
}
