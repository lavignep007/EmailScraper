using Google;
using Google.Apis.Gmail.v1;
using EmailScraper.EmailProviders.Gmail;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace EmailScraper.Validation;

public static class ArchiveValidator
{
    public static async Task ValidateAsync(
        GmailService gmail, 
        string databasePath,
        string gmailQuery)
    {
        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" ARCHIVE VALIDATION - STEP 8");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        var report = new ValidationReport();

        var messages = await Database.GetMessagesForValidationAsync(databasePath);

        report.Messages = messages.Count;

        /*
         * ====================================================
         * EML validation
         * ====================================================
         */

        foreach (var message in messages)
        {
            if (!File.Exists(message.FilePath))
            {
                report.MissingEml++;

                Console.WriteLine($"MISSING EML message {message.Id}: " + message.FilePath);

                continue;
            }

            var info = new FileInfo(message.FilePath);

            if (info.Length == 0)
            {
                report.EmptyEml++;

                Console.WriteLine($"EMPTY EML message {message.Id}: " + message.FilePath);

                continue;
            }

            try
            {
                /*
                 * Actually parse the EML.
                 *
                 * This catches files that exist and are
                 * non-zero but are still malformed.
                 */
                await Task.Run(() => MimeMessage.Load(message.FilePath));
            }
            catch (Exception ex)
            {
                report.InvalidEml++;

                Console.WriteLine($"INVALID EML message {message.Id}: " + ex.Message);
            }
        }

        /*
         * ====================================================
         * Database counts
         * ====================================================
         */

        report.Threads = await Database.ExecuteCountAsync(
            databasePath,
                """
                SELECT COUNT(*)
                FROM Threads;
                """);

        report.Attachments = await Database.ExecuteCountAsync(
            databasePath,
                """
                SELECT COUNT(*)
                FROM Attachments;
                """);

        report.SearchDocuments = await Database.ExecuteCountAsync(
            databasePath,
                """
                SELECT COUNT(*)
                FROM SearchDocuments;
                """);

        /*
         * ====================================================
         * Missing physical attachment files
         * ====================================================
         */

        report.MissingAttachments = await CountMissingAttachmentFilesAsync(databasePath);

        /*
         * ====================================================
         * Orphan Attachments
         *
         * Attachment exists in Attachments but is referenced
         * by no MessageAttachments row.
         * ====================================================
         */

        report.OrphanAttachments = await Database.ExecuteCountAsync(
            databasePath,
                """
                SELECT COUNT(*)
                FROM Attachments a
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM MessageAttachments ma
                    WHERE ma.AttachmentId = a.Id
                );
                """);

        /*
         * ====================================================
         * Messages missing from thread reconstruction
         * ====================================================
         */

        report.MessagesWithoutThread = await Database.ExecuteCountAsync(
            databasePath,
                """
                SELECT COUNT(*)
                FROM Messages m
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM MessageThreads mt
                    WHERE mt.MessageId = m.Id
                );
                """);

        /*
         * ====================================================
         * Broken ParentMessageId relationships
         * ====================================================
         */

        report.BrokenParents = await Database.ExecuteCountAsync(
            databasePath,
                """
                SELECT COUNT(*)
                FROM MessageThreads mt
                WHERE mt.ParentMessageId IS NOT NULL
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM Messages m
                      WHERE m.Id = mt.ParentMessageId
                  );
                """);

        /*
         * ====================================================
         * Duplicate provider message IDs
         *
         * Should always be zero because ProviderMessageId is UNIQUE,
         * but validation should still prove it.
         * ====================================================
         */

        report.DuplicateProviderMessageIds = await Database.ExecuteCountAsync(
            databasePath,
                """
                SELECT COUNT(*)
                FROM
                (
                    SELECT ProviderMessageId
                    FROM Messages
                    GROUP BY ProviderMessageId
                    HAVING COUNT(*) > 1
                );
                """);

        /*
         * Duplicate RFC Message-ID.
         *
         * Unlike ProviderMessageId, this isn't necessarily fatal.
         * Broken/forwarded/imported messages occasionally
         * reuse Message-ID values, so report it separately.
         */
        report.DuplicateMessageIds = await Database.ExecuteCountAsync(
            databasePath,
                """
                SELECT COUNT(*)
                FROM
                (
                    SELECT MessageId
                    FROM Messages
                    WHERE MessageId IS NOT NULL
                      AND TRIM(MessageId) <> ''
                    GROUP BY LOWER(TRIM(MessageId))
                    HAVING COUNT(*) > 1
                );
                """);

        /*
         * ====================================================
         * Search index completeness
         * ====================================================
         */

        report.MissingSearchDocuments = await Database.ExecuteCountAsync(
            databasePath,
                """
                SELECT COUNT(*)
                FROM Messages m
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM SearchDocuments s
                    WHERE s.MessageId = m.Id
                );
                """);

        /*
         * ====================================================
         * Report
         * ====================================================
         */

        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" ARCHIVE REPORT");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        Console.WriteLine($"Messages:                  {report.Messages:N0}");
        Console.WriteLine($"Threads:                   {report.Threads:N0}");
        Console.WriteLine($"Attachments:               {report.Attachments:N0}");
        Console.WriteLine($"Search documents:          {report.SearchDocuments:N0}");

        Console.WriteLine();

        Console.WriteLine($"Missing EML:               {report.MissingEml:N0}");
        Console.WriteLine($"Empty EML:                 {report.EmptyEml:N0}");
        Console.WriteLine($"Invalid EML:               {report.InvalidEml:N0}");

        Console.WriteLine();

        Console.WriteLine($"Missing attachment files:  {report.MissingAttachments:N0}");
        Console.WriteLine($"Orphan attachments:        {report.OrphanAttachments:N0}");

        Console.WriteLine();

        Console.WriteLine($"Messages without thread:   {report.MessagesWithoutThread:N0}");
        Console.WriteLine($"Broken parent links:       {report.BrokenParents:N0}");

        Console.WriteLine();

        Console.WriteLine($"Duplicate provider IDs:    {report.DuplicateProviderMessageIds:N0}");
        Console.WriteLine($"Duplicate Message-IDs:     {report.DuplicateMessageIds:N0}");

        Console.WriteLine();

        Console.WriteLine($"Missing search documents:  {report.MissingSearchDocuments:N0}");

        Console.WriteLine();

        Console.WriteLine($"Validation errors:         {report.Errors:N0}");

        Console.WriteLine();

        /*
         * ====================================================
         * Gmail reconciliation
         * ====================================================
         */

        var gmailReport = await ReconcileWithGmailAsync(gmail, databasePath, gmailQuery);

        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" GMAIL RECONCILIATION");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        Console.WriteLine($"Gmail matching messages:   {gmailReport.GmailMessages:N0}");
        Console.WriteLine($"Local archived messages:   {gmailReport.LocalMessages:N0}");
        Console.WriteLine($"Missing locally:           {gmailReport.MissingLocally.Count:N0}");
        Console.WriteLine($"Extra locally:             {gmailReport.ExtraLocally.Count:N0}");

        Console.WriteLine();

        foreach (var gmailId in gmailReport.MissingLocally)
        {
            Console.WriteLine($"MISSING LOCAL: {gmailId}");
        }

        foreach (var gmailId in gmailReport.ExtraLocally)
        {
            Console.WriteLine($"EXTRA LOCAL: {gmailId}");
        }

        if (gmailReport.ExtraLocally.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Diagnosing extra local messages...");

            await DiagnoseExtraMessagesAsync(gmail, gmailReport.ExtraLocally);
        }

        /*
         * ====================================================
         * Final verdict
         * ====================================================
         */

        var gmailErrors = gmailReport.MissingLocally.Count + gmailReport.ExtraLocally.Count;

        Console.WriteLine();

        Console.WriteLine($"Gmail reconciliation errors: {gmailErrors:N0}");

        Console.WriteLine();

        if (report.Errors == 0 &&
            gmailErrors == 0)
        {
            Console.WriteLine("ARCHIVE VALIDATION PASSED");
        }
        else
        {
            Console.WriteLine("ARCHIVE VALIDATION FAILED");
        }

        Console.WriteLine();
    }

    private static async Task<int> CountMissingAttachmentFilesAsync(
        string databasePath)
    {
        var missing = 0;

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        SELECT
            Id,
            FilePath
        FROM Attachments
        ORDER BY Id;
        """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var id = reader.GetInt64(0);

            var filePath = reader.GetString(1);

            if (!File.Exists(filePath))
            {
                missing++;

                Console.WriteLine($"MISSING ATTACHMENT {id}: " + filePath);

                continue;
            }

            var info = new FileInfo(filePath);

            if (info.Length == 0)
            {
                missing++;

                Console.WriteLine($"EMPTY ATTACHMENT {id}: " + filePath);
            }
        }

        return missing;
    }

    public static async Task<GmailReconciliationReport> ReconcileWithGmailAsync(
        GmailService gmail,
        string databasePath,
        string gmailQuery)
    {
        var gmailIds = await GetMatchingGmailIdsAsync(gmail, gmailQuery);

        var localIds = await Database.GetAllProviderMessageIdsAsync(databasePath);

        var missingLocally = gmailIds
            .Except(localIds)
            .OrderBy(x => x)
            .ToList();

        var extraLocally = localIds
            .Except(gmailIds)
            .OrderBy(x => x)
            .ToList();

        return new GmailReconciliationReport
        {
            GmailMessages = gmailIds.Count,
            LocalMessages = localIds.Count,
            MissingLocally = missingLocally,
            ExtraLocally = extraLocally
        };
    }

    private static async Task<HashSet<string>> GetMatchingGmailIdsAsync(
        GmailService gmail,
        string query)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        string? pageToken = null;

        do
        {
            var request = gmail.Users.Messages.List("me");

            request.Q = query;
            request.MaxResults = 500;
            request.PageToken = pageToken;

            var response = await request.ExecuteAsync();

            if (response.Messages != null)
            {
                foreach (var message in response.Messages)
                {
                    if (!string.IsNullOrWhiteSpace(message.Id))
                    {
                        result.Add(message.Id);
                    }
                }
            }

            pageToken = response.NextPageToken;

        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return result;
    }

    private static async Task DiagnoseExtraMessagesAsync(
        GmailService gmail,
        IEnumerable<string> gmailIds)
    {
        foreach (var gmailId in gmailIds)
        {
            try
            {
                var request = gmail.Users.Messages.Get("me", gmailId);

                request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata;

                var message = await request.ExecuteAsync();

                Console.WriteLine($"EXTRA {gmailId}: EXISTS in Gmail Thread={message.ThreadId}");
            }
            catch (GoogleApiException ex)
                when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.WriteLine($"EXTRA {gmailId}: NO LONGER EXISTS IN GMAIL");
            }
        }
    }
}
