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

if (config.EmailAddresses.Count == 0)
{
    Console.WriteLine("No email addresses configured.");
    return;
}

QuestPDF.Settings.License = LicenseType.Community;

Directory.CreateDirectory(config.ArchivePath);
Directory.CreateDirectory(Path.Combine(config.ArchivePath, "messages"));

await Database.InitializeAsync(config.DatabasePath);

var emailProvider = await EmailProviderFactory.CreateAsync(config);

Console.WriteLine();
Console.WriteLine("==========================================");
Console.WriteLine(" Email Scraper - V1.1");
Console.WriteLine("==========================================");
Console.WriteLine();
Console.WriteLine($"Provider: {emailProvider.Name}");
Console.WriteLine($"Complete archive: {Path.GetFullPath(config.ArchivePath)}");
Console.WriteLine("Tracking:");

foreach (var address in config.EmailAddresses)
    Console.WriteLine($"  {address}");

Console.WriteLine();

var syncCheckpoint = await emailProvider.GetSyncCheckpointAsync();
SynchronizationResult synchronization;

if (string.IsNullOrWhiteSpace(syncCheckpoint))
{
    Console.WriteLine("No synchronization state found.");
    Console.WriteLine("Starting the initial full synchronization automatically.");
    Console.WriteLine();
    synchronization = await emailProvider.FullSyncAsync();
}
else
{
    Console.WriteLine($"Last synchronization checkpoint: {syncCheckpoint}");
    Console.WriteLine("Starting incremental synchronization automatically.");
    synchronization = await emailProvider.IncrementalSyncAsync(syncCheckpoint);
}

if (synchronization.Downloaded > 0)
{
    Console.WriteLine();
    Console.WriteLine($"Processing {synchronization.Downloaded:N0} newly downloaded message(s)...");

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

await emailProvider.RefreshDeliveryStatesAsync();

if (config.PerspectiveArchives.Count > 0)
{
    Console.WriteLine("Perspective archives:");
    foreach (var perspective in config.PerspectiveArchives)
        Console.WriteLine($"  {perspective.Name}: {Path.GetFullPath(perspective.ArchivePath)}");

    foreach (var perspective in config.PerspectiveArchives)
        await PerspectiveArchiveBuilder.BuildAsync(
            config.DatabasePath, config.ArchivePath, perspective);
}

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
