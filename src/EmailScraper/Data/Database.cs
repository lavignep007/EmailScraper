using Microsoft.Data.Sqlite;

namespace EmailScraper.Data;

public static class Database
{
    public static async Task InitializeAsync(
        string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
    CREATE TABLE IF NOT EXISTS Messages
    (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,

        GmailId TEXT NOT NULL UNIQUE,
        GmailThreadId TEXT,

        MessageId TEXT,
        InReplyTo TEXT,
        ReferencesHeader TEXT,

        Date TEXT,
        Subject TEXT,

        FromAddress TEXT,
        ToAddresses TEXT,
        CcAddresses TEXT,
        BccAddresses TEXT,

        MessageType TEXT NOT NULL DEFAULT 'Email',
        ReactionEmoji TEXT,

        FilePath TEXT NOT NULL,

        ImportedUtc TEXT,
        ParsedUtc TEXT,
        ParserVersion INTEGER,

        RelativePath TEXT,
        DisplayName TEXT
    );

    CREATE TABLE IF NOT EXISTS SyncState
    (
        Key TEXT PRIMARY KEY,
        Value TEXT
    );

    CREATE TABLE IF NOT EXISTS Attachments
    (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,

        Sha256 TEXT NOT NULL UNIQUE,

        FileName TEXT,

        ContentType TEXT,

        Size INTEGER NOT NULL,

        FilePath TEXT NOT NULL
    );

    CREATE TABLE IF NOT EXISTS MessageAttachments
    (
        MessageId INTEGER NOT NULL,

        AttachmentId INTEGER NOT NULL,

        ContentId TEXT,

        IsInline INTEGER NOT NULL DEFAULT 0,

        PRIMARY KEY
        (
            MessageId,
            AttachmentId,
            ContentId
        ),

        FOREIGN KEY
        (
            MessageId
        )
        REFERENCES Messages(Id),

        FOREIGN KEY
        (
            AttachmentId
        )
        REFERENCES Attachments(Id)
    );

    CREATE TABLE IF NOT EXISTS Threads
    (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,

        ThreadKey TEXT NOT NULL UNIQUE,

        Subject TEXT,

        FirstDate TEXT,
        LastDate TEXT,

        Name TEXT
    );

    CREATE TABLE IF NOT EXISTS MessageThreads
    (
        ThreadId INTEGER NOT NULL,

        MessageId INTEGER NOT NULL,

        ParentMessageId INTEGER,

        SortOrder INTEGER NOT NULL DEFAULT 0,

        PRIMARY KEY
        (
            ThreadId,
            MessageId
        ),

        FOREIGN KEY
        (
            ThreadId
        )
        REFERENCES Threads(Id),

        FOREIGN KEY
        (
            MessageId
        )
        REFERENCES Messages(Id),

        FOREIGN KEY
        (
            ParentMessageId
        )
        REFERENCES Messages(Id)
    );

        CREATE TABLE IF NOT EXISTS SearchDocuments
    (
        MessageId INTEGER PRIMARY KEY,

        ThreadId INTEGER,

        Date TEXT,

        Subject TEXT,

        FromAddress TEXT,

        ToAddresses TEXT,

        CcAddresses TEXT,

        Body TEXT,

        AttachmentNames TEXT,

        FOREIGN KEY (MessageId)
            REFERENCES Messages(Id),

        FOREIGN KEY (ThreadId)
            REFERENCES Threads(Id)
    );

    CREATE VIRTUAL TABLE IF NOT EXISTS SearchIndex
    USING fts5
    (
        Subject,
        FromAddress,
        ToAddresses,
        CcAddresses,
        Body,
        AttachmentNames,

        content='SearchDocuments',
        content_rowid='MessageId',
        tokenize='unicode61 remove_diacritics 2'
    );

    CREATE INDEX IF NOT EXISTS
        IX_Attachments_Sha256
        ON Attachments(Sha256);

    CREATE INDEX IF NOT EXISTS
        IX_MessageAttachments_AttachmentId
        ON MessageAttachments(AttachmentId);

    CREATE INDEX IF NOT EXISTS
        IX_MessageAttachments_MessageId
        ON MessageAttachments(MessageId);

    CREATE INDEX IF NOT EXISTS
        IX_Messages_ParserVersion
        ON Messages(ParserVersion);

    CREATE INDEX IF NOT EXISTS
        IX_MessageThreads_MessageId
        ON MessageThreads(MessageId);

    CREATE INDEX IF NOT EXISTS
        IX_MessageThreads_ParentMessageId
        ON MessageThreads(ParentMessageId);

    CREATE INDEX IF NOT EXISTS
        IX_Threads_FirstDate
        ON Threads(FirstDate);
    """;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        var command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM pragma_table_info('{table}')
            WHERE name = $column;
            """;

        command.Parameters.AddWithValue("$column", column);

        var result = await command.ExecuteScalarAsync();
        var exists = Convert.ToInt64(result) > 0;

        if (exists) return;

        command = connection.CreateCommand();

        command.CommandText =
            $"ALTER TABLE {table} " +
            $"ADD COLUMN {column} {definition};";

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<bool> MessageExistsAsync(
        string databasePath,
        string gmailId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*)
            FROM Messages
            WHERE GmailId = $gmailId
            """;

        command.Parameters.AddWithValue("$gmailId", gmailId);

        var result = await command.ExecuteScalarAsync();

        return Convert.ToInt64(result) > 0;
    }

    public static async Task InsertMessageAsync(
        string databasePath,
        MessageRecord message)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO Messages
            (
                GmailId,
                GmailThreadId,
                MessageId,
                InReplyTo,
                ReferencesHeader,
                Date,
                Subject,
                FromAddress,
                ToAddresses,
                CcAddresses,
                BccAddresses,
                MessageType,
                ReactionEmoji,
                FilePath,
                ImportedUtc
            )
            VALUES
            (
                $gmailId,
                $gmailThreadId,
                $messageId,
                $inReplyTo,
                $references,
                $date,
                $subject,
                $from,
                $to,
                $cc,
                $bcc,
                $messageType,
                $reactionEmoji,
                $filePath,
                $importedUtc
            );
            """;

        command.Parameters.AddWithValue("$gmailId", message.GmailId);
        command.Parameters.AddWithValue("$gmailThreadId", (object?)message.GmailThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$messageId", (object?)message.MessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$inReplyTo", (object?)message.InReplyTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$references", (object?)message.References ?? DBNull.Value);
        command.Parameters.AddWithValue("$date", (object?)message.Date ?? DBNull.Value);
        command.Parameters.AddWithValue("$subject", (object?)message.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", (object?)message.From ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)message.To ?? DBNull.Value);
        command.Parameters.AddWithValue("$cc", (object?)message.Cc ?? DBNull.Value);
        command.Parameters.AddWithValue("$bcc", (object?)message.Bcc ?? DBNull.Value);
        command.Parameters.AddWithValue("$messageType", message.MessageType);
        command.Parameters.AddWithValue("$reactionEmoji", (object?)message.ReactionEmoji ?? DBNull.Value);
        command.Parameters.AddWithValue("$filePath", message.FilePath);
        command.Parameters.AddWithValue("$importedUtc", DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<string?> GetSyncStateAsync(
        string databasePath,
        string key)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
            SELECT Value
            FROM SyncState
            WHERE Key = $key
            """;

        command.Parameters.AddWithValue("$key", key);

        var result = await command.ExecuteScalarAsync();

        return result == null ||
               result == DBNull.Value
            ? null
            : result.ToString();
    }

    public static async Task SetSyncStateAsync(
        string databasePath,
        string key,
        string value)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO SyncState
                (Key, Value)
            VALUES
                ($key, $value)
            ON CONFLICT(Key)
            DO UPDATE SET
                Value = excluded.Value;
            """;

        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<DatabaseMessage?> GetMessageByGmailIdAsync(
        string databasePath,
        string gmailId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        SELECT
            Id,
            GmailId,
            ParserVersion
        FROM Messages
        WHERE GmailId = $gmailId;
        """;

        command.Parameters.AddWithValue("$gmailId", gmailId);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return null;

        return new DatabaseMessage
        {
            Id = reader.GetInt64(0),
            GmailId = reader.GetString(1),
            ParserVersion = reader.IsDBNull(2) ? null : reader.GetInt32(2)
        };
    }

    public static async Task MarkParsedAsync(
        string databasePath,
        long messageId,
        int parserVersion)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        UPDATE Messages
        SET
            ParsedUtc = $parsedUtc,
            ParserVersion = $parserVersion
        WHERE Id = $messageId;
        """;

        command.Parameters.AddWithValue("$parsedUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$parserVersion", parserVersion);
        command.Parameters.AddWithValue("$messageId", messageId);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task UpdateMessageMetadataAsync(
        string databasePath,
        long messageId,
        MessageRecord message)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
    UPDATE Messages
    SET
        GmailThreadId = $gmailThreadId,
        MessageId = $messageIdHeader,
        InReplyTo = $inReplyTo,
        ReferencesHeader = $references,
        Date = $date,
        Subject = $subject,
        FromAddress = $from,
        ToAddresses = $to,
        CcAddresses = $cc
    WHERE Id = $id;
    """;

        command.Parameters.AddWithValue("$gmailThreadId", (object?)message.GmailThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$messageIdHeader", (object?)message.MessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$inReplyTo", (object?)message.InReplyTo ?? DBNull.Value);
        command.Parameters.AddWithValue("$references", (object?)message.References ?? DBNull.Value);
        command.Parameters.AddWithValue("$date", (object?)message.Date ?? DBNull.Value);
        command.Parameters.AddWithValue("$subject", (object?)message.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", (object?)message.From ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)message.To ?? DBNull.Value);
        command.Parameters.AddWithValue("$cc", (object?)message.Cc ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", messageId);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> GetOrCreateAttachmentAsync(
        string databasePath,
        string sha256,
        string fileName,
        string contentType,
        long size,
        string filePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        INSERT INTO Attachments
        (
            Sha256,
            FileName,
            ContentType,
            Size,
            FilePath
        )
        VALUES
        (
            $sha256,
            $fileName,
            $contentType,
            $size,
            $filePath
        )
        ON CONFLICT(Sha256)
        DO NOTHING;

        SELECT Id
        FROM Attachments
        WHERE Sha256 = $sha256;
        """;

        command.Parameters.AddWithValue("$sha256", sha256);
        command.Parameters.AddWithValue("$fileName", fileName);
        command.Parameters.AddWithValue("$contentType", contentType);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$filePath", filePath);

        var result = await command.ExecuteScalarAsync();

        return Convert.ToInt64(result);
    }

    public static async Task AddMessageAttachmentAsync(
        string databasePath,
        long messageId,
        long attachmentId,
        string? contentId,
        bool isInline)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        INSERT OR IGNORE INTO
            MessageAttachments
        (
            MessageId,
            AttachmentId,
            ContentId,
            IsInline
        )
        VALUES
        (
            $messageId,
            $attachmentId,
            $contentId,
            $isInline
        );
        """;

        command.Parameters.AddWithValue("$messageId", messageId);
        command.Parameters.AddWithValue("$attachmentId", attachmentId);
        command.Parameters.AddWithValue("$contentId", (object?)contentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$isInline", isInline ? 1 : 0);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task UpdateReactionAsync(
        string databasePath,
        long messageId,
        string? emoji)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        UPDATE Messages
        SET
            MessageType = 'Reaction',
            ReactionEmoji = $emoji
        WHERE Id = $messageId;
        """;

        command.Parameters.AddWithValue("$emoji", (object?)emoji ?? DBNull.Value);
        command.Parameters.AddWithValue("$messageId", messageId);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<ThreadMessage>> GetAllMessagesForThreadingAsync(
        string databasePath)
    {
        var result = new List<ThreadMessage>();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        SELECT
            Id,
            GmailId,
            GmailThreadId,
            MessageId,
            InReplyTo,
            ReferencesHeader,
            Date,
            Subject,
            FromAddress,
            ToAddresses,
            CcAddresses,
            BccAddresses,
            MessageType
        FROM Messages
        ORDER BY Date;
        """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new ThreadMessage
            {
                Id = reader.GetInt64(0),
                GmailId = reader.GetString(1),
                GmailThreadId = reader.IsDBNull(2) ? null : reader.GetString(2),
                MessageId = reader.IsDBNull(3) ? null : reader.GetString(3),
                InReplyTo = reader.IsDBNull(4) ? null : reader.GetString(4),
                References = reader.IsDBNull(5) ? null : reader.GetString(5),
                Date = reader.IsDBNull(6) ? null : reader.GetString(6),
                Subject = reader.IsDBNull(7) ? null : reader.GetString(7),
                From = reader.IsDBNull(8) ? null : reader.GetString(8),
                To = reader.IsDBNull(9) ? null : reader.GetString(9),
                Cc = reader.IsDBNull(10) ? null : reader.GetString(10),
                Bcc = reader.IsDBNull(11) ? null : reader.GetString(11),
                MessageType = reader.IsDBNull(12) ? "Email" : reader.GetString(12)
            });
        }

        return result;
    }

    public static async Task ClearThreadsAsync(
        string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        DELETE FROM SearchDocuments;
        DELETE FROM MessageThreads;        
        """;

        await command.ExecuteNonQueryAsync();

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> InsertThreadAsync(
        string databasePath,
        ThreadRecord thread)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        INSERT INTO Threads
        (
            ThreadKey,
            Subject,
            FirstDate,
            LastDate,
            Name
        )
        VALUES
        (
            $threadKey,
            $subject,
            $firstDate,
            $lastDate,
            $name
        )
        ON CONFLICT(ThreadKey)
        DO UPDATE SET
            Subject = excluded.Subject,
            FirstDate = excluded.FirstDate,
            LastDate = excluded.LastDate,
            Name = excluded.Name;

        SELECT Id
        FROM Threads
        WHERE ThreadKey = $threadKey;
        """;

        command.Parameters.AddWithValue("$threadKey", thread.ThreadKey);
        command.Parameters.AddWithValue("$subject", (object?)thread.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$firstDate", (object?)thread.FirstDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastDate", (object?)thread.LastDate ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)thread.Name ?? DBNull.Value);

        var result = await command.ExecuteScalarAsync();

        return Convert.ToInt64(result);
    }

    public static async Task InsertMessageThreadAsync(
        string databasePath,
        MessageThreadRecord record)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        INSERT INTO MessageThreads
        (
            ThreadId,
            MessageId,
            ParentMessageId,
            SortOrder
        )
        VALUES
        (
            $threadId,
            $messageId,
            $parentMessageId,
            $sortOrder
        );
        """;

        command.Parameters.AddWithValue("$threadId", record.ThreadId);
        command.Parameters.AddWithValue("$messageId", record.MessageId);
        command.Parameters.AddWithValue("$parentMessageId", (object?)record.ParentMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sortOrder", record.SortOrder);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<ThreadViewRow>> GetThreadViewAsync(
        string databasePath)
    {
        var result = new List<ThreadViewRow>();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        SELECT
            t.Id,
            t.Subject,
            t.FirstDate,
            t.LastDate,

            mt.SortOrder,
            mt.ParentMessageId,

            m.Id,
            m.MessageType,
            m.Date,
            m.FromAddress,
            m.ToAddresses,
            m.Subject,
            m.MessageId,
            m.InReplyTo,
            m.ReferencesHeader,
            m.ReactionEmoji

        FROM Threads t

        INNER JOIN MessageThreads mt
            ON mt.ThreadId = t.Id

        INNER JOIN Messages m
            ON m.Id = mt.MessageId

        ORDER BY
            t.Id,
            mt.SortOrder;
        """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new ThreadViewRow
            {
                ThreadId = reader.GetInt64(0),
                ThreadSubject = reader.IsDBNull(1) ? null : reader.GetString(1),
                FirstDate = reader.IsDBNull(2) ? null : reader.GetString(2),
                LastDate = reader.IsDBNull(3) ? null : reader.GetString(3),
                SortOrder = reader.GetInt32(4),
                ParentMessageId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                MessageId = reader.GetInt64(6),
                MessageType = reader.GetString(7),
                Date = reader.IsDBNull(8) ? null : reader.GetString(8),
                From = reader.IsDBNull(9) ? null : reader.GetString(9),
                To = reader.IsDBNull(10) ? null : reader.GetString(10),
                Subject = reader.IsDBNull(11) ? null : reader.GetString(11),
                InternetMessageId = reader.IsDBNull(12) ? null : reader.GetString(12),
                InReplyTo = reader.IsDBNull(13) ? null : reader.GetString(13),
                References = reader.IsDBNull(14) ? null : reader.GetString(14),
                ReactionEmoji = reader.IsDBNull(15) ? null : reader.GetString(15)
            });
        }

        return result;
    }

    public static async Task UpdateMessageOrganizationAsync(
        string databasePath,
        long messageId,
        string relativePath,
        string displayName)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        UPDATE Messages
        SET
            RelativePath = $relativePath,
            DisplayName = $displayName
        WHERE Id = $messageId;
        """;

        command.Parameters.AddWithValue("$relativePath", relativePath);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$messageId", messageId);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task UpdateThreadNameAsync(
        string databasePath,
        long threadId,
        string name)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        UPDATE Threads
        SET Name = $name
        WHERE Id = $threadId;
        """;

        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$threadId", threadId);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task SetThreadNamesFromSubjectsAsync(
        string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        UPDATE Threads
        SET Name =
            CASE
                WHEN Subject IS NULL
                    OR TRIM(Subject) = ''
                THEN '(no subject)'
                ELSE Subject
            END;
        """;

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<PdfMessage>> GetPdfMessagesForThreadAsync(
        string databasePath,
        long threadId)
    {
        var result = new List<PdfMessage>();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        SELECT
            m.Id,
            mt.ThreadId,
            mt.SortOrder,
            mt.ParentMessageId,

            m.MessageType,
            m.ReactionEmoji,
            m.Date,
            m.Subject,
            m.FromAddress,
            m.ToAddresses,
            m.CcAddresses,
            m.BccAddresses,

            m.RelativePath,
            m.DisplayName,

            t.Name

        FROM MessageThreads mt

        INNER JOIN Messages m
            ON m.Id = mt.MessageId

        INNER JOIN Threads t
            ON t.Id = mt.ThreadId

        WHERE mt.ThreadId = $threadId

        ORDER BY
            mt.SortOrder;
        """;

        command.Parameters.AddWithValue("$threadId", threadId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new PdfMessage
            {
                Id = reader.GetInt64(0),
                ThreadId = reader.GetInt64(1),
                SortOrder = reader.GetInt32(2),
                ParentMessageId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                MessageType = reader.GetString(4),
                ReactionEmoji = reader.IsDBNull(5) ? null : reader.GetString(5),
                Date = reader.IsDBNull(6) ? null : reader.GetString(6),
                Subject = reader.IsDBNull(7) ? null : reader.GetString(7),
                From = reader.IsDBNull(8) ? null : reader.GetString(8),
                To = reader.IsDBNull(9) ? null : reader.GetString(9),
                Cc = reader.IsDBNull(10) ? null : reader.GetString(10),
                Bcc = reader.IsDBNull(11) ? null : reader.GetString(11),
                RelativePath = reader.IsDBNull(12) ? null : reader.GetString(12),
                DisplayName = reader.IsDBNull(13) ? null : reader.GetString(13),
                ThreadName = reader.IsDBNull(14) ? null : reader.GetString(14)
            });
        }

        return result;
    }

    public static async Task<List<ThreadRecord>> GetAllThreadsAsync(
        string databasePath)
    {
        var result = new List<ThreadRecord>();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        SELECT
            Id,
            ThreadKey,
            Name,
            Subject,
            FirstDate,
            LastDate
        FROM Threads
        ORDER BY
            FirstDate,
            Id;
        """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new ThreadRecord
            {
                Id = reader.GetInt64(0),
                ThreadKey = reader.GetString(1),
                Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                Subject = reader.IsDBNull(3) ? null : reader.GetString(3),
                FirstDate = reader.IsDBNull(4) ? null : reader.GetString(4),
                LastDate = reader.IsDBNull(5) ? null : reader.GetString(5)
            });
        }

        return result;
    }

    public static async Task<List<PdfAttachment>> GetAttachmentsForMessageAsync(
        string databasePath,
        long messageId)
    {
        var result = new List<PdfAttachment>();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        SELECT
            a.FileName,
            a.ContentType,
            a.Size,
            a.FilePath,
            ma.IsInline

        FROM MessageAttachments ma

        INNER JOIN Attachments a
            ON a.Id = ma.AttachmentId

        WHERE ma.MessageId = $messageId

        ORDER BY
            a.FileName;
        """;

        command.Parameters.AddWithValue("$messageId", messageId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new PdfAttachment
            {
                FileName = reader.IsDBNull(0) ? "(unnamed)" : reader.GetString(0),
                ContentType = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Size = reader.GetInt64(2),
                FilePath = reader.GetString(3),
                IsInline = reader.GetInt64(4) != 0
            });
        }

        return result;
    }

    public static async Task<List<EmlRepairRecord>> GetMessagesForEmlValidationAsync(
        string databasePath)
    {
        var result = new List<EmlRepairRecord>();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        SELECT
            Id,
            GmailId,
            FilePath
        FROM Messages
        ORDER BY Id;
        """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new EmlRepairRecord
            {
                Id = reader.GetInt64(0),
                GmailId = reader.GetString(1),
                FilePath = reader.IsDBNull(2) ? "" : reader.GetString(2)
            });
        }

        return result;
    }

    public static async Task UpdateGmailThreadIdAsync(
        string databasePath,
        long messageId,
    string? gmailThreadId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
        UPDATE Messages
        SET GmailThreadId = $gmailThreadId
        WHERE Id = $id;
        """;

        command.Parameters.AddWithValue("$gmailThreadId", (object?)gmailThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", messageId);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<SearchSourceMessage>> GetMessagesForSearchIndexAsync(
        string databasePath)
    {
        var result = new List<SearchSourceMessage>();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
    SELECT
        m.Id,
        mt.ThreadId,
        m.Date,
        m.Subject,
        m.FromAddress,
        m.ToAddresses,
        m.CcAddresses,
        m.FilePath,

        (
            SELECT GROUP_CONCAT(a.FileName, ' ')
            FROM MessageAttachments ma
            INNER JOIN Attachments a
                ON a.Id = ma.AttachmentId
            WHERE ma.MessageId = m.Id
        ) AS AttachmentNames

    FROM Messages m

    LEFT JOIN MessageThreads mt
        ON mt.MessageId = m.Id

    ORDER BY m.Id;
    """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new SearchSourceMessage
            {
                MessageId = reader.GetInt64(0),
                ThreadId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                Date = reader.IsDBNull(2) ? null : reader.GetString(2),
                Subject = reader.IsDBNull(3) ? null : reader.GetString(3),
                From = reader.IsDBNull(4) ? null : reader.GetString(4),
                To = reader.IsDBNull(5) ? null : reader.GetString(5),
                Cc = reader.IsDBNull(6) ? null : reader.GetString(6),
                FilePath = reader.GetString(7),
                AttachmentNames = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return result;
    }

    public static async Task InsertSearchDocumentAsync(
        string databasePath,
        SearchSourceMessage message,
        string body)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
    INSERT OR REPLACE INTO SearchDocuments
    (
        MessageId,
        ThreadId,
        Date,
        Subject,
        FromAddress,
        ToAddresses,
        CcAddresses,
        Body,
        AttachmentNames
    )
    VALUES
    (
        $messageId,
        $threadId,
        $date,
        $subject,
        $from,
        $to,
        $cc,
        $body,
        $attachmentNames
    );
    """;

        command.Parameters.AddWithValue("$messageId", message.MessageId);
        command.Parameters.AddWithValue("$threadId", (object?)message.ThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue("$date", (object?)message.Date ?? DBNull.Value);
        command.Parameters.AddWithValue("$subject", (object?)message.Subject ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", (object?)message.From ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)message.To ?? DBNull.Value);
        command.Parameters.AddWithValue("$cc", (object?)message.Cc ?? DBNull.Value);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$attachmentNames", (object?)message.AttachmentNames ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    public static async Task RebuildSearchIndexAsync(
        string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
    INSERT INTO SearchIndex(SearchIndex)
    VALUES('rebuild');
    """;

        await command.ExecuteNonQueryAsync();
    }

    public static async Task<List<SearchResult>> SearchAsync(
        string databasePath,
        string searchText,
        int limit = 50)
    {
        var result = new List<SearchResult>();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
    SELECT
        d.MessageId,
        d.ThreadId,
        d.Date,
        d.Subject,
        d.FromAddress,

        snippet(
            SearchIndex,
            4,
            '[[',
            ']]',
            ' ... ',
            20
        ),

        bm25(SearchIndex)

    FROM SearchIndex

    INNER JOIN SearchDocuments d
        ON d.MessageId = SearchIndex.rowid

    WHERE SearchIndex MATCH $search

    ORDER BY bm25(SearchIndex)

    LIMIT $limit;
    """;

        command.Parameters.AddWithValue("$search", searchText);
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new SearchResult
            {
                MessageId = reader.GetInt64(0),
                ThreadId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                Date = reader.IsDBNull(2) ? null : reader.GetString(2),
                Subject = reader.IsDBNull(3) ? null : reader.GetString(3),
                From = reader.IsDBNull(4) ? null : reader.GetString(4),
                Snippet = reader.IsDBNull(5) ? null : reader.GetString(5),
                Rank = reader.GetDouble(6)
            });
        }

        return result;
    }

    public static async Task<List<ValidationMessage>> GetMessagesForValidationAsync(
        string databasePath)
    {
        var result = new List<ValidationMessage>();

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
    SELECT
        Id,
        GmailId,
        MessageId,
        FilePath
    FROM Messages
    ORDER BY Id;
    """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new ValidationMessage
            {
                Id = reader.GetInt64(0),
                GmailId = reader.GetString(1),
                MessageId = reader.IsDBNull(2) ? null : reader.GetString(2),
                FilePath = reader.GetString(3)
            });
        }

        return result;
    }

    public static async Task<int> ExecuteCountAsync(
        string databasePath,
        string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText =
            sql;

        var value = await command.ExecuteScalarAsync();

        return Convert.ToInt32(value);
    }

    public static async Task<HashSet<string>> GetAllGmailIdsAsync(
        string databasePath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");

        await connection.OpenAsync();

        var command = connection.CreateCommand();

        command.CommandText = """
    SELECT GmailId
    FROM Messages
    WHERE GmailId IS NOT NULL
      AND TRIM(GmailId) <> '';
    """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}
