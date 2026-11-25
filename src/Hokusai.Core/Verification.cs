using Hokusai.Runtime;

namespace Hokusai.Core;

public sealed class DemoVerification
{
    public int Index { get; set; }
    public bool Matched { get; set; }
    public List<DiffMismatch> Mismatches { get; set; } = [];
    public List<string> FileMismatches { get; set; } = [];
    public string? Error { get; set; }
    public DatabaseDiff? Actual { get; set; }
}

public sealed class VerificationReport
{
    public string Action { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public bool Verified { get; set; }
    public string Source { get; set; } = "";
    public List<DemoVerification> Demonstrations { get; set; } = [];
    public List<string> Issues { get; set; } = [];
}

/// <summary>Typed inputs for one execution of an action.</summary>
public sealed class ActionInput
{
    public Dictionary<string, object?> Scalars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<Dictionary<string, object?>>> Collections { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExecutionOutcome
{
    public List<HkPlannedStatement> Statements { get; } = [];
    public HkResult? Result { get; set; }
    public string? FileContent { get; set; }
    public long RowsAffected { get; set; }
}
