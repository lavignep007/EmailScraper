using MimeKit;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EmailScraper.Archive;

public static class PdfGenerator
{
    public static async Task GenerateAsync(
        string databasePath,
        string archivePath)
    {
        var pdfRoot = Path.Combine(archivePath, "pdf");
        var messagePdfPath = Path.Combine(pdfRoot, "messages");
        var threadPdfPath = Path.Combine(pdfRoot, "threads");

        Directory.CreateDirectory(messagePdfPath);
        Directory.CreateDirectory(threadPdfPath);

        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" PDF GENERATION - STEP 6");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        var threads = await Database.GetAllThreadsAsync(databasePath);
        var allMessages = await Database.GetAllPdfMessagesAsync(databasePath);

        Console.WriteLine($"Threads found: {threads.Count:N0}");
        Console.WriteLine($"Messages found: {allMessages.Count:N0}");

        var messageCount = 0;
        var messageSkipped = 0;
        var threadCount = 0;
        var threadSkipped = 0;
        var errors = 0;

        foreach (var message in allMessages)
        {
            try
            {
                if (await GenerateMessagePdfAsync(databasePath, archivePath, messagePdfPath, message))
                    messageCount++;
                else
                    messageSkipped++;

                Console.Write($"\rMessages: {messageCount + messageSkipped:N0}/{allMessages.Count:N0}  " +
                    $"Generated: {messageCount:N0}  Skipped: {messageSkipped:N0}  Errors: {errors:N0}");
            }
            catch (Exception ex)
            {
                errors++;
                Console.WriteLine();
                Console.WriteLine($"ERROR message {message.Id}:");
                Console.WriteLine($"  {ex.Message}");
            }
        }

        Console.WriteLine();

        foreach (var thread in threads)
        {
            try
            {
                var messages = await Database.GetPdfMessagesForThreadAsync(databasePath, thread.Id);

                if (messages.Count == 0) continue;

                var threadOutputPath = GetThreadPdfPath(threadPdfPath, thread);

                /*
                 * Generate complete thread PDF.
                 */
                if (string.Equals(thread.RevisionHash, thread.PdfRevisionHash, StringComparison.Ordinal) &&
                    File.Exists(threadOutputPath))
                {
                    threadSkipped++;
                }
                else
                {
                    await GenerateThreadPdfAsync(databasePath, archivePath, threadOutputPath, thread, messages);
                    await Database.SetThreadPdfRevisionAsync(databasePath, thread.Id, thread.RevisionHash);
                    threadCount++;
                }

                Console.Write($"\rThreads: {threadCount + threadSkipped:N0}/{threads.Count:N0}  " +
                    $"Generated: {threadCount:N0}  Skipped: {threadSkipped:N0}  Errors: {errors:N0}");
            }
            catch (Exception ex)
            {
                errors++;

                Console.WriteLine();
                Console.WriteLine($"ERROR thread {thread.Id}:");
                Console.WriteLine($"  {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine();

        Console.WriteLine("PDF generation finished.");
        Console.WriteLine($"Thread PDFs:   {threadCount:N0}");
        Console.WriteLine($"Threads current: {threadSkipped:N0}");
        Console.WriteLine($"Message PDFs:  {messageCount:N0}");
        Console.WriteLine($"Already exist: {messageSkipped:N0}");
        Console.WriteLine($"Errors:        {errors:N0}");

        Console.WriteLine();

        var expectedThreadPdfs = threads
            .Select(x => GetThreadPdfPath(threadPdfPath, x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(threadPdfPath, "*.pdf"))
            if (!expectedThreadPdfs.Contains(path))
                File.Delete(path);
    }

    private static async Task<bool> GenerateMessagePdfAsync(
        string databasePath,
        string archivePath,
        string outputDirectory,
        PdfMessage message)
    {
        var displayName = message.DisplayName ?? $"Message {message.Id}";
        var fileName = $"{message.Id:D6} - {SanitizeFileName(ShortenFileNamePart(displayName) + ".pdf")}";
        var outputPath = Path.Combine(outputDirectory, fileName);

        if (File.Exists(outputPath)) return false;

        if (string.IsNullOrWhiteSpace(message.RelativePath)) throw new InvalidOperationException($"Message {message.Id} has no RelativePath.");

        var emlPath = Path.Combine(archivePath,
            message.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(emlPath)) throw new FileNotFoundException($"EML file not found: {emlPath}");

        var mime = await Task.Run(() => MimeMessage.Load(emlPath));
        var attachments = await Database.GetAttachmentsForMessageAsync(databasePath, message.Id);
        var body = GetBodyText(mime);

        Document
            .Create(document =>
            {
                document.Page(page =>
                {
                    page.Size(PageSizes.A4);

                    page.Margin(40);

                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header()
                        .Element(container => ComposeMessageHeader(container, message, false));

                    page.Content()
                        .PaddingVertical(15)
                        .Element(container => ComposeMessageContent(container, message, body, attachments));

                    page.Footer()
                        .AlignCenter()
                        .Text(text =>
                        {
                            text.Span("Page ");
                            text.CurrentPageNumber();
                            text.Span(" / ");
                            text.TotalPages();
                        });
                });
            })
            .GeneratePdf(outputPath);

        return true;
    }

    private static async Task GenerateThreadPdfAsync(
        string databasePath,
        string archivePath,
        string outputPath,
        ThreadRecord thread,
        List<PdfMessage> messages)
    {
        /*
         * Load all EML bodies first.
         */
        var bodies = new Dictionary<long, string>();
        var attachments = new Dictionary<long, List<PdfAttachment>>();

        foreach (var message in messages)
        {
            if (message.MessageType == "Reaction")
            {
                bodies[message.Id] = message.ReactionEmoji ?? "";
                attachments[message.Id] = [];

                continue;
            }

            if (string.IsNullOrWhiteSpace(message.RelativePath))
                bodies[message.Id] = "";
            else
            {
                var emlPath = Path.Combine(archivePath,
                    message.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(emlPath))
                {
                    var mime = await Task.Run(() => MimeMessage.Load(emlPath));

                    bodies[message.Id] = GetBodyText(mime);
                }
                else
                {
                    bodies[message.Id] = "[Original EML file not found]";
                }
            }

            attachments[message.Id] = await Database.GetAttachmentsForMessageAsync(databasePath, message.Id);
        }

        Document
            .Create(document =>
            {
                document.Page(page =>
                {
                    page.Size(PageSizes.A4);

                    page.Margin(40);

                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header()
                        .Element(container => ComposeThreadHeader(container, thread, messages));

                    page.Content()
                        .PaddingVertical(15)
                        .Column(column =>
                        {
                            column.Spacing(20);

                            foreach (var message in messages)
                            {
                                column.Item()
                                    .Element(container => ComposeThreadMessage(container, message,
                                        bodies[message.Id], attachments[message.Id]));
                            }
                        });

                    page.Footer()
                        .AlignCenter()
                        .Text(text =>
                        {
                            text.Span("Page ");
                            text.CurrentPageNumber();
                            text.Span(" / ");
                            text.TotalPages();
                        });
                });
            })
            .GeneratePdf(outputPath);
    }

    private static string GetThreadPdfPath(
        string outputDirectory,
        ThreadRecord thread) =>
        Path.Combine(outputDirectory, $"{thread.Id:D6}.pdf");

    private static void ComposeMessageHeader(
        IContainer container,
        PdfMessage message,
        bool compact)
    {
        container
            .Column(column =>
            {
                column.Spacing(4);

                column.Item()
                    .Text(message.Subject ?? "(no subject)")
                    .FontSize(18)
                    .Bold();

                column.Item()
                    .Text($"Date: {message.Date ?? "Unknown"}");

                column.Item()
                    .Text($"From: {message.From ?? "Unknown"}");

                column.Item()
                    .Text($"To: {message.To ?? "Unknown"}");

                if (!string.IsNullOrWhiteSpace(message.Cc))
                    column.Item()
                        .Text($"Cc: {message.Cc}");

                if (!string.IsNullOrWhiteSpace(message.Bcc))
                    column.Item()
                        .Text($"Bcc: {message.Bcc}");

                column.Item()
                    .PaddingTop(6)
                    .LineHorizontal(1);
            });
    }

    private static void ComposeThreadHeader(
        IContainer container,
        ThreadRecord thread,
        List<PdfMessage> messages)
    {
        container
            .Column(column =>
            {
                column.Spacing(5);

                column.Item()
                    .Text(string.IsNullOrWhiteSpace(thread.Name) ? $"Thread {thread.Id}" : thread.Name)
                    .FontSize(22)
                    .Bold();

                column.Item()
                    .Text($"Messages: {messages.Count}");

                column.Item()
                    .Text($"First: {thread.FirstDate}");

                column.Item()
                    .Text($"Last: {thread.LastDate}");

                column.Item()
                    .PaddingTop(8)
                    .LineHorizontal(1);
            });
    }

    private static void ComposeThreadMessage(
        IContainer container,
        PdfMessage message,
        string body,
        List<PdfAttachment> attachments)
    {
        container
            .Border(1)
            .Padding(12)
            .Column(column =>
            {
                column.Spacing(6);

                if (message.MessageType == "Reaction")
                {
                    column.Item()
                        .Text($"Reaction: {message.ReactionEmoji}")
                        .FontSize(18)
                        .Bold();

                    column.Item()
                        .Text($"From: {message.From}");

                    column.Item()
                        .Text($"Date: {message.Date}");

                    return;
                }

                column.Item()
                    .Text(message.DisplayName ?? $"Message {message.Id}")
                    .FontSize(12)
                    .Bold();

                column.Item()
                    .Text($"From: {message.From}");

                column.Item()
                    .Text($"To: {message.To}");

                if (!string.IsNullOrWhiteSpace(message.Cc))
                    column.Item()
                        .Text($"Cc: {message.Cc}");

                column.Item()
                    .Text($"Date: {message.Date}");

                column.Item()
                    .PaddingTop(5)
                    .LineHorizontal(0.5f);

                if (!string.IsNullOrWhiteSpace(body))
                    column.Item()
                        .Text(SanitizePdfText(body, message.Id))
                        .FontSize(10);

                ComposeAttachments(column, attachments);
            });
    }

    private static void ComposeMessageContent(
        IContainer container,
        PdfMessage message,
        string body,
        List<PdfAttachment> attachments)
    {
        container
            .Column(column =>
            {
                column.Spacing(10);

                if (message.MessageType == "Reaction")
                {
                    column.Item()
                        .Text(message.ReactionEmoji ?? "")
                        .FontSize(32)
                        .Bold();

                    return;
                }

                if (!string.IsNullOrWhiteSpace(body))
                    column.Item()
                        .Text(SanitizePdfText(body, message.Id))
                        .FontSize(10);

                ComposeAttachments(column, attachments);
            });
    }

    private static void ComposeAttachments(
        ColumnDescriptor column,
        List<PdfAttachment> attachments)
    {
        if (attachments.Count == 0) return;

        column.Item()
            .PaddingTop(10)
            .Text("Attachments")
            .FontSize(12)
            .Bold();

        foreach (var attachment in attachments)
        {
            var inline = attachment.IsInline ? " (inline)" : "";

            column.Item()
                .Text($"• {attachment.FileName}{inline}  " +
                    $"[{attachment.ContentType}, {FormatSize(attachment.Size)}]");
        }
    }

    private static string FormatSize(
        long size)
    {
        if (size < 1024) return $"{size} B";

        if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";

        return $"{size / (1024.0 * 1024.0):F1} MB";
    }

    private static string GetBodyText(
        MimeMessage message)
    {
        /*
         * Prefer the proper text/plain part.
         */
        if (!string.IsNullOrWhiteSpace(message.TextBody)) return NormalizeBody(message.TextBody);

        /*
         * Some emails are HTML-only.
         */
        if (!string.IsNullOrWhiteSpace(message.HtmlBody)) return HtmlToPlainText(message.HtmlBody);

        return "";
    }

    private static string NormalizeBody(
        string text)
    {
        return text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();
    }

    private static string HtmlToPlainText(
        string html)
    {
        var text = html;

        text = System.Text.RegularExpressions.Regex.Replace(text, @"<br\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        text = System.Text.RegularExpressions.Regex.Replace(text, @"</p\s*>", "\n\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");

        return System.Net.WebUtility
            .HtmlDecode(text)
            .Trim();
    }

    private static string SanitizeFileName(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unnamed";

        // Remove characters Windows does not permit in filenames.
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        // Windows also doesn't like control characters.
        value = new string(value.Where(c => !char.IsControl(c)).ToArray());

        // Avoid filenames ending in a space or period.
        value = value.Trim().TrimEnd('.');

        // Avoid Windows reserved device names.
        var reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9"
        };

        if (reservedNames.Contains(value))
            value = "_" + value;

        // Keep paths comfortably below Windows path limits.
        const int maxLength = 180;

        if (value.Length > maxLength)
            value = value[..maxLength].TrimEnd('.', ' ');

        return string.IsNullOrWhiteSpace(value)
            ? "Unnamed"
            : value;
    }

    private static string ShortenFileNamePart(
        string value,
        int maxLength = 100)
    {
        value = value.Trim();

        if (value.Length <= maxLength) return value;

        return value[..maxLength].TrimEnd() + "...";
    }

    private static string SanitizePdfText(
        string? value,
        long? messageId = null)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";

        if (value.Contains('\uF0D8'))
            Console.WriteLine($"Message {messageId}: removing glyph U+F0D8");

        return value.Replace("\uF0D8", "");
    }
}
