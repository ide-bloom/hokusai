namespace Hokusai.Infer;

public abstract record AlignedNode
{
    /// <summary>Demonstrations in which this node occurs.</summary>
    public abstract IReadOnlyCollection<int> Presence { get; }
}

/// <summary>One statement position; <see cref="Instances"/> maps a demonstration to the statement's index in it.</summary>
public sealed record AlignedStatement(string Shape, Dictionary<int, int> Instances) : AlignedNode
{
    public override IReadOnlyCollection<int> Presence => Instances.Keys;
}

/// <summary>A block repeated a variable number of times; each iteration lists statement indexes for the body positions.</summary>
public sealed record AlignedLoop(IReadOnlyList<string> Body, Dictionary<int, List<int[]>> Iterations) : AlignedNode
{
    public override IReadOnlyCollection<int> Presence => Iterations.Keys;
}

/// <summary>
/// Aligns the statement sequences of several demonstrations by normalized shape. Repeated blocks are folded into
/// loops first, then demonstrations are merged progressively with a longest-common-subsequence alignment; positions
/// missing from some demonstrations become conditional.
/// </summary>
public static class Aligner
{
    private const int MaxBody = 8;

    private sealed record Token(string Key, string? Shape, int Index, IReadOnlyList<string>? Body, List<int[]>? Iterations);

    public static List<AlignedNode> Align(IReadOnlyList<IReadOnlyList<string>> demos)
    {
        var bodies = FindLoopBodies(demos);
        var tokenized = demos.Select(d => Tokenize(d, bodies)).ToList();
        if (tokenized.Count == 0)
        {
            return [];
        }

        // Each profile column holds the token key and, per demonstration, the token found there.
        var columns = tokenized[0].Select(t => (t.Key, Tokens: new Dictionary<int, Token> { [0] = t })).ToList();
        for (var d = 1; d < tokenized.Count; d++)
        {
            columns = Merge(columns, tokenized[d], d);
        }

        var nodes = new List<AlignedNode>();
        foreach (var (key, tokens) in columns)
        {
            var any = tokens.Values.First();
            if (any.Body is not null)
            {
                nodes.Add(new AlignedLoop(any.Body, tokens.ToDictionary(p => p.Key, p => p.Value.Iterations!)));
            }
            else
            {
                nodes.Add(new AlignedStatement(any.Shape!, tokens.ToDictionary(p => p.Key, p => p.Value.Index)));
            }
        }

        return nodes;
    }

    /// <summary>Blocks that repeat back to back at least twice in some demonstration, longest first.</summary>
    internal static List<IReadOnlyList<string>> FindLoopBodies(IReadOnlyList<IReadOnlyList<string>> demos)
    {
        var found = new List<IReadOnlyList<string>>();
        foreach (var seq in demos)
        {
            for (var k = 1; k <= Math.Min(MaxBody, seq.Count / 2); k++)
            {
                for (var i = 0; i + (2 * k) <= seq.Count; i++)
                {
                    var body = seq.Skip(i).Take(k).ToList();
                    if (!body.SequenceEqual(seq.Skip(i + k).Take(k)) || IsSelfRepeating(body))
                    {
                        continue;
                    }

                    if (!found.Any(f => f.SequenceEqual(body)))
                    {
                        found.Add(body);
                    }
                }
            }
        }

        // Longest first, so a multi-statement line-item block is folded before any shorter block it contains.
        return [.. found.OrderByDescending(b => b.Count)];
    }

    private static bool IsSelfRepeating(List<string> body)
    {
        for (var p = 1; p < body.Count; p++)
        {
            if (body.Count % p == 0 && Enumerable.Range(0, body.Count).All(i => body[i] == body[i % p]))
            {
                return true;
            }
        }

        return false;
    }

    private static List<Token> Tokenize(IReadOnlyList<string> seq, List<IReadOnlyList<string>> bodies)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < seq.Count)
        {
            var matched = false;
            foreach (var body in bodies)
            {
                if (!Matches(seq, i, body))
                {
                    continue;
                }

                var iterations = new List<int[]>();
                while (Matches(seq, i, body))
                {
                    iterations.Add([.. Enumerable.Range(i, body.Count)]);
                    i += body.Count;
                }

                tokens.Add(new Token("loop:" + string.Join("\u001E", body), null, -1, body, iterations));
                matched = true;
                break;
            }

            if (!matched)
            {
                tokens.Add(new Token("stmt:" + seq[i], seq[i], i, null, null));
                i++;
            }
        }

        return tokens;
    }

    private static bool Matches(IReadOnlyList<string> seq, int start, IReadOnlyList<string> body)
    {
        if (start + body.Count > seq.Count)
        {
            return false;
        }

        for (var j = 0; j < body.Count; j++)
        {
            if (seq[start + j] != body[j])
            {
                return false;
            }
        }

        return true;
    }

    private static List<(string Key, Dictionary<int, Token> Tokens)> Merge(List<(string Key, Dictionary<int, Token> Tokens)> profile, List<Token> demo, int d)
    {
        var n = profile.Count;
        var m = demo.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = profile[i].Key == demo[j].Key ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var merged = new List<(string, Dictionary<int, Token>)>();
        int a = 0, b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && profile[a].Key == demo[b].Key)
            {
                profile[a].Tokens[d] = demo[b];
                merged.Add(profile[a]);
                a++;
                b++;
            }
            else if (b < m && (a >= n || lcs[a, b + 1] >= lcs[a + 1, b]))
            {
                merged.Add((demo[b].Key, new Dictionary<int, Token> { [d] = demo[b] }));
                b++;
            }
            else
            {
                merged.Add(profile[a]);
                a++;
            }
        }

        return merged;
    }
}
