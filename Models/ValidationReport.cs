public sealed class ValidationReport
{
    public int Messages { get; set; }
    public int Threads { get; set; }
    public int Attachments { get; set; }
    public int SearchDocuments { get; set; }

    public int MissingEml { get; set; }
    public int EmptyEml { get; set; }
    public int InvalidEml { get; set; }

    public int MissingAttachments { get; set; }
    public int OrphanAttachments { get; set; }

    public int MessagesWithoutThread { get; set; }
    public int BrokenParents { get; set; }

    public int DuplicateGmailIds { get; set; }
    public int DuplicateMessageIds { get; set; }

    public int MissingSearchDocuments { get; set; }

    public int Errors =>
        MissingEml +
        EmptyEml +
        InvalidEml +
        MissingAttachments +
        OrphanAttachments +
        MessagesWithoutThread +
        BrokenParents +
        DuplicateGmailIds +
        MissingSearchDocuments;
}
