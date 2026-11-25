namespace Hokusai.Core;

/// <summary>
/// Guards the user supplied for destructive statements whose condition diff-only capture cannot see, keyed by the
/// statement's normalized shape so they survive re-inference. Stored in <c>recordings/&lt;action&gt;/guards.json</c>.
/// </summary>
public sealed class Guards
{
    /// <summary>The user confirmed the statement runs without any extra condition, as recorded.</summary>
    public const string Unconditional = "(unconditional)";

    public Dictionary<string, string> ByShape { get; set; } = new(StringComparer.Ordinal);

    public string? For(string shape) => ByShape.TryGetValue(shape, out var g) ? g : null;
}
