using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EmailScraper.Archive;

public static class ArchiveManifestGenerator
{
    public const string ScraperVersion = "0.3.0";
    public const int SchemaVersion = 4;

    public static async Task WriteAsync(
        string databasePath,
        string archivePath,
        AppConfig config,
        IReadOnlyCollection<SourceRun> runs)
    {
        // Microsoft.Data.Sqlite may keep disposed connections in its pool. On
        // Windows those pooled handles can prevent File.OpenRead's default
        // sharing mode from opening the database for hashing.
        SqliteConnection.ClearAllPools();
        var databaseSha256 = await HashFileAsync(databasePath);

        var manifest = new ArchiveManifest
        {
            ScraperVersion = ScraperVersion,
            SchemaVersion = SchemaVersion,
            ParserVersion = MimeParser.CurrentParserVersion,
            GeneratedUtc = DateTimeOffset.UtcNow,
            TimeZone = TimeZoneInfo.Local.Id,
            Sources = runs.ToDictionary(x => x.SourceKey, StringComparer.OrdinalIgnoreCase),
            Counts = new ArchiveCounts
            {
                Messages = await ScalarAsync(databasePath, "SELECT COUNT(*) FROM Messages"),
                Attachments = await ScalarAsync(databasePath, "SELECT COUNT(*) FROM Attachments"),
                Threads = await ScalarAsync(databasePath, "SELECT COUNT(*) FROM Threads WHERE State = 'Active'"),
                EmlFiles = CountFiles(Path.Combine(archivePath, "messages"), "*.eml"),
                MessagePdfs = CountFiles(Path.Combine(archivePath, "pdf", "messages"), "*.pdf"),
                ThreadPdfs = CountFiles(Path.Combine(archivePath, "pdf", "threads"), "*.pdf")
            },
            DatabaseSha256 = databaseSha256,
            StableSets = await BuildStableSetsAsync(databasePath),
            Integrity = new IntegrityResult
            {
                QuickCheck = await ScalarTextAsync(databasePath, "PRAGMA quick_check"),
                ForeignKeyErrors = await CountForeignKeyErrorsAsync(databasePath),
                CoverageErrors = await ScalarAsync(databasePath, "SELECT COUNT(*) FROM Messages WHERE FilePath IS NULL OR TRIM(FilePath) = ''")
            },
            Capabilities = new Capabilities
            {
                GraphInternetMessageIdCaptured = await ScalarAsync(databasePath,
                    "SELECT COUNT(*) FROM Messages WHERE Provider IN ('Microsoft365', 'Outlook', 'Office365') AND GraphInternetMessageIdRaw IS NOT NULL AND TRIM(GraphInternetMessageIdRaw) <> ''") > 0,
                ProviderAttachmentIdCaptured = await ScalarAsync(databasePath,
                    "SELECT COUNT(*) FROM MessageAttachments WHERE ProviderAttachmentId IS NOT NULL AND TRIM(ProviderAttachmentId) <> ''") > 0
            }
        };

        await File.WriteAllTextAsync(
            Path.Combine(archivePath, "archive-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<StableSets> BuildStableSetsAsync(string databasePath)
    {
        var provider = new List<string>();
        var messageIds = new List<string>();
        var attachments = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceKey, Provider, ProviderMessageId, MessageIdRaw FROM Messages ORDER BY SourceKey, ProviderMessageId;";
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                provider.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
                if (!reader.IsDBNull(3)) messageIds.Add(reader.GetString(3));
            }
        command = connection.CreateCommand();
        command.CommandText = "SELECT Sha256 FROM Attachments ORDER BY Sha256;";
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) attachments.Add(reader.GetString(0));
        return new StableSets
        {
            ProviderIdsSha256 = HashLines(provider),
            MimeMessageIdsSha256 = HashLines(messageIds),
            AttachmentSha256 = HashLines(attachments)
        };
    }

    private static async Task<int> CountForeignKeyErrorsAsync(string databasePath)
    {
        var count = 0;
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) count++;
        return count;
    }

    private static async Task<long> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarTextAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        var command = connection.CreateCommand(); command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString() ?? "unknown";
    }

    private static int CountFiles(string path, string pattern) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories).Count() : 0;

    private static async Task<string> HashFileAsync(string path)
    {
        const int maxAttempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
              await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1024 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                // A provider, antivirus scanner, or SQLite handle may release
                // the file shortly after the first sharing violation.
                SqliteConnection.ClearAllPools();
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    private static string HashLines(IEnumerable<string> values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values.OrderBy(x => x, StringComparer.Ordinal))))).ToLowerInvariant();

    public sealed record SourceRun(string SourceKey, string Provider, string MailboxAddress, List<string> FilterAddresses, string Mode,
        string? CheckpointBefore, string? CheckpointAfter, List<string> Added, List<string> Modified, List<string> Ignored, List<string> Deleted);

    private sealed class ArchiveManifest
    {
        public string ScraperVersion { get; set; } = ""; public int SchemaVersion { get; set; }
        public int ParserVersion { get; set; } public DateTimeOffset GeneratedUtc { get; set; }
        public string TimeZone { get; set; } = ""; public Dictionary<string, SourceRun> Sources { get; set; } = [];
        public ArchiveCounts Counts { get; set; } = new(); public string DatabaseSha256 { get; set; } = "";
        public StableSets StableSets { get; set; } = new(); public IntegrityResult Integrity { get; set; } = new();
        public Capabilities Capabilities { get; set; } = new();
    }
    private sealed class ArchiveCounts { public long Messages { get; set; } public long Attachments { get; set; } public long Threads { get; set; } public int EmlFiles { get; set; } public int MessagePdfs { get; set; } public int ThreadPdfs { get; set; } }
    private sealed class StableSets { public string ProviderIdsSha256 { get; set; } = ""; public string MimeMessageIdsSha256 { get; set; } = ""; public string AttachmentSha256 { get; set; } = ""; }
    private sealed class IntegrityResult { public string QuickCheck { get; set; } = ""; public int ForeignKeyErrors { get; set; } public long CoverageErrors { get; set; } }
    private sealed class Capabilities { public bool GraphInternetMessageIdCaptured { get; set; } public bool ProviderAttachmentIdCaptured { get; set; } }
}
