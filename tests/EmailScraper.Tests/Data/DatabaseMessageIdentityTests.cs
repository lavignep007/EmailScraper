using Microsoft.Data.Sqlite;

namespace EmailScraper.Tests.Data;

public sealed class DatabaseMessageIdentityTests
{
    private readonly Fixture fixture = new();

    [Fact]
    public async Task Provider_message_id_is_preserved_exactly()
    {
        await using var database = await TestDatabase.CreateAsync();
        var gmailId = fixture.Create<string>();

        await Database.InsertMessageAsync(database.Path, CreateMessage(gmailId));

        var stored = await Database.GetMessageByProviderMessageIdAsync(
            database.Path, "personal-gmail", gmailId);

        stored.Should().NotBeNull();
        stored!.ProviderMessageId.Should().Be(gmailId);
    }

    [Fact]
    public async Task Duplicate_provider_message_id_is_rejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var gmailId = fixture.Create<string>();

        await Database.InsertMessageAsync(database.Path, CreateMessage(gmailId));

        var duplicate = () => Database.InsertMessageAsync(database.Path, CreateMessage(gmailId));

        await duplicate.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Same_provider_message_id_is_allowed_in_different_sources()
    {
        await using var database = await TestDatabase.CreateAsync();
        var providerId = fixture.Create<string>();

        await Database.InsertMessageAsync(database.Path, CreateMessage(providerId));
        var second = CreateMessage(providerId);
        second.SourceKey = "syndicate-o365";
        second.Provider = "Microsoft365";

        await Database.InsertMessageAsync(database.Path, second);

        (await Database.GetAllProviderMessageIdsAsync(database.Path, "personal-gmail"))
            .Should().ContainSingle(providerId);
        (await Database.GetAllProviderMessageIdsAsync(database.Path, "syndicate-o365"))
            .Should().ContainSingle(providerId);
    }

    private MessageRecord CreateMessage(string gmailId) => new()
    {
        SourceKey = "personal-gmail",
        Provider = "Gmail",
        ProviderMessageId = gmailId,
        MessageId = $"<{fixture.Create<string>()}@example.test>",
        Date = "2026-01-01T12:00:00+00:00",
        Subject = fixture.Create<string>(),
        FilePath = $"messages/{gmailId}.eml"
    };
}
