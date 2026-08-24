public sealed class AppConfigTests
{
    [Fact]
    public void Defaults_point_to_repository_runtime_archive()
    {
        var config = new AppConfig();

        Assert.Equal("./archive", config.ArchivePath);
        Assert.Equal("./archive/archive.db", config.DatabasePath);
        Assert.Empty(config.EmailAddresses);
    }
}
