namespace EmailScraper.Threading;

using System.Security.Cryptography;
using System.Text;

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

        var allMessages = await Database.GetAllMessagesForThreadingAsync(databasePath);
        var messages = SelectCanonicalThreadCopies(allMessages);
        var existingThreads = await Database.GetExistingThreadsAsync(databasePath);

        Console.WriteLine($"Messages found:             {allMessages.Count:N0}");
        Console.WriteLine($"Canonical threading copies: {messages.Count:N0}");
        Console.WriteLine($"Duplicate source copies:    {allMessages.Count - messages.Count:N0}");

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
            .ToDictionary(
                x => x.Key,
                x => x.OrderBy(GetCanonicalMessageIdentity, StringComparer.Ordinal).First());

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

        var connectedComponents = messages
            .GroupBy(x => unionFind.Find(x.Id))
            .Select(x => x.ToList())
            .OrderBy(x => x.Min(GetDate))
            .ToList();

        var threadGroups = connectedComponents
            .SelectMany(x => BuildReplyPaths(x, byMessageId))
            .Where(x => x.Count > 1 || HasDeclaredThreadRelationship(x[0]))
            .OrderBy(x => x.Min(GetDate))
            .ThenBy(BuildThreadKey, StringComparer.Ordinal)
            .ToList();

        /*
         * Messages which have no relationship whatsoever are
         * naturally represented as single-message components.
         */
        var threadedMessageIds = threadGroups.SelectMany(x => x).Select(x => x.Id).ToHashSet();
        var standaloneMessages = allMessages.Count(x => !threadedMessageIds.Contains(x.Id));

        Console.WriteLine($"Logical thread groups: {threadGroups.Count:N0}");
        Console.WriteLine($"Standalone messages   : {standaloneMessages:N0}");

        Console.WriteLine();

        /*
         * ----------------------------------------------------
         * Create the actual Thread records.
         * ----------------------------------------------------
         */

        var threadCount = 0;
        var assignments = MatchExistingThreads(existingThreads, threadGroups);
        var assignedExistingIds = assignments.Values.Select(x => x.Id).ToHashSet();
        var currentThreadIds = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var obsolete in existingThreads.Where(x => !assignedExistingIds.Contains(x.Id)))
            await Database.DeactivateThreadAsync(databasePath, obsolete.Id);

        foreach (var group in threadGroups)
        {
            var key = BuildThreadKey(group);
            assignments.TryGetValue(key, out var existing);
            var threadId = await CreateThreadAsync(databasePath, group, byMessageId, existing);
            currentThreadIds[key] = threadId;

            if (existing == null)
            {
                var splitFrom = existingThreads
                    .Where(x => x.MessageIds.Intersect(group.Select(m => m.Id)).Any())
                    .OrderByDescending(x => x.MessageIds.Intersect(group.Select(m => m.Id)).Count())
                    .ThenBy(x => x.Id)
                    .FirstOrDefault();

                if (splitFrom != null && assignedExistingIds.Contains(splitFrom.Id))
                    await Database.AddThreadRelationAsync(databasePath, splitFrom.Id, threadId, "Split");
            }

            threadCount++;
            Console.Write($"\rThreads: {threadCount:N0}/{threadGroups.Count:N0}");
        }

        foreach (var obsolete in existingThreads.Where(x => !assignedExistingIds.Contains(x.Id)))
        {
            var successor = threadGroups
                .Select(group => new
                {
                    Key = BuildThreadKey(group),
                    Overlap = group.Count(message => obsolete.MessageIds.Contains(message.Id))
                })
                .Where(x => x.Overlap > 0)
                .OrderByDescending(x => x.Overlap)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .FirstOrDefault();

            if (successor != null)
                await Database.AddThreadRelationAsync(
                    databasePath, obsolete.Id, currentThreadIds[successor.Key], "SupersededBy");
        }

        Console.WriteLine();
        Console.WriteLine($"Threads created: {threadCount:N0}");

        Console.WriteLine("Thread reconstruction complete.");

        Console.WriteLine();
    }

    private static async Task<long> CreateThreadAsync(
        string databasePath,
        List<ThreadMessage> messages,
        Dictionary<string, ThreadMessage> byMessageId,
        ExistingThread? existing)
    {
        var ordered = messages
            .OrderBy(GetDate)
            .ThenBy(GetCanonicalMessageIdentity, StringComparer.Ordinal)
            .ToList();
        var first = ordered.First();
        var last = ordered.Last();
        var threadKey = BuildThreadKey(ordered);
        var missingAncestorCount = GetMissingAncestorCount(ordered, byMessageId);
        var record = new ThreadRecord
        {
            Id = existing?.Id ?? 0,
            ThreadKey = threadKey,
            ProviderThreadId = ordered
                .Select(x => x.ProviderThreadId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .OrderBy(x => x, StringComparer.Ordinal)
                .FirstOrDefault(),
            Subject = CleanSubject(first.Subject),
            FirstDate = first.Date,
            LastDate = last.Date,
            RevisionHash = BuildRevisionHash(ordered, byMessageId),
            IsPartial = missingAncestorCount > 0,
            MissingAncestorCount = missingAncestorCount
        };
        long threadId;

        if (existing == null)
            threadId = await Database.InsertThreadAsync(databasePath, record);
        else
        {
            await Database.UpdateThreadAsync(databasePath, record);
            threadId = existing.Id;
        }

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

        await Database.RecordThreadRevisionAsync(
            databasePath, threadId, threadKey, record.RevisionHash);

        return threadId;
    }

    private static Dictionary<string, ExistingThread> MatchExistingThreads(
        List<ExistingThread> existingThreads,
        List<List<ThreadMessage>> groups)
    {
        var result = new Dictionary<string, ExistingThread>(StringComparer.Ordinal);
        var availableGroups = groups.ToDictionary(BuildThreadKey, x => x, StringComparer.Ordinal);
        var usedExisting = new HashSet<long>();

        foreach (var existing in existingThreads.OrderBy(x => x.Id))
        {
            if (!availableGroups.ContainsKey(existing.ThreadKey)) continue;

            result[existing.ThreadKey] = existing;
            availableGroups.Remove(existing.ThreadKey);
            usedExisting.Add(existing.Id);
        }

        foreach (var existing in existingThreads.Where(x => !usedExisting.Contains(x.Id)).OrderBy(x => x.Id))
        {
            var match = availableGroups
                .Select(x => new
                {
                    x.Key,
                    Overlap = x.Value.Count(message => existing.MessageIds.Contains(message.Id))
                })
                .Where(x => x.Overlap > 0)
                .OrderByDescending(x => x.Overlap)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .FirstOrDefault();

            if (match == null) continue;

            result[match.Key] = existing;
            availableGroups.Remove(match.Key);
        }

        return result;
    }

    private static string BuildRevisionHash(
        List<ThreadMessage> messages,
        Dictionary<string, ThreadMessage> byMessageId)
    {
        var value = string.Join("\n", messages.Select((message, index) =>
        {
            var componentIds = messages.Select(x => x.Id).ToHashSet();
            var parentId = ResolveParent(message, componentIds, byMessageId);
            var parent = parentId.HasValue
                ? messages.FirstOrDefault(x => x.Id == parentId.Value)
                : null;
            return $"{index}|{GetCanonicalMessageIdentity(message)}|" +
                (parent == null ? "" : GetCanonicalMessageIdentity(parent));
        }));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
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

    private static bool HasDeclaredThreadRelationship(ThreadMessage message) =>
        !string.IsNullOrWhiteSpace(message.InReplyTo) ||
        !string.IsNullOrWhiteSpace(message.References);

    private static int GetMissingAncestorCount(
        IEnumerable<ThreadMessage> messages,
        Dictionary<string, ThreadMessage> byMessageId)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message.InReplyTo))
            {
                var parent = NormalizeMessageId(message.InReplyTo);
                if (parent.Length > 0 && !byMessageId.ContainsKey(parent)) missing.Add(parent);
            }

            if (!string.IsNullOrWhiteSpace(message.References))
                foreach (var reference in ParseReferences(message.References))
                    if (!byMessageId.ContainsKey(reference)) missing.Add(reference);
        }

        return missing.Count;
    }

    private static List<List<ThreadMessage>> BuildReplyPaths(
        List<ThreadMessage> component,
        Dictionary<string, ThreadMessage> byMessageId)
    {
        var componentIds = component.Select(x => x.Id).ToHashSet();
        var parentByMessage = component.ToDictionary(
            x => x.Id,
            x => ResolveParent(x, componentIds, byMessageId));
        var childrenByMessage = component.ToDictionary(
            x => x.Id,
            _ => new List<ThreadMessage>());

        foreach (var message in component)
        {
            var parentId = parentByMessage[message.Id];

            if (parentId.HasValue && childrenByMessage.TryGetValue(parentId.Value, out var children))
                children.Add(message);
        }

        foreach (var children in childrenByMessage.Values)
            children.Sort(CompareMessages);

        var roots = component
            .Where(x => !parentByMessage[x.Id].HasValue)
            .OrderBy(GetDate)
            .ThenBy(GetCanonicalMessageIdentity, StringComparer.Ordinal)
            .ToList();
        var paths = new List<List<ThreadMessage>>();

        foreach (var root in roots)
            AddReplyPaths(root, childrenByMessage, [], paths, new HashSet<long>());

        return paths;
    }

    private static long? ResolveParent(
        ThreadMessage message,
        HashSet<long> componentIds,
        Dictionary<string, ThreadMessage> byMessageId)
    {
        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
        {
            var key = NormalizeMessageId(message.InReplyTo);

            if (byMessageId.TryGetValue(key, out var parent) &&
                parent.Id != message.Id && componentIds.Contains(parent.Id))
                return parent.Id;
        }

        if (!string.IsNullOrWhiteSpace(message.References))
        {
            var references = ParseReferences(message.References);

            for (var i = references.Count - 1; i >= 0; i--)
            {
                if (byMessageId.TryGetValue(references[i], out var parent) &&
                    parent.Id != message.Id && componentIds.Contains(parent.Id))
                    return parent.Id;
            }
        }

        return null;
    }

    private static void AddReplyPaths(
        ThreadMessage message,
        Dictionary<long, List<ThreadMessage>> childrenByMessage,
        List<ThreadMessage> ancestors,
        List<List<ThreadMessage>> paths,
        HashSet<long> activePath)
    {
        if (!activePath.Add(message.Id)) return;

        var path = new List<ThreadMessage>(ancestors) { message };
        var children = childrenByMessage[message.Id]
            .Where(x => !activePath.Contains(x.Id))
            .ToList();

        if (children.Count == 0)
            paths.Add(path);
        else
            foreach (var child in children)
                AddReplyPaths(child, childrenByMessage, path, paths, activePath);

        activePath.Remove(message.Id);
    }

    private static int CompareMessages(
        ThreadMessage left,
        ThreadMessage right)
    {
        var dateComparison = GetDate(left).CompareTo(GetDate(right));

        return dateComparison != 0
            ? dateComparison
            : StringComparer.Ordinal.Compare(GetCanonicalMessageIdentity(left), GetCanonicalMessageIdentity(right));
    }

    private static List<ThreadMessage> SelectCanonicalThreadCopies(
        IEnumerable<ThreadMessage> messages) =>
        messages
            .GroupBy(message => string.IsNullOrWhiteSpace(message.MessageId)
                ? $"row:{message.Id}"
                : $"message:{NormalizeMessageId(message.MessageId)}",
                StringComparer.Ordinal)
            .Select(group => group.OrderBy(message => message.Id).First())
            .OrderBy(GetDate)
            .ThenBy(message => message.Id)
            .ToList();

    private static string BuildThreadKey(
        List<ThreadMessage> messages)
    {
        var leaf = messages.Last();
        var lineage = string.IsNullOrWhiteSpace(leaf.References)
            ? []
            : ParseReferences(leaf.References);

        if (!string.IsNullOrWhiteSpace(leaf.InReplyTo))
        {
            var parent = NormalizeMessageId(leaf.InReplyTo);

            if (parent.Length > 0 && !lineage.Contains(parent, StringComparer.Ordinal))
                lineage.Add(parent);
        }

        lineage.Add(GetCanonicalMessageIdentity(leaf));

        return "path:" + string.Join("->", lineage.Select(AsThreadKeyPart));
    }

    private static string AsThreadKeyPart(
        string identity) =>
        identity.StartsWith("message:", StringComparison.Ordinal) ||
        identity.StartsWith("provider:", StringComparison.Ordinal)
            ? identity
            : $"message:{identity}";

    private static string GetCanonicalMessageIdentity(
        ThreadMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.MessageId))
            return $"message:{NormalizeMessageId(message.MessageId)}";

        return $"provider:{message.SourceKey.ToLowerInvariant()}:" +
            message.ProviderMessageId.ToLowerInvariant();
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
