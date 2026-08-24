using MimeKit;
using System.Net;
using System.Text.RegularExpressions;

public static class SearchIndexer
{
    public static async Task BuildAsync(
        string databasePath)
    {
        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" SEARCH INDEX - STEP 7");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        var messages = await Database.GetMessagesForSearchIndexAsync(databasePath);

        Console.WriteLine($"Messages found: {messages.Count:N0}");

        var processed = 0;
        var errors = 0;

        foreach (var item in messages)
        {
            try
            {
                if (!File.Exists(item.FilePath)) throw new FileNotFoundException("EML file not found.", item.FilePath);

                var mime = await Task.Run(() => MimeMessage.Load(item.FilePath));
                var body = GetSearchableBody(mime);

                await Database.InsertSearchDocumentAsync(databasePath, item, body);

                processed++;

                if (processed % 50 == 0)
                    Console.WriteLine($"Indexed: {processed:N0}/{messages.Count:N0}");
            }
            catch (Exception ex)
            {
                errors++;

                Console.WriteLine($"ERROR message {item.MessageId}: {ex.Message}");
            }
        }

        await Database.RebuildSearchIndexAsync(databasePath);

        Console.WriteLine();
        Console.WriteLine($"Indexed: {processed:N0}");
        Console.WriteLine($"Errors:  {errors:N0}");
        Console.WriteLine("Search index complete.");
    }

    private static string GetSearchableBody(
        MimeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.TextBody)) return message.TextBody;

        if (!string.IsNullOrWhiteSpace(message.HtmlBody)) return StripHtml(message.HtmlBody);

        return "";
    }

    private static string StripHtml(
        string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        // Remove script/style blocks completely.
        var text = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        // Give common block elements some separation.
        text = Regex.Replace(text, @"<(br|p|div|li|tr|h[1-6])\b[^>]*>", " ", RegexOptions.IgnoreCase);

        // Remove remaining HTML tags.
        text = Regex.Replace(text, @"<[^>]+>", " ");

        // Decode things like &nbsp; &amp; &#233;
        text = WebUtility.HtmlDecode(text);

        // Collapse whitespace.
        text = Regex.Replace(text, @"\s+", " ");

        return text.Trim();
    }
}
