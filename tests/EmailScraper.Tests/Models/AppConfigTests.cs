namespace EmailScraper.Tests.Models;

public sealed class AppConfigTests
{
    [Fact]
    public void Defaults_point_to_repository_runtime_archive()
    {
        var config = new AppConfig();

        config.EmailProvider.Should().Be("Gmail");
        config.ArchivePath.Should().Be("./archive");
        config.DatabasePath.Should().Be("./archive/archive.db");
        config.EmailAddresses.Should().BeEmpty();
        config.PerspectiveArchives.Should().BeEmpty();
    }
}
