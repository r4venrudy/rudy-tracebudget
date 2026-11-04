namespace TraceBudget;

internal readonly record struct Route(string Service, string Name);
internal sealed record Population(int Count, ulong? P50, ulong? P95);
internal sealed record RouteDelta(Route Route, Population Before, Population After,
    Int128? P95Delta, string Status);
internal sealed record Comparison(int BeforeExcluded, int AfterExcluded, RouteDelta[] Routes)
{
    internal string Status => BeforeExcluded != 0 || AfterExcluded != 0 ||
        Routes.Length == 0 || Routes.Any(route => route.Status == "insufficient-samples")
            ? "inconclusive"
            : Routes.Any(route => route.Status == "regressed") ? "regressed" : "within-budget";
}

internal static class Budgets
{
    internal static Comparison Compare(TraceMetrics[] baseline, TraceMetrics[] candidate,
        int minSamples, ulong maxDelta)
    {
        var before = Group(baseline);
        var after = Group(candidate);
        List<RouteDelta> changes = [];
        foreach (var route in before.Keys.Union(after.Keys)
                     .OrderBy(key => key.Service, StringComparer.Ordinal)
                     .ThenBy(key => key.Name, StringComparer.Ordinal))
        {
            var left = Summarize(before.GetValueOrDefault(route));
            var right = Summarize(after.GetValueOrDefault(route));
            Int128? delta = left.P95 is ulong oldP95 && right.P95 is ulong newP95
                ? (Int128)newP95 - oldP95 : null;
            string status = left.Count < minSamples || right.Count < minSamples
                ? "insufficient-samples" : delta > maxDelta ? "regressed" : "within-budget";
            changes.Add(new(route, left, right, delta, status));
        }
        return new(baseline.Count(trace => !trace.Qualified), candidate.Count(trace => !trace.Qualified),
            changes.ToArray());
    }

    private static Dictionary<Route, ulong[]> Group(TraceMetrics[] traces) =>
        traces.Where(trace => trace.Qualified)
            .Select(trace => trace.Root!)
            .GroupBy(span => new Route(span.Service, span.Name))
            .ToDictionary(group => group.Key, group => group.Select(span => span.Duration).Order().ToArray());

    private static Population Summarize(ulong[]? durations)
    {
        if (durations is null || durations.Length == 0)
            return new(0, null, null);
        return new(durations.Length, Quantile(durations, 50), Quantile(durations, 95));
    }

    private static ulong Quantile(ulong[] sorted, int percent) =>
        sorted[(sorted.Length * percent + 99) / 100 - 1];
}
