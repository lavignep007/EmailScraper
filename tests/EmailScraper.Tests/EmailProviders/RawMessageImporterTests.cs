using EmailScraper.EmailProviders;
using EmailScraper.Tests.Data;
using Microsoft.Data.Sqlite;

namespace EmailScraper.Tests.EmailProviders;

public sealed class RawMessageImporterTests
{
    [Fact]
    public async Task Empty_filter_imports_all_messages_and_filename_collisions_are_suffixed()
    {
        await using var database = await TestDatabase.CreateAsync();
        var appConfig = new AppConfig
        {
            ArchivePath = database.Directory,
            DatabasePath = database.Path
        };
        var source = new EmailSourceConfig
        {
            Id = "source",
            Name = "Source",
            Provider = "Test",
            MailboxAddress = "source@example.test"
        };
        var secondSource = new EmailSourceConfig
        {
            Id = "second-source",
            Name = "Second source",
            Provider = "Test",
            MailboxAddress = "second@example.test"
        };
        var raw = BuildMessage("outside@example.test", "recipient@example.test");

        (await RawMessageImporter.ImportAsync(
            appConfig, source, "same:identifier", null, raw, false)).Should().BeTrue();
        (await RawMessageImporter.ImportAsync(
            appConfig, secondSource, "same:identifier", null, raw, false)).Should().BeTrue();

        var paths = await ReadPathsAsync(database.Path);
        paths.Should().HaveCount(2);
        paths.Should().OnlyContain(path => File.Exists(path));
        Path.GetFileNameWithoutExtension(paths[1]).Should().EndWith("_1");
    }

    [Fact]
    public async Task Participant_filter_uses_exact_mailboxes()
    {
        await using var database = await TestDatabase.CreateAsync();
        var appConfig = new AppConfig
        {
            ArchivePath = database.Directory,
            DatabasePath = database.Path
        };
        var source = new EmailSourceConfig
        {
            Id = "source",
            Name = "Source",
            Provider = "Test",
            MailboxAddress = "source@example.test",
            FilterAddresses = ["target@example.test"]
        };

        (await RawMessageImporter.ImportAsync(
            appConfig,
            source,
            "excluded",
            null,
            BuildMessage("nottarget@example.test", "recipient@example.test"),
            false)).Should().BeFalse();
        (await RawMessageImporter.ImportAsync(
            appConfig,
            source,
            "included",
            null,
            BuildMessage("Target <target@example.test>", "recipient@example.test"),
            false)).Should().BeTrue();

        (await Database.GetAllProviderMessageIdsAsync(database.Path, source.Id))
            .Should().Equal("included");
    }

    private static byte[] BuildMessage(string from, string to) =>
        System.Text.Encoding.UTF8.GetBytes(
            $"From: {from}\r\nTo: {to}\r\n" +
            "Date: Thu, 1 Jan 2026 12:00:00 +0000\r\n" +
            "Message-ID: <shared@example.test>\r\n" +
            "Subject: Same display name\r\n\r\nEvidence body.");

    private static async Task<List<string>> ReadPathsAsync(string databasePath)
    {
        var result = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT FilePath FROM Messages ORDER BY Id;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }
}
