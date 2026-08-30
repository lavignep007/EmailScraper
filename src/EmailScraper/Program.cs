using QuestPDF.Infrastructure;

var builder = Host.CreateApplicationBuilder(
    new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    });

var config = builder.Configuration.Get<AppConfig>()
    ?? throw new InvalidOperationException("Could not load configuration.");

Console.WriteLine($"Environment: {builder.Environment.EnvironmentName}");

if (config.EmailSources.Count == 0)
{
    Console.WriteLine("No email sources configured.");
    return;
}

ValidateConfiguration(config);

QuestPDF.Settings.License = LicenseType.Community;

Directory.CreateDirectory(config.ArchivePath);
Directory.CreateDirectory(Path.Combine(config.ArchivePath, "messages"));

await Database.InitializeAsync(config.DatabasePath);

var emailProviders = new List<IEmailProvider>();

foreach (var source in config.EmailSources)
    emailProviders.Add(await EmailProviderFactory.CreateAsync(config, source));

Console.WriteLine();
Console.WriteLine("==========================================");
Console.WriteLine(" Email Scraper - V1.1");
Console.WriteLine("==========================================");
Console.WriteLine();
Console.WriteLine($"Complete archive: {Path.GetFullPath(config.ArchivePath)}");
Console.WriteLine("Email sources:");

foreach (var provider in emailProviders)
{
    var source = config.EmailSources.Single(x => x.Id == provider.SourceKey);
    Console.WriteLine($"  {provider.Name}: {provider.MailboxAddress}");
    Console.WriteLine(source.FilterAddresses.Count == 0
        ? "    Filter: all mailbox messages"
        : $"    Filter: {string.Join(", ", source.FilterAddresses)}");
}

Console.WriteLine();

long downloaded = 0;
var sourceRuns = new List<ArchiveManifestGenerator.SourceRun>();

foreach (var emailProvider in emailProviders)
{
    var syncCheckpoint = await emailProvider.GetSyncCheckpointAsync();
    var source = config.EmailSources.Single(x => x.Id == emailProvider.SourceKey);
    var beforeIds = await Database.GetAllProviderMessageIdsAsync(config.DatabasePath, source.Id);
    var mode = string.IsNullOrWhiteSpace(syncCheckpoint) ? "full" : "incremental";
    SynchronizationResult synchronization;

    if (string.IsNullOrWhiteSpace(syncCheckpoint))
    {
        Console.WriteLine($"No synchronization state found for {emailProvider.Name}.");
        Console.WriteLine("Starting its initial full synchronization automatically.");
        synchronization = await emailProvider.FullSyncAsync();
    }
    else
    {
        Console.WriteLine($"Last checkpoint for {emailProvider.Name}: {syncCheckpoint}");
        Console.WriteLine("Starting its incremental synchronization automatically.");
        synchronization = await emailProvider.IncrementalSyncAsync(syncCheckpoint);
    }

    downloaded += synchronization.Downloaded;

    var afterIds = await Database.GetAllProviderMessageIdsAsync(config.DatabasePath, source.Id);
    sourceRuns.Add(new ArchiveManifestGenerator.SourceRun(
    source.Id, source.Provider, source.MailboxAddress,
        source.FilterAddresses.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
        mode, syncCheckpoint,
        await emailProvider.GetSyncCheckpointAsync(),
        afterIds.Except(beforeIds).OrderBy(x => x, StringComparer.Ordinal).ToList(),
        [], beforeIds.Intersect(afterIds).OrderBy(x => x, StringComparer.Ordinal).ToList(),
        beforeIds.Except(afterIds).OrderBy(x => x, StringComparer.Ordinal).ToList()));
}

if (downloaded > 0)
{
    Console.WriteLine();
    Console.WriteLine($"Processing {downloaded:N0} newly downloaded message(s) from all sources...");

    await MimeParser.ParseArchiveAsync(config.DatabasePath, config.ArchivePath);
    await ThreadBuilder.BuildAsync(config.DatabasePath);
    await ArchiveOrganizer.RunAsync(config.DatabasePath, config.ArchivePath);
    await PdfGenerator.GenerateAsync(config.DatabasePath, config.ArchivePath);
    await SearchIndexer.BuildAsync(config.DatabasePath);
}
else
{
    Console.WriteLine();
    Console.WriteLine("No new messages downloaded. Archive processing is already up to date.");
}

foreach (var emailProvider in emailProviders)
    await emailProvider.RefreshDeliveryStatesAsync();

if (config.PerspectiveArchives.Count > 0)
{
    Console.WriteLine("Perspective archives:");
    foreach (var perspective in config.PerspectiveArchives)
        Console.WriteLine($"  {perspective.Name}: {Path.GetFullPath(perspective.ArchivePath)}");

    foreach (var perspective in config.PerspectiveArchives)
    {
        var rules = config.EmailSources
            .Select(source => new
            {
                Source = source,
                Filter = source.PerspectiveFilters.FirstOrDefault(x =>
                    string.Equals(x.Key, perspective.Name, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Filter.Key) && x.Filter.Value is not null)
            .Select(x => new PerspectiveSourceRule(x.Source.Id, x.Filter.Value!))
            .ToList();

        await PerspectiveArchiveBuilder.BuildAsync(
            config.DatabasePath, config.ArchivePath, perspective, rules);
    }
}

await ArchiveManifestGenerator.WriteAsync(
    config.DatabasePath, config.ArchivePath, config, sourceRuns);
Console.WriteLine($"Archive manifest: {Path.Combine(config.ArchivePath, "archive-manifest.json")}");

while (true)
{
    Console.WriteLine();
    Console.WriteLine("[S] Search archive");
    Console.WriteLine("[C] Validate archive integrity");
    Console.WriteLine("[Enter] Exit");
    Console.Write("Selection: ");

    var choice = Console.ReadLine()?.Trim().ToUpperInvariant();

    switch (choice)
    {
        case "S":
        {
            Console.Write("Search: ");
            var query = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(query)) break;

            var results = await Database.SearchAsync(config.DatabasePath, query);
            Console.WriteLine();
            Console.WriteLine($"Results: {results.Count:N0}");
            Console.WriteLine();

            foreach (var result in results)
            {
                Console.WriteLine($"Message #{result.MessageId}" +
                    (result.ThreadId.HasValue ? $"  Thread #{result.ThreadId}" : ""));
                Console.WriteLine($"  Date   : {result.Date}");
                Console.WriteLine($"  From   : {result.From}");
                Console.WriteLine($"  Subject: {result.Subject}");
                Console.WriteLine($"  {result.Snippet}");
                Console.WriteLine();
            }

            break;
        }
        case "C":
            await ArchiveValidator.ValidateAsync(config.DatabasePath);
            foreach (var emailProvider in emailProviders)
                await emailProvider.ValidateAsync();
            break;
        case "":
        case null:
            Console.WriteLine();
            Console.WriteLine("Done.");
            return;
        default:
            Console.WriteLine("Unknown selection.");
            break;
    }
}

static void ValidateConfiguration(AppConfig config)
{
    var duplicateSource = config.EmailSources
        .Where(x => !string.IsNullOrWhiteSpace(x.Id))
        .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(x => x.Count() > 1);
    if (duplicateSource is not null)
        throw new InvalidOperationException($"Duplicate email source Id '{duplicateSource.Key}'.");

    foreach (var source in config.EmailSources)
    {
        if (string.IsNullOrWhiteSpace(source.Id))
            throw new InvalidOperationException("Every email source requires a stable Id.");
        if (string.IsNullOrWhiteSpace(source.Name))
            throw new InvalidOperationException($"Email source '{source.Id}' requires a Name.");
        if (string.IsNullOrWhiteSpace(source.Provider))
            throw new InvalidOperationException($"Email source '{source.Id}' requires a Provider.");
        if (string.IsNullOrWhiteSpace(source.MailboxAddress))
            throw new InvalidOperationException($"Email source '{source.Id}' requires a MailboxAddress.");
    }

    var duplicatePerspective = config.PerspectiveArchives
        .Where(x => !string.IsNullOrWhiteSpace(x.Name))
        .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(x => x.Count() > 1);
    if (duplicatePerspective is not null)
        throw new InvalidOperationException(
            $"Duplicate perspective archive Name '{duplicatePerspective.Key}'.");
}
