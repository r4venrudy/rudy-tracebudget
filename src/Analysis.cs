namespace TraceBudget;

internal sealed record SpanMetrics(Span Span, ulong Covered, ulong Uncovered, int PeakChildren);
internal sealed record TraceMetrics(string Id, int Roots, int MissingParents, int CycleMembers,
    int OutsideParent, SpanMetrics[] Spans, Span? Root)
{
    internal bool Qualified => Roots == 1 && MissingParents == 0 && CycleMembers == 0 && OutsideParent == 0;
}

internal static class Analysis
{
    internal static TraceMetrics[] Analyze(List<Span> spans) =>
        spans.GroupBy(span => span.Trace, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => AnalyzeTrace(group.Key, group.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray()))
            .ToArray();

    private static TraceMetrics AnalyzeTrace(string trace, Span[] spans)
    {
        var byId = spans.Select((span, index) => (span.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var parents = new int[spans.Length];
        var children = new List<(ulong Start, ulong End)>?[spans.Length];
        int roots = 0, missing = 0, outside = 0;
        Span? root = null;
        for (int i = 0; i < spans.Length; i++)
        {
            var span = spans[i];
            parents[i] = -1;
            if (span.Parent is null)
            {
                roots++;
                root = span;
                continue;
            }
            if (!byId.TryGetValue(span.Parent, out int parentIndex))
            {
                missing++;
                continue;
            }
            parents[i] = parentIndex;
            var parent = spans[parentIndex];
            if (span.Start < parent.Start || span.End > parent.End)
                outside++;
            ulong start = Math.Max(parent.Start, span.Start);
            ulong end = Math.Min(parent.End, span.End);
            if (end > start)
                (children[parentIndex] ??= []).Add((start, end));
        }

        SpanMetrics[] metrics = new SpanMetrics[spans.Length];
        for (int i = 0; i < spans.Length; i++)
        {
            var (covered, peak) = Measure(children[i]);
            metrics[i] = new(spans[i], covered, spans[i].Duration - covered, peak);
        }
        return new(trace, roots, missing, CountCycles(parents), outside, metrics, roots == 1 ? root : null);
    }

    // Each node has at most one parent. Nodes left after removing childless
    // vertices and their incoming edges are exactly the cycle members.
    private static int CountCycles(int[] parents)
    {
        var indegree = new int[parents.Length];
        foreach (int parent in parents)
            if (parent >= 0)
                indegree[parent]++;
        Queue<int> leaves = new();
        for (int i = 0; i < indegree.Length; i++)
            if (indegree[i] == 0)
                leaves.Enqueue(i);
        int remaining = parents.Length;
        while (leaves.TryDequeue(out int leaf))
        {
            remaining--;
            int parent = parents[leaf];
            if (parent >= 0 && --indegree[parent] == 0)
                leaves.Enqueue(parent);
        }
        return remaining;
    }

    private static (ulong Covered, int Peak) Measure(List<(ulong Start, ulong End)>? intervals)
    {
        if (intervals is null)
            return (0, 0);
        var endpoints = new (ulong Time, int Change)[2 * intervals.Count];
        for (int i = 0; i < intervals.Count; i++)
        {
            endpoints[2 * i] = (intervals[i].Start, 1);
            endpoints[2 * i + 1] = (intervals[i].End, -1);
        }
        Array.Sort(endpoints);
        ulong covered = 0, last = endpoints[0].Time;
        int active = 0, peak = 0;
        for (int i = 0; i < endpoints.Length;)
        {
            ulong time = endpoints[i].Time;
            if (active > 0)
                covered += time - last;
            do { active += endpoints[i++].Change; }
            while (i < endpoints.Length && endpoints[i].Time == time);
            peak = Math.Max(peak, active);
            last = time;
        }
        return (covered, peak);
    }
}
