namespace EmailScraper.Threading;

public static class ThreadBuilder
{
    public const int CurrentVersion = 2;

    public static async Task BuildAsync(
        string databasePath)
    {
        Console.WriteLine();
        Console.WriteLine("==========================================");
        Console.WriteLine(" THREAD RECONSTRUCTION - STEP 4");
        Console.WriteLine("==========================================");
        Console.WriteLine();

        var messages = await Database.GetAllMessagesForThreadingAsync(databasePath);

        Console.WriteLine($"Messages found: {messages.Count:N0}");

        Console.WriteLine();

        /*
         * Remove all previously generated thread data.
         * Thread reconstruction is completely rebuildable.
         */
        await Database.ClearThreadsAsync(databasePath);

        /*
         * Message-ID -> message
         *
         * Message-ID values are normalized because some
         * messages contain <id>, while others don't.
         */
        var byMessageId = messages
            .Where(x => !string.IsNullOrWhiteSpace(x.MessageId))
            .GroupBy(x => NormalizeMessageId(x.MessageId!))
            .ToDictionary(x => x.Key, x => x.First());

        /*
         * Statistics.
         */
        var messagesWithMessageId = messages.Count(x => !string.IsNullOrWhiteSpace(x.MessageId));
        var messagesWithInReplyTo = messages.Count(x => !string.IsNullOrWhiteSpace(x.InReplyTo));
        var messagesWithReferences = messages.Count(x => !string.IsNullOrWhiteSpace(x.References));

        Console.WriteLine($"Messages with Message-ID : {messagesWithMessageId:N0}");
        Console.WriteLine($"Messages with In-Reply-To: {messagesWithInReplyTo:N0}");
        Console.WriteLine($"Messages with References : {messagesWithReferences:N0}");

        Console.WriteLine();

        /*
         * ----------------------------------------------------
         * Build logical thread components.
         *
         * Two messages belong to the same logical thread when
         * their MIME headers establish a relationship.
         *
         * In-Reply-To:
         *
         *     child -> direct parent
         *
         * References:
         *
         *     child -> ancestors
         *
         * References are particularly important when an
         * intermediate message is missing from the archive.
         * ----------------------------------------------------
         */

        var unionFind = new UnionFind(messages);

        foreach (var message in messages)
        {
            /*
             * Strongest relationship:
             * In-Reply-To.
             */
            if (!string.IsNullOrWhiteSpace(message.InReplyTo))
            {
                var parentKey = NormalizeMessageId(message.InReplyTo);

                if (byMessageId.TryGetValue(parentKey, out var parent))
                    unionFind.Union(message.Id, parent.Id);
            }

            /*
             * References establish ancestry.
             *
             * If ANY referenced message exists in our archive,
             * connect the current message to it.
             *
             * This allows us to reconstruct a thread even when
             * one or more intermediate emails are missing.
             */
            if (!string.IsNullOrWhiteSpace(message.References))
            {
                var references = ParseReferences(message.References);

                foreach (var reference in references)
                {
                    if (byMessageId.TryGetValue(reference, out var referencedMessage))
                        unionFind.Union(message.Id, referencedMessage.Id);
                }
            }
        }

        /*
         * ----------------------------------------------------
         * Convert the connected components into thread groups.
         * ----------------------------------------------------
         */

        var threadGroups = messages
            .GroupBy(x => unionFind.Find(x.Id))
            .Select(x => x.ToList())
            .OrderBy(x => x.Min(GetDate))
            .ToList();

        /*
         * Messages which have no relationship whatsoever are
         * naturally represented as single-message components.
         */
        var standaloneMessages = threadGroups.Count(x =>
            x.Count == 1 && !HasThreadRelationship(x[0], byMessageId));

        Console.WriteLine($"Logical thread groups: {threadGroups.Count:N0}");
        Console.WriteLine($"Standalone messages   : {standaloneMessages:N0}");

        Console.WriteLine();

        /*
         * ----------------------------------------------------
         * Create the actual Thread records.
         * ----------------------------------------------------
         */

        var threadCount = 0;

        foreach (var group in threadGroups)
        {
            await CreateThreadAsync(databasePath, group, byMessageId);

            threadCount++;
            Console.Write($"\rThreads: {threadCount:N0}/{threadGroups.Count:N0}");
        }

        await Database.DeleteEmptyThreadsAsync(databasePath);

        Console.WriteLine();
        Console.WriteLine($"Threads created: {threadCount:N0}");

        Console.WriteLine("Thread reconstruction complete.");

        Console.WriteLine();
    }

    private static async Task<long> CreateThreadAsync(
        string databasePath,
        List<ThreadMessage> messages,
        Dictionary<string, ThreadMessage> byMessageId)
    {
        var ordered = messages
            .OrderBy(GetDate)
            .ThenBy(x => x.Id)
            .ToList();
        var first = ordered.First();
        var last = ordered.Last();
        var threadKey = BuildThreadKey(ordered);
        var threadId = await Database.InsertThreadAsync(databasePath, new ThreadRecord
        {
            ThreadKey = threadKey,
            Subject = CleanSubject(first.Subject),
            FirstDate = first.Date,
            LastDate = last.Date
        });

        /*
         * Build parent relationships.
         *
         * This is done AFTER the complete logical component
         * has been identified, so the parent can be anywhere
         * inside the thread.
         */
        var sortOrder = 0;

        foreach (var message in ordered)
        {
            long? parentId = null;

            /*
             * Strongest relationship:
             * In-Reply-To.
             */
            if (!string.IsNullOrWhiteSpace(message.InReplyTo))
            {
                var parentKey = NormalizeMessageId(message.InReplyTo);

                if (byMessageId.TryGetValue(parentKey, out var parent))
                {
                    /*
                     * Only use the parent if it belongs to
                     * this logical thread.
                     *
                     * This is normally guaranteed by the
                     * UnionFind construction, but keeping the
                     * check here protects against bad MIME data.
                     */
                    if (messages.Any(x => x.Id == parent.Id)) parentId = parent.Id;
                }
            }

            /*
             * If there was no direct In-Reply-To relationship,
             * use the most recent References entry that exists
             * in this thread.
             */
            if (!parentId.HasValue &&
                !string.IsNullOrWhiteSpace(message.References))
            {
                var references = ParseReferences(message.References);

                for (var i = references.Count - 1; i >= 0; i--)
                {
                    if (byMessageId.TryGetValue(references[i], out var parent))
                    {
                        if (messages.Any(x => x.Id == parent.Id))
                        {
                            parentId = parent.Id;
                            break;
                        }
                    }
                }
            }

            /*
             * Never allow a message to be its own parent.
             */
            if (parentId == message.Id) parentId = null;

            await Database.InsertMessageThreadAsync(databasePath, new MessageThreadRecord
            {
                ThreadId = threadId,
                MessageId = message.Id,
                ParentMessageId = parentId,
                SortOrder = sortOrder++
            });
        }

        return threadId;
    }

    private static bool HasThreadRelationship(
        ThreadMessage message,
        Dictionary<string, ThreadMessage> byMessageId)
    {
        /*
         * In-Reply-To relationship.
         */
        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            var key = NormalizeMessageId(message.InReplyTo);

            if (byMessageId.ContainsKey(key)) return true;
        }

        /*
         * References relationship.
         */
        if (!string.IsNullOrWhiteSpace(message.References))
        {
            foreach (var reference in ParseReferences(message.References))
            {
                if (byMessageId.ContainsKey(reference)) return true;
            }
        }

        return false;
    }

    private static string BuildThreadKey(
        List<ThreadMessage> messages)
    {
        /*
         * GmailThreadId is optional.
         *
         * If we eventually restore it, use it as a useful
         * stable identifier, but it is NOT required for
         * thread reconstruction.
         */
        var gmailThreadId = messages
            .Select(x => x.GmailThreadId)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        if (!string.IsNullOrWhiteSpace(gmailThreadId)) return $"gmail:{gmailThreadId}";

        /*
         * Otherwise use the Message-ID of the root/oldest
         * message as the logical thread key.
         */
        var first = messages
            .OrderBy(GetDate)
            .ThenBy(x => x.Id)
            .First();

        if (!string.IsNullOrWhiteSpace(first.MessageId)) return $"message:{NormalizeMessageId(first.MessageId)}";

        return $"message:{first.Id}";
    }

    private static List<string> ParseReferences(
        string value)
    {
        return value
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeMessageId)
            .Where(x => x.Length > 0)
            .ToList();
    }

    private static string NormalizeMessageId(
        string value)
    {
        return value
            .Trim()
            .Trim('<', '>')
            .ToLowerInvariant();
    }

    private static DateTimeOffset GetDate(
        ThreadMessage message)
    {
        return DateTimeOffset.TryParse(message.Date, out var date)
            ? date
            : DateTimeOffset.MinValue;
    }

    private static string? CleanSubject(
        string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;

        var result = subject.Trim();

        /*
         * Remove reply/forward prefixes.
         */
        while (true)
        {
            var upper = result.ToUpperInvariant();

            if (upper.StartsWith("RE:"))
            {
                result = result[3..].Trim();
                continue;
            }

            if (upper.StartsWith("FW:"))
            {
                result = result[3..].Trim();
                continue;
            }

            if (upper.StartsWith("FWD:"))
            {
                result = result[4..].Trim();
                continue;
            }

            break;
        }

        return result;
    }

    /*
     * ========================================================
     * Simple Union-Find / Disjoint Set implementation.
     *
     * Used to identify connected logical email conversations.
     * ========================================================
     */
    private sealed class UnionFind
    {
        private readonly Dictionary<long, long> parent;

        private readonly Dictionary<long, int> rank;

        public UnionFind(
            IEnumerable<ThreadMessage> messages)
        {
            parent = new Dictionary<long, long>();
            rank = new Dictionary<long, int>();

            foreach (var message in messages)
            {
                parent[message.Id] = message.Id;
                rank[message.Id] = 0;
            }
        }

        public long Find(long id)
        {
            if (parent[id] != id)
                parent[id] = Find(parent[id]);

            return parent[id];
        }

        public void Union(
            long first,
            long second)
        {
            var rootFirst = Find(first);
            var rootSecond = Find(second);

            if (rootFirst == rootSecond) return;

            var rankFirst = rank[rootFirst];
            var rankSecond = rank[rootSecond];

            if (rankFirst < rankSecond)
                parent[rootFirst] = rootSecond;
            else if (rankFirst > rankSecond)
                parent[rootSecond] = rootFirst;
            else
            {
                parent[rootSecond] = rootFirst;
                rank[rootFirst]++;
            }
        }
    }
}
