using EmailScraper.Tests.Data;
using QuestPDF.Infrastructure;

namespace EmailScraper.Tests.Archive;

public sealed class PdfGeneratorTests
{
    [Fact]
    public async Task Standalone_and_threaded_messages_all_receive_individual_pdfs()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        await using var database = await TestDatabase.CreateAsync();
        var archivePath = database.Directory;
        var messagesPath = Path.Combine(archivePath, "messages");
        Directory.CreateDirectory(messagesPath);

        var standalone = await AddMessageAsync(database.Path, messagesPath,
            "provider-s", "<s@example.test>", null, "Standalone", "2026-01-01T12:00:00+00:00");
        var root = await AddMessageAsync(database.Path, messagesPath,
            "provider-a", "<a@example.test>", null, "Conversation", "2026-01-02T12:00:00+00:00");
        var reply = await AddMessageAsync(database.Path, messagesPath,
            "provider-b", "<b@example.test>", "<a@example.test>", "Re: Conversation",
            "2026-01-03T12:00:00+00:00");

        await ThreadBuilder.BuildAsync(database.Path);
        await EmailScraper.Archive.PdfGenerator.GenerateAsync(database.Path, archivePath);

        Directory.EnumerateFiles(Path.Combine(archivePath, "pdf", "messages"), "*.pdf")
            .Should().HaveCount(3);
        Directory.EnumerateFiles(Path.Combine(archivePath, "pdf", "threads"), "*.pdf")
            .Should().HaveCount(1);

        File.Exists(Path.Combine(archivePath, "pdf", "messages", $"{standalone:D6} - Standalone.pdf"))
            .Should().BeTrue();
        new[] { root, reply }.Should().OnlyContain(id =>
            Directory.EnumerateFiles(Path.Combine(archivePath, "pdf", "messages"), $"{id:D6} - *.pdf").Any());
    }

    private static async Task<long> AddMessageAsync(
        string databasePath,
        string messagesPath,
        string providerMessageId,
        string messageId,
        string? inReplyTo,
        string subject,
        string date)
    {
        var fileName = $"{providerMessageId}.eml";
        var filePath = Path.Combine(messagesPath, fileName);
        var headers = $"From: a@example.test\r\nTo: b@example.test\r\nDate: {date}\r\n" +
            $"Message-ID: {messageId}\r\nSubject: {subject}\r\n" +
            (inReplyTo == null ? "" : $"In-Reply-To: {inReplyTo}\r\n") +
            "MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nEvidence body.";
        await File.WriteAllTextAsync(filePath, headers);

        await Database.InsertMessageAsync(databasePath, new MessageRecord
        {
            ProviderMessageId = providerMessageId,
            MessageId = messageId,
            InReplyTo = inReplyTo,
            Date = date,
            Subject = subject,
            From = "a@example.test",
            To = "b@example.test",
            FilePath = filePath
        });

        var stored = await Database.GetMessageByProviderMessageIdAsync(databasePath, providerMessageId);
        await Database.UpdateMessageOrganizationAsync(databasePath, stored!.Id, $"messages/{fileName}", subject);
        return stored.Id;
    }
}
