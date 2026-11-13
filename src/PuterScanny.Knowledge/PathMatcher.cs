using System.IO.Enumeration;

namespace PuterScanny.Knowledge;

/// <summary>
/// Matches paths against many patterns at once, one folder name at a time, so a whole drive can be
/// matched in a single pass: each folder's state is computed from its parent's state plus its own name.
/// Pattern segments are literal names, wildcards (<c>*</c>, <c>?</c>), or <c>**</c> (any number of folders).
/// </summary>
public sealed class PathMatcher<T>
{
    private readonly Node _root = new(false);

    /// <summary>The state before any path segment has been consumed.</summary>
    public MatchState Start { get; private set; } = MatchState.Empty;

    public void Add(string pattern, T value, int specificity)
    {
        string[] segments = pattern.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        Node node = _root;
        foreach (string seg in segments)
        {
            if (seg == "**")
                node = node.DoubleStar ??= new Node(true);
            else if (seg.AsSpan().IndexOfAny('*', '?') >= 0)
            {
                var existing = node.Globs.FirstOrDefault(g => string.Equals(g.Pattern, seg, StringComparison.OrdinalIgnoreCase));
                if (existing.Node is null)
                {
                    existing = (seg, new Node(false));
                    node.Globs.Add(existing);
                }
                node = existing.Node;
            }
            else
            {
                if (!node.Literals.TryGetValue(seg, out var next)) node.Literals[seg] = next = new Node(false);
                node = next;
            }
        }
        node.Values.Add((value, specificity));
        Start = new MatchState(Closure([_root]));
    }

    /// <summary>Consumes one folder or file name.</summary>
    public MatchState Advance(MatchState state, string segment)
    {
        if (state.Nodes.Length == 0) return MatchState.Empty;

        List<Node>? next = null;
        foreach (Node n in state.Nodes)
        {
            if (n.IsDoubleStar) (next ??= []).Add(n);
            if (n.Literals.TryGetValue(segment, out var lit)) (next ??= []).Add(lit);
            foreach (var (pattern, child) in n.Globs)
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, segment, ignoreCase: true)) (next ??= []).Add(child);
            }
        }
        return next is null ? MatchState.Empty : new MatchState(Closure(next));
    }

    /// <summary>Consumes every segment of a full path, starting from <see cref="Start"/>.</summary>
    public MatchState AdvancePath(string path)
    {
        var state = Start;
        foreach (string seg in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            state = Advance(state, seg);
        return state;
    }

    /// <summary>The most specific value whose pattern ends exactly at this state, if any.</summary>
    public bool TryGetBest(MatchState state, out T value)
    {
        value = default!;
        int best = int.MinValue;
        bool found = false;
        foreach (Node n in state.Nodes)
        {
            foreach (var (v, spec) in n.Values)
            {
                if (spec > best)
                {
                    best = spec;
                    value = v;
                    found = true;
                }
            }
        }
        return found;
    }

    private static Node[] Closure(IEnumerable<Node> nodes)
    {
        var set = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Node>(nodes);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!set.Add(n)) continue;
            if (n.DoubleStar is { } ds) stack.Push(ds); // '**' may match zero folders
        }
        return [.. set];
    }

    internal sealed class Node(bool isDoubleStar)
    {
        public readonly bool IsDoubleStar = isDoubleStar;
        public readonly Dictionary<string, Node> Literals = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Pattern, Node Node)> Globs = [];
        public Node? DoubleStar;
        public readonly List<(T Value, int Specificity)> Values = [];
    }

    /// <summary>Immutable set of positions in the pattern tree. Cheap to keep one per folder.</summary>
    public sealed class MatchState
    {
        public static readonly MatchState Empty = new([]);

        internal MatchState(Node[] nodes) => Nodes = nodes;

        internal Node[] Nodes { get; }

        public bool IsEmpty => Nodes.Length == 0;
    }
}
