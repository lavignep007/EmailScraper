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

        var messages = await Database
            .GetAllMessagesForThreadingAsync(
                databasePath);

        Console.WriteLine($"Messages: {messages.Count:N0}");

        await Database
            .SetThreadNamesFromSubjectsAsync(
                databasePath);

        var updated = 0;

        foreach (var message in messages)
        {
            /*
             * Locate the original EML.
             */
            var emlPath = FindEml(
                archivePath, 
                message.GmailId);

            if (emlPath == null)
            {
                Console.WriteLine();
                Console.WriteLine($"WARNING: EML not found for " + $"{message.GmailId}");

                continue;
            }

            var relativePath = Path
                .GetRelativePath(
                    archivePath, 
                    emlPath)
                .Replace(
                    Path.DirectorySeparatorChar, '/');

            var displayName = DisplayNameBuilder
                .Build(
                    message);

            await Database
                .UpdateMessageOrganizationAsync(
                    databasePath, 
                    message.Id, 
                    relativePath, 
                    displayName);

            updated++;
        }

        Console.WriteLine();
        Console.WriteLine($"Messages organized: {updated:N0}");
        Console.WriteLine("Step 5 complete.");
    }

    private static string? FindEml(
        string archivePath,
        string gmailId)
    {
        var messagesPath = Path.Combine(
            archivePath, 
            "messages");

        if (!Directory.Exists(messagesPath))
        {
            return null;
        }

        /*
         * We search by Gmail ID rather than
         * assuming a particular directory layout.
         */
        return Directory
            .EnumerateFiles(
                messagesPath, "*.eml",
                SearchOption.AllDirectories)
            .FirstOrDefault(path => 
                Path
                    .GetFileNameWithoutExtension(
                        path)
                    .Contains(
                        gmailId, 
                        StringComparison.OrdinalIgnoreCase));
    }
}
