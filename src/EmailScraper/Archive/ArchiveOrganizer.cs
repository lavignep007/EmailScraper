namespace EmailScraper.Archive;

public static class ArchiveOrganizer
{
    public static async Task RunAsync(
        string databasePath, 
        string archivePath)
    {
        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" ARCHIVE ORGANIZATION - STEP 5");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        var messages = await Database.GetAllMessagesForThreadingAsync(databasePath);

        Console.WriteLine($"Messages: {messages.Count:N0}");

        await Database.SetThreadNamesFromSubjectsAsync(databasePath);

        var processed = 0;
        var updated = 0;
        var missing = 0;

        foreach (var message in messages)
        {
            var emlPath = message.FilePath;

            if (string.IsNullOrWhiteSpace(emlPath) || !File.Exists(emlPath))
            {
                Console.WriteLine();
                Console.WriteLine($"WARNING: EML not found for " +
                    $"{message.SourceKey}/{message.ProviderMessageId}");
                missing++;
            }
            else
            {
                var relativePath = Path
                    .GetRelativePath(archivePath, emlPath)
                    .Replace(Path.DirectorySeparatorChar, '/');

                var displayName = DisplayNameBuilder.Build(message);

                await Database.UpdateMessageOrganizationAsync(databasePath, message.Id, relativePath, displayName);

                updated++;
            }

            processed++;
            Console.Write($"\rProcessed: {processed:N0}/{messages.Count:N0}  " +
                $"Organized: {updated:N0}  Missing: {missing:N0}");
        }

        Console.WriteLine();
        Console.WriteLine($"Messages organized: {updated:N0}");
        Console.WriteLine($"EML files missing:   {missing:N0}");
        Console.WriteLine("Step 5 complete.");
    }

}
