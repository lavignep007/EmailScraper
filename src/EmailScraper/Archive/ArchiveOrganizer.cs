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

        var emlIndex = BuildEmlIndex(archivePath);

        Console.WriteLine($"EML files indexed: {emlIndex.Count:N0}");
        Console.WriteLine();

        await Database.SetThreadNamesFromSubjectsAsync(databasePath);

        var processed = 0;
        var updated = 0;
        var missing = 0;

        foreach (var message in messages)
        {
            /*
             * Locate the original EML.
             */
            emlIndex.TryGetValue(message.GmailId, out var emlPath);

            if (emlPath == null)
            {
                Console.WriteLine();
                Console.WriteLine($"WARNING: EML not found for " + $"{message.GmailId}");
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

    private static Dictionary<string, string> BuildEmlIndex(
        string archivePath)
    {
        var messagesPath = Path.Combine(archivePath, "messages");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(messagesPath))
        {
            return result;
        }

        /*
         * Downloaded EML files end with _{gmailId}.eml.
         * Index them once instead of scanning the complete
         * archive separately for every database message.
         */
        foreach (var path in Directory.EnumerateFiles(messagesPath, "*.eml", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            var separator = fileName.LastIndexOf('_');

            if (separator < 0 || separator == fileName.Length - 1)
                continue;

            var gmailId = fileName[(separator + 1)..];

            result.TryAdd(gmailId, path);
        }

        return result;
    }
}
