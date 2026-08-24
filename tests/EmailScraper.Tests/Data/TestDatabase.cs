using Microsoft.Data.Sqlite;

namespace EmailScraper.Tests.Data;

internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string directory, string path)
    {
        Directory = directory;
        Path = path;
    }

    public string Directory { get; }

    public string Path { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EmailScraper.Tests",
            Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var database = new TestDatabase(directory, System.IO.Path.Combine(directory, "archive.db"));
        await Database.InitializeAsync(database.Path);
        return database;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();

        if (System.IO.Directory.Exists(Directory))
            System.IO.Directory.Delete(Directory, recursive: true);

        return ValueTask.CompletedTask;
    }
}
