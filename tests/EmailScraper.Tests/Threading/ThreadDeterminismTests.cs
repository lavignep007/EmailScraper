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

        const string expected = "path:message:a@example.test->message:b@example.test";
        (await ReadThreadKeysAsync(first.Path)).Should().Equal(expected);
        (await ReadThreadKeysAsync(second.Path)).Should().Equal(expected);
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

    [Fact]
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

    [Fact]
    public async Task Missing_intermediate_messages_do_not_change_the_portable_thread_key()
    {
        var root = Message("gmail-a", "<a@example.test>", null, "2026-01-01T12:00:00+00:00");
        var middle = Message("gmail-x", "<x@example.test>", "<a@example.test>", "2026-01-02T12:00:00+00:00");
        var leaf = Message("gmail-z", "<z@example.test>", "<x@example.test>", "2026-01-03T12:00:00+00:00");
        leaf.References = "<a@example.test> <x@example.test>";

        await using var complete = await TestDatabase.CreateAsync();
        await using var partial = await TestDatabase.CreateAsync();

        await InsertAsync(complete.Path, [root, middle, leaf]);
        await InsertAsync(partial.Path, [root, leaf]);

        await ThreadBuilder.BuildAsync(complete.Path);
        await ThreadBuilder.BuildAsync(partial.Path);

        var completeKeys = await ReadThreadKeysAsync(complete.Path);
        var partialKeys = await ReadThreadKeysAsync(partial.Path);

        completeKeys.Should().Equal("path:message:a@example.test->message:x@example.test->message:z@example.test");
        partialKeys.Should().Equal(completeKeys);
    }

    [Fact]
    public async Task Standalone_messages_do_not_create_threads()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertAsync(database.Path,
        [
            Message("provider-a", "<a@example.test>", null, "2026-01-01T12:00:00+00:00")
        ]);

        await ThreadBuilder.BuildAsync(database.Path);

        (await ReadThreadsAsync(database.Path)).Should().BeEmpty();
    }

    [Fact]
    public async Task Extending_a_thread_preserves_its_local_id_and_changes_its_revision()
    {
        await using var database = await TestDatabase.CreateAsync();
        var root = Message("provider-a", "<a@example.test>", null, "2026-01-01T12:00:00+00:00");
        var reply = Message("provider-b", "<b@example.test>", "<a@example.test>", "2026-01-02T12:00:00+00:00");
        await InsertAsync(database.Path, [root, reply]);
        await ThreadBuilder.BuildAsync(database.Path);
        var before = (await ReadThreadsAsync(database.Path)).Single();

        var extension = Message("provider-c", "<c@example.test>", "<b@example.test>", "2026-01-03T12:00:00+00:00");
        extension.References = "<a@example.test> <b@example.test>";
        await InsertAsync(database.Path, [extension]);
        await ThreadBuilder.BuildAsync(database.Path);
        var after = (await ReadThreadsAsync(database.Path)).Single();

        after.Id.Should().Be(before.Id);
        after.RevisionHash.Should().NotBe(before.RevisionHash);
    }

    [Fact]
    public async Task A_later_split_preserves_one_local_id_and_creates_another_thread()
    {
        await using var database = await TestDatabase.CreateAsync();
        var root = Message("provider-a", "<a@example.test>", null, "2026-01-01T12:00:00+00:00");
        var middle = Message("provider-b", "<b@example.test>", "<a@example.test>", "2026-01-02T12:00:00+00:00");
        await InsertAsync(database.Path, [root, middle]);
        await ThreadBuilder.BuildAsync(database.Path);
        var originalId = (await ReadThreadsAsync(database.Path)).Single().Id;

        var firstBranch = Message("provider-c", "<c@example.test>", "<b@example.test>", "2026-01-03T12:00:00+00:00");
        firstBranch.References = "<a@example.test> <b@example.test>";
        var secondBranch = Message("provider-d", "<d@example.test>", "<b@example.test>", "2026-01-04T12:00:00+00:00");
        secondBranch.References = "<a@example.test> <b@example.test>";
        await InsertAsync(database.Path, [firstBranch, secondBranch]);
        await ThreadBuilder.BuildAsync(database.Path);

        var threads = await ReadThreadsAsync(database.Path);
        threads.Should().HaveCount(2);
        threads.Select(x => x.Id).Should().Contain(originalId);
    }

    private static MessageRecord Message(string gmailId, string messageId, string? inReplyTo, string date) => new()
    {
        ProviderMessageId = gmailId,
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

    private static async Task<List<StoredThread>> ReadThreadsAsync(string databasePath)
    {
        var result = new List<StoredThread>();
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ThreadKey, RevisionHash FROM Threads ORDER BY Id;";
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            result.Add(new StoredThread(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));

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

    private sealed record StoredThread(long Id, string ThreadKey, string RevisionHash);
}
