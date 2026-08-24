public sealed class GmailReconciliationReport
{
    public int GmailMessages { get; set; }

    public int LocalMessages { get; set; }

    public List<string> MissingLocally { get; set; } = [];

    public List<string> ExtraLocally { get; set; } = [];
}