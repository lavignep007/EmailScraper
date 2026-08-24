public static class DisplayNameBuilder
{
    public const string MyEmail = "lavignep007@gmail.com";

    public static string Build(
        ThreadMessage message)
    {
        var date = GetDisplayDate(message.Date);
        var from = GetPersonName(message.From);
        var to = GetMainRecipient(message.To);
        var subject = CleanSubject(message.Subject);

        return $"{date} - {from} → {to} - {subject}";
    }

    private static string GetDisplayDate(
        string? value)
    {
        if (DateTimeOffset.TryParse(value, out var date))
            return date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        return "Unknown date";
    }

    private static string GetMainRecipient(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unknown";

        var recipients = SplitAddresses(value);

        if (recipients.Count == 0) return "Unknown";

        /*
         * Prefer someone other than "me".
         */
        var recipient = recipients.FirstOrDefault(x => !IsMe(ExtractEmail(x)));

        return GetPersonName(recipient ?? recipients[0]);
    }

    private static string GetPersonName(
        string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "Unknown";

        /*
         * Handle:
         *
         * Jimmy Belony <jimmy@example.com>
         *
         * and:
         *
         * jimmy@example.com
         */
        var start = address.IndexOf('<');
        var end = address.IndexOf('>');

        if (start >= 0 &&
            end > start)
        {
            var name = address[..start]
                .Trim()
                .Trim('"');

            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        var email = ExtractEmail(address);

        if (string.IsNullOrWhiteSpace(email)) return address.Trim();

        /*
         * No display name available.
         *
         * Use the part before @ as requested.
         */
        var at = email.IndexOf('@');

        if (at > 0) return email[..at];

        return email;
    }

    private static string ExtractEmail(
        string value)
    {
        var start = value.IndexOf('<');
        var end = value.IndexOf('>');

        if (start >= 0 &&
            end > start)
            return value[(start + 1)..end]
                .Trim()
                .ToLowerInvariant();

        return value
            .Trim()
            .ToLowerInvariant();
    }

    private static bool IsMe(
        string email)
    {
        return string.Equals(email, MyEmail, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitAddresses(
        string value)
    {
        return value
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();
    }

    private static string CleanSubject(
        string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "(no subject)";

        var result = subject.Trim();

        while (true)
        {
            var upper = result.ToUpperInvariant();

            if (upper.StartsWith("RE:"))
            {
                result = result[3..].Trim();
                continue;
            }

            if (upper.StartsWith("FW:"))
            {
                result = result[3..].Trim();
                continue;
            }

            if (upper.StartsWith("FWD:"))
            {
                result = result[4..].Trim();
                continue;
            }

            break;
        }

        return result;
    }
}
