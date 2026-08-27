using MimeKit;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmailScraper.Archive;

public static class PerspectiveArchiveBuilder
{
    private const int ProjectionVersion = 1;
    private const string ManifestFileName = "perspective-manifest.json";

    public static async Task BuildAsync(
        string sourceDatabasePath,
        string sourceArchivePath,
        PerspectiveArchiveConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ArchivePath))
            throw new InvalidOperationException($"Perspective archive '{config.Name}' has no ArchivePath.");

        var addresses = config.EmailAddresses
            .Select(NormalizeAddress)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (addresses.Count == 0)
        {
            Console.WriteLine($"Perspective archive '{config.Name}' has no email addresses; skipping.");
            return;
        }

        var sourceRoot = NormalizeRoot(sourceArchivePath);
        var targetRoot = NormalizeRoot(config.ArchivePath);
        ValidateDistinctRoots(sourceRoot, targetRoot);

        var sourceMessages = await Database.GetMessagesForProjectionAsync(sourceDatabasePath);
        var addressEligible = sourceMessages
            .Where(x => IsVisibleTo(x, addresses))
            .ToList();
        var retained = addressEligible
            .Where(x => !x.IsUnsent)
            .ToList();
        var excludedByAddress = sourceMessages.Count - addressEligible.Count;
        var excludedAsUnsent = addressEligible.Count - retained.Count;
        var fingerprint = BuildFingerprint(retained, addresses);

        if (await IsCurrentAsync(targetRoot, fingerprint))
        {
            Console.WriteLine();
            Console.WriteLine($"Perspective archive '{config.Name}' is already current.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine($" PERSPECTIVE ARCHIVE - {config.Name}");
        Console.WriteLine("==========================================");
        Console.WriteLine();
        Console.WriteLine($"Perspective addresses: {string.Join(", ", addresses)}");
        Console.WriteLine($"Source messages:       {sourceMessages.Count:N0}");
        Console.WriteLine($"Messages retained:     {retained.Count:N0}");
        Console.WriteLine($"Excluded by address:   {excludedByAddress:N0}");
        Console.WriteLine($"Excluded as unsent:    {excludedAsUnsent:N0}");

        var stagingRoot = targetRoot + ".building";
        var previousRoot = targetRoot + ".previous";
        DeleteWorkingDirectory(stagingRoot, targetRoot, ".building");
        Directory.CreateDirectory(Path.Combine(stagingRoot, "messages"));
        var targetDatabasePath = Path.Combine(stagingRoot, "archive.db");

        try
        {
            await Database.InitializeAsync(targetDatabasePath);
            await CopyMessagesAsync(sourceRoot, stagingRoot, targetDatabasePath, retained);
            await MimeParser.ParseArchiveAsync(targetDatabasePath, stagingRoot);
            await ThreadBuilder.BuildAsync(targetDatabasePath);
            await ArchiveOrganizer.RunAsync(targetDatabasePath, stagingRoot);
            await PdfGenerator.GenerateAsync(targetDatabasePath, stagingRoot);
            await SearchIndexer.BuildAsync(targetDatabasePath);
            await Database.RebaseArchiveFilePathsAsync(targetDatabasePath, stagingRoot, targetRoot);

            var manifest = new PerspectiveManifest
            {
                ProjectionVersion = ProjectionVersion,
                Name = config.Name,
                EmailAddresses = addresses,
                Fingerprint = fingerprint,
                SourceMessages = sourceMessages.Count,
                RetainedMessages = retained.Count,
                ExcludedByAddress = excludedByAddress,
                ExcludedAsUnsent = excludedAsUnsent,
                GeneratedUtc = DateTimeOffset.UtcNow
            };
            await File.WriteAllTextAsync(
                Path.Combine(stagingRoot, ManifestFileName),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            SqliteConnection.ClearAllPools();
            ReplaceTarget(stagingRoot, targetRoot, previousRoot);
            Console.WriteLine($"Perspective archive complete: {targetRoot}");
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            DeleteWorkingDirectory(stagingRoot, targetRoot, ".building");
            throw;
        }
    }

    internal static bool IsVisibleTo(
        ProjectionMessage message,
        IReadOnlyCollection<string> addresses)
    {
        var participants = EnumerateAddresses(message.From)
            .Concat(EnumerateAddresses(message.To))
            .Concat(EnumerateAddresses(message.Cc))
            .Concat(EnumerateAddresses(message.Bcc))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return addresses.Any(participants.Contains);
    }

    private static IEnumerable<string> EnumerateAddresses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;

        InternetAddressList parsed;

        try
        {
            parsed = InternetAddressList.Parse(value);
        }
        catch (ParseException)
        {
            yield break;
        }

        foreach (var mailbox in parsed.Mailboxes)
            yield return NormalizeAddress(mailbox.Address);
    }

    private static string NormalizeAddress(string value) => value.Trim().ToLowerInvariant();

    private static string NormalizeRoot(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string BuildFingerprint(
        IReadOnlyCollection<ProjectionMessage> messages,
        IReadOnlyCollection<string> addresses)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append($"version:{ProjectionVersion}\n");

        foreach (var address in addresses) Append($"address:{address}\n");

        foreach (var message in messages.OrderBy(x => x.ProviderMessageId, StringComparer.Ordinal))
        {
            var file = new FileInfo(message.FilePath);
            Append($"message:{message.ProviderMessageId}|{file.Length}|{file.LastWriteTimeUtc.Ticks}\n");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value));
    }

    private static async Task<bool> IsCurrentAsync(string targetRoot, string fingerprint)
    {
        var manifestPath = Path.Combine(targetRoot, ManifestFileName);
        var databasePath = Path.Combine(targetRoot, "archive.db");

        if (!File.Exists(manifestPath) || !File.Exists(databasePath)) return false;

        try
        {
            var manifest = JsonSerializer.Deserialize<PerspectiveManifest>(
                await File.ReadAllTextAsync(manifestPath));
            return manifest?.ProjectionVersion == ProjectionVersion &&
                string.Equals(manifest.Fingerprint, fingerprint, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task CopyMessagesAsync(
        string sourceRoot,
        string targetRoot,
        string targetDatabasePath,
        IReadOnlyCollection<ProjectionMessage> messages)
    {
        var sourceMessagesRoot = Path.GetFullPath(Path.Combine(sourceRoot, "messages"));
        var copied = 0;

        foreach (var message in messages)
        {
            var sourcePath = Path.GetFullPath(message.FilePath);

            if (!IsWithin(sourcePath, sourceMessagesRoot))
                throw new InvalidOperationException($"Message file is outside the source messages directory: {sourcePath}");
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Source EML file not found.", sourcePath);

            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var targetPath = Path.Combine(targetRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath);

            await Database.InsertMessageAsync(targetDatabasePath, new MessageRecord
            {
                ProviderMessageId = message.ProviderMessageId,
                ProviderThreadId = message.ProviderThreadId,
                MessageId = message.MessageId,
                InReplyTo = message.InReplyTo,
                References = message.References,
                Date = message.Date,
                Subject = message.Subject,
                From = message.From,
                To = message.To,
                Cc = message.Cc,
                Bcc = message.Bcc,
                MessageType = message.MessageType,
                IsUnsent = message.IsUnsent,
                ReactionEmoji = message.ReactionEmoji,
                FilePath = targetPath
            });

            copied++;
            Console.Write($"\rCopied EML files: {copied:N0}/{messages.Count:N0}");
        }

        Console.WriteLine();
    }

    private static void ReplaceTarget(string stagingRoot, string targetRoot, string previousRoot)
    {
        DeleteWorkingDirectory(previousRoot, targetRoot, ".previous");

        if (Directory.Exists(targetRoot)) Directory.Move(targetRoot, previousRoot);

        try
        {
            Directory.Move(stagingRoot, targetRoot);
        }
        catch
        {
            if (!Directory.Exists(targetRoot) && Directory.Exists(previousRoot))
                Directory.Move(previousRoot, targetRoot);
            throw;
        }

        DeleteWorkingDirectory(previousRoot, targetRoot, ".previous");
    }

    private static void DeleteWorkingDirectory(string path, string targetRoot, string requiredSuffix)
    {
        var fullPath = Path.GetFullPath(path);

        if (!fullPath.Equals(targetRoot + requiredSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected perspective cleanup path: {fullPath}");

        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private static void ValidateDistinctRoots(string sourceRoot, string targetRoot)
    {
        if (targetRoot.Equals(Path.GetPathRoot(targetRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A filesystem root cannot be used as a perspective archive path.");

        if (sourceRoot.Equals(targetRoot, StringComparison.OrdinalIgnoreCase) ||
            IsWithin(targetRoot, sourceRoot) || IsWithin(sourceRoot, targetRoot))
            throw new InvalidOperationException(
                "A perspective archive path must be separate from, and not nested within, the source archive path.");
    }

    private static bool IsWithin(string candidate, string directory)
    {
        var relative = Path.GetRelativePath(directory, candidate);
        return relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private sealed class PerspectiveManifest
    {
        public int ProjectionVersion { get; set; }
        public string Name { get; set; } = "";
        public List<string> EmailAddresses { get; set; } = [];
        public string Fingerprint { get; set; } = "";
        public int SourceMessages { get; set; }
        public int RetainedMessages { get; set; }
        public int ExcludedByAddress { get; set; }
        public int ExcludedAsUnsent { get; set; }
        public DateTimeOffset GeneratedUtc { get; set; }
    }
}
