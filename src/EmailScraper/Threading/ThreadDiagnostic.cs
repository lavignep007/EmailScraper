namespace EmailScraper.Threading;

public static class ThreadDiagnostic
{
    public static async Task ShowAsync(
        string databasePath)
    {
        var rows = await Database.GetThreadViewAsync(databasePath);

        if (rows.Count == 0)
        {
            Console.WriteLine("No threads found.");
            return;
        }

        var threadIds = rows
            .Select(x => x.ThreadId)
            .Distinct()
            .ToList();

        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" THREAD DIAGNOSTIC");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        Console.WriteLine($"Threads: {threadIds.Count:N0}");
        Console.WriteLine($"Messages: {rows.Count:N0}");

        Console.WriteLine();

        Console.Write("Enter thread number, [A]ll, or [Enter] to cancel: ");

        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(input)) return;

        if (input.Equals("A", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var threadId in threadIds)
            {
                ShowThread(rows.Where(x => x.ThreadId == threadId).ToList());
            }

            return;
        }

        if (!long.TryParse(input, out var selectedThreadId))
        {
            Console.WriteLine("Invalid thread number.");
            return;
        }

        var selected = rows.Where(x => x.ThreadId == selectedThreadId).ToList();

        if (selected.Count == 0)
        {
            Console.WriteLine($"Thread {selectedThreadId} not found.");
            return;
        }

        ShowThread(selected);
    }

    private static void ShowThread(
        List<ThreadViewRow> rows)
    {
        var first = rows.First();

        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine($"THREAD #{first.ThreadId}");
        Console.WriteLine($"Subject: {first.ThreadSubject}");
        Console.WriteLine($"Messages: {rows.Count}");
        Console.WriteLine("============================================================");

        foreach (var row in rows)
        {
            Console.WriteLine();

            Console.WriteLine($"[{row.SortOrder + 1:000}] {row.MessageType}");
            Console.WriteLine($"    Database ID : {row.MessageId}");
            Console.WriteLine($"    Date        : {row.Date}");
            Console.WriteLine($"    From        : {row.From}");
            Console.WriteLine($"    To          : {row.To}");
            Console.WriteLine($"    Subject     : {row.Subject}");
            Console.WriteLine($"    Parent      : {FormatParent(row.ParentMessageId)}");
            Console.WriteLine($"    Message-ID  : {row.InternetMessageId}");
            Console.WriteLine($"    In-Reply-To : {row.InReplyTo}");

            if (!string.IsNullOrWhiteSpace(row.ReactionEmoji))
                Console.WriteLine($"    Reaction    : {row.ReactionEmoji}");
        }

        Console.WriteLine();
    }

    private static string FormatParent(
        long? parentId)
    {
        return parentId.HasValue
            ? parentId.Value.ToString()
            : "(root)";
    }
}
