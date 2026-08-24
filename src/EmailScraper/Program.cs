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
Console.WriteLine("Tracking:");

foreach (var address in config.EmailAddresses)
    Console.WriteLine($"  {address}");

Console.WriteLine();

var syncCheckpoint = await emailProvider.GetSyncCheckpointAsync();

if (string.IsNullOrWhiteSpace(syncCheckpoint))
{
    Console.WriteLine("No synchronization state found.");
    Console.WriteLine("This will be the initial FULL synchronization.");
    Console.WriteLine();
    await emailProvider.FullSyncAsync();
}
else
{
    Console.WriteLine($"Last synchronization checkpoint: {syncCheckpoint}");
    Console.WriteLine();
    Console.WriteLine("[I] Incremental synchronization (default)");
    Console.WriteLine("[F] Full synchronization");
    Console.WriteLine();
    Console.Write("Selection: ");

    var input = Console.ReadLine()?.Trim().ToUpperInvariant();

    if (input == "F")
        await emailProvider.FullSyncAsync();
    else
        await emailProvider.IncrementalSyncAsync(syncCheckpoint);
}

Console.WriteLine();
Console.WriteLine("[P] Parse local EML archive");
Console.WriteLine("[R] Repair missing/empty EML files");
Console.WriteLine("[T] Rebuild message threads");
Console.WriteLine("[V] View thread diagnostic");
Console.WriteLine("[O] Organize archive");
Console.WriteLine("[D] Generate PDFs");
Console.WriteLine("[X] Build search index");
Console.WriteLine("[S] Search archive");
Console.WriteLine("[C] Validate archive");
Console.WriteLine("[Enter] Exit");
Console.Write("Selection: ");

var postSyncChoice = Console.ReadLine()?.Trim().ToUpperInvariant();

switch (postSyncChoice)
{
    case "P":
        await MimeParser.ParseArchiveAsync(config.DatabasePath, config.ArchivePath);
        break;
    case "R":
        await emailProvider.RepairCorruptEmlFilesAsync();
        break;
    case "T":
        await ThreadBuilder.BuildAsync(config.DatabasePath);
        break;
    case "V":
        await ThreadDiagnostic.ShowAsync(config.DatabasePath);
        break;
    case "O":
        await ArchiveOrganizer.RunAsync(config.DatabasePath, config.ArchivePath);
        break;
    case "D":
        await PdfGenerator.GenerateAsync(config.DatabasePath, config.ArchivePath);
        break;
    case "X":
        await SearchIndexer.BuildAsync(config.DatabasePath);
        break;
    case "C":
        await emailProvider.ValidateAsync();
        break;
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
}

Console.WriteLine();
Console.WriteLine("Done.");
