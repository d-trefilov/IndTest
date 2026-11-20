namespace Numbering.Core;

public enum ProposalStatus
{
    Ready,
    Unchanged,
    Conflict,
    Skipped
}

public sealed class NumberProposal
{
    public NumberProposal(string? value, ProposalStatus status, string? reason = null)
    {
        Value = value;
        Status = status;
        Reason = reason;
    }

    public string? Value { get; }
    public ProposalStatus Status { get; }
    public string? Reason { get; }
}
