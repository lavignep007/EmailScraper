using EmailScraper.Tests.Data;
using Microsoft.Data.Sqlite;
using QuestPDF.Infrastructure;

namespace EmailScraper.Tests.Archive;

public sealed class PerspectiveArchiveBuilderTests
{
    [Fact]
    public async Task Delivery_state_reconciliation_repairs_false_negatives_and_sent_transitions()
    {
        await using var source = await TestDatabase.CreateAsync();
        var sourceMessagesPath = Path.Combine(source.Directory, "messages");
        Directory.CreateDirectory(sourceMessagesPath);

        await AddSourceMessageAsync(source.Path, sourceMessagesPath,
            "previously-unsent", "a@example.test", "b@example.test", null,
            isUnsent: true);
        await AddSourceMessageAsync(source.Path, sourceMessagesPath,
            "missed-scheduled", "a@example.test", "b@example.test", null);
        await AddSourceMessageAsync(source.Path, sourceMessagesPath,
            "other-source-unsent", "a@example.test", "b@example.test", null,
            isUnsent: true,
            sourceKey: "other-source");

        var changes = await Database.ReconcileUnsentMessagesAsync(
            source.Path,
            "test-source",
            new HashSet<string>(["missed-scheduled", "not-in-this-archive"]));

        changes.CurrentlyUnsent.Should().Be(1);
        changes.NewlyUnsent.Should().Be(1);
        changes.NewlyDelivered.Should().Be(1);
        (await ReadValuesAsync(source.Path, """
            SELECT SourceKey || ':' || ProviderMessageId || ':' || IsUnsent
            FROM Messages
            ORDER BY SourceKey, ProviderMessageId;
            """))
            .Should().Equal(
                "other-source:other-source-unsent:1",
                "test-source:missed-scheduled:1",
                "test-source:previously-unsent:0");
    }

    [Fact]
    public async Task Projection_keeps_exact_alias_matches_and_omits_excluded_attachments()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        await using var source = await TestDatabase.CreateAsync();
        var sourceMessagesPath = Path.Combine(source.Directory, "messages");
        var targetPath = Path.Combine(Path.GetTempPath(), "EmailScraper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceMessagesPath);

        try
        {
            await AddSourceMessageAsync(source.Path, sourceMessagesPath,
                "kept-one", "a@example.test", "Opponent <b1@example.test>, c@example.test", "public.txt");
            await AddSourceMessageAsync(source.Path, sourceMessagesPath,
                "excluded-secret", "c@example.test", "a@example.test", "secret.txt");
            await AddSourceMessageAsync(source.Path, sourceMessagesPath,
                "kept-alias", "c@example.test", "b2@example.test", null);
            await AddSourceMessageAsync(source.Path, sourceMessagesPath,
                "excluded-substring", "c@example.test", "notb1@example.test", null);
            await AddSourceMessageAsync(source.Path, sourceMessagesPath,
                "excluded-unsent", "a@example.test", "b1@example.test", "scheduled-secret.txt",
                isUnsent: true);
            await AddSourceMessageAsync(source.Path, sourceMessagesPath,
                "excluded-other-source", "a@example.test", "b1@example.test", "other-source.txt",
                sourceKey: "syndicate-source");

            await EmailScraper.Archive.PerspectiveArchiveBuilder.BuildAsync(
                source.Path,
                source.Directory,
                new PerspectiveArchiveConfig
                {
                    Name = "Moriarty",
                    ArchivePath = targetPath + Path.DirectorySeparatorChar
                },
                [new PerspectiveSourceRule(
                    "test-source", ["b1@example.test", "B2@example.test"])]);

            var targetDatabase = Path.Combine(targetPath, "archive.db");
            (await ReadValuesAsync(targetDatabase, "SELECT ProviderMessageId FROM Messages ORDER BY ProviderMessageId;"))
                .Should().Equal("kept-alias", "kept-one");
            (await ReadValuesAsync(targetDatabase, "SELECT FileName FROM Attachments ORDER BY FileName;"))
                .Should().Equal("public.txt");
            var storedPaths = await ReadValuesAsync(targetDatabase,
                "SELECT FilePath FROM Messages UNION ALL SELECT FilePath FROM Attachments;");
            storedPaths.Should().OnlyContain(path => path.StartsWith(targetPath, StringComparison.OrdinalIgnoreCase));
            storedPaths.Should().OnlyContain(path => File.Exists(path));
            Directory.EnumerateFiles(Path.Combine(targetPath, "pdf", "messages"), "*.pdf")
                .Should().HaveCount(2);
            File.Exists(Path.Combine(targetPath, "perspective-manifest.json")).Should().BeTrue();

            var manifestBefore = await File.ReadAllTextAsync(
                Path.Combine(targetPath, "perspective-manifest.json"));
            await EmailScraper.Archive.PerspectiveArchiveBuilder.BuildAsync(
                source.Path,
                source.Directory,
                new PerspectiveArchiveConfig
                {
                    Name = "Moriarty",
                    ArchivePath = targetPath + Path.DirectorySeparatorChar
                },
                [new PerspectiveSourceRule(
                    "test-source", ["b2@example.test", "b1@example.test"])]);
            (await File.ReadAllTextAsync(Path.Combine(targetPath, "perspective-manifest.json")))
                .Should().Be(manifestBefore, "an unchanged address set and evidence set should not rebuild the projection");

            File.Exists(Path.Combine(sourceMessagesPath, "20260101_Evidence_excluded-secret.eml"))
                .Should().BeTrue("the complete source archive must remain untouched");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(targetPath)) Directory.Delete(targetPath, recursive: true);
        }
    }

    private static async Task AddSourceMessageAsync(
        string databasePath,
        string messagesPath,
        string providerMessageId,
        string from,
        string to,
        string? attachmentName,
        bool isUnsent = false,
        string sourceKey = "test-source")
    {
        var fileName = $"20260101_Evidence_{providerMessageId}.eml";
        var filePath = Path.Combine(messagesPath, fileName);
        var body = attachmentName == null
            ? "Content-Type: text/plain; charset=utf-8\r\n\r\nEvidence body."
            : $"Content-Type: multipart/mixed; boundary=boundary\r\n\r\n" +
              "--boundary\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nEvidence body.\r\n" +
              $"--boundary\r\nContent-Type: text/plain; name={attachmentName}\r\n" +
              $"Content-Disposition: attachment; filename={attachmentName}\r\n\r\nattachment evidence\r\n" +
              "--boundary--\r\n";
        var eml = $"From: {from}\r\nTo: {to}\r\nDate: 2026-01-01T12:00:00+00:00\r\n" +
            $"Message-ID: <{providerMessageId}@example.test>\r\nSubject: Evidence\r\nMIME-Version: 1.0\r\n{body}";
        await File.WriteAllTextAsync(filePath, eml);

        await Database.InsertMessageAsync(databasePath, new MessageRecord
        {
            SourceKey = sourceKey,
            Provider = "Test",
            ProviderMessageId = providerMessageId,
            MessageId = $"<{providerMessageId}@example.test>",
            Date = "2026-01-01T12:00:00+00:00",
            Subject = "Evidence",
            From = from,
            To = to,
            IsUnsent = isUnsent,
            FilePath = filePath
        });
    }

    private static async Task<List<string>> ReadValuesAsync(string databasePath, string sql)
    {
        var result = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }
}
