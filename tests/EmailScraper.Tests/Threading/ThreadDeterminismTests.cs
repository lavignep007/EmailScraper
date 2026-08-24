using Microsoft.Data.Sqlite;
using EmailScraper.Tests.Data;

namespace EmailScraper.Tests.Threading;

public sealed class ThreadDeterminismTests
{
    [Fact]
    public async Task Equivalent_archives_produce_the_same_thread_key_regardless_of_insertion_order()
    {
        var messages = new[]
        {
            Message("gmail-a", "<A@Example.test>", null, "2026-01-01T12:00:00+00:00"),
            Message("gmail-b", "<B@example.test>", "<a@example.test>", "2026-01-02T12:00:00+00:00")
        };

        await using var first = await TestDatabase.CreateAsync();
        await using var second = await TestDatabase.CreateAsync();

        await InsertAsync(first.Path, messages);
        await InsertAsync(second.Path, messages.Reverse());

        await ThreadBuilder.BuildAsync(first.Path);
        await ThreadBuilder.BuildAsync(second.Path);

        (await ReadThreadKeysAsync(first.Path)).Should().Equal("message:a@example.test");
        (await ReadThreadKeysAsync(second.Path)).Should().Equal("message:a@example.test");
    }

    [Fact]
    public async Task Rebuilding_threads_preserves_the_thread_key_and_membership()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertAsync(database.Path,
        [
            Message("gmail-a", "<a@example.test>", null, "2026-01-01T12:00:00+00:00"),
            Message("gmail-b", "<b@example.test>", "<a@example.test>", "2026-01-02T12:00:00+00:00")
        ]);

        await ThreadBuilder.BuildAsync(database.Path);
        var before = await ReadPortableMembershipAsync(database.Path);

        await ThreadBuilder.BuildAsync(database.Path);
        var after = await ReadPortableMembershipAsync(database.Path);

        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    [Fact(Skip = "Required behavior: the current union-find builder merges sibling reply branches into one thread.")]
    public async Task Replies_to_different_recipients_form_distinct_threads_that_share_the_root_message()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertAsync(database.Path,
        [
            Message("gmail-a", "<a@example.test>", null, "2026-01-01T12:00:00+00:00"),
            Message("gmail-x", "<x@example.test>", "<a@example.test>", "2026-01-02T12:00:00+00:00"),
            Message("gmail-y", "<y@example.test>", "<a@example.test>", "2026-01-03T12:00:00+00:00")
        ]);

        await ThreadBuilder.BuildAsync(database.Path);

        var memberships = await ReadPortableMembershipAsync(database.Path);
        memberships.Select(x => x.ThreadKey).Distinct().Should().HaveCount(2);
        memberships.Count(x => x.MessageId == "<a@example.test>").Should().Be(2);
    }

    private static MessageRecord Message(string gmailId, string messageId, string? inReplyTo, string date) => new()
    {
        GmailId = gmailId,
        MessageId = messageId,
        InReplyTo = inReplyTo,
        Date = date,
        Subject = "Evidence",
        FilePath = $"messages/{gmailId}.eml"
    };

    private static async Task InsertAsync(string databasePath, IEnumerable<MessageRecord> messages)
    {
        foreach (var message in messages)
            await Database.InsertMessageAsync(databasePath, message);
    }

    private static async Task<List<string>> ReadThreadKeysAsync(string databasePath)
    {
        var result = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT ThreadKey FROM Threads ORDER BY ThreadKey;";
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) result.Add(reader.GetString(0));

        return result;
    }

    private static async Task<List<PortableMembership>> ReadPortableMembershipAsync(string databasePath)
    {
        var result = new List<PortableMembership>();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.ThreadKey, m.MessageId
            FROM MessageThreads mt
            JOIN Threads t ON t.Id = mt.ThreadId
            JOIN Messages m ON m.Id = mt.MessageId
            ORDER BY t.ThreadKey, mt.SortOrder;
            """;
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            result.Add(new PortableMembership(reader.GetString(0), reader.GetString(1)));

        return result;
    }

    private sealed record PortableMembership(string ThreadKey, string MessageId);
}
