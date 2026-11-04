using System.Globalization;

namespace TraceBudget;

internal static class Reports
{
    private static string Ns(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    internal static object Inspect(TraceMetrics[] traces, bool labels) => new
    {
        schema = 1,
        mode = "inspect",
        traces = traces.Select((trace, index) =>
        {
            var positions = trace.Spans.Select((item, i) => (item.Span.Id, Number: i + 1))
                .ToDictionary(x => x.Id, x => x.Number);
            ulong origin = trace.Spans.Min(item => item.Span.Start);
            return new
            {
                trace = index + 1,
                traceId = labels ? trace.Id : null,
                qualified = trace.Qualified,
                roots = trace.Roots,
                missingParents = trace.MissingParents,
                cycleMembers = trace.CycleMembers,
                outsideParent = trace.OutsideParent,
                spans = trace.Spans.Select((item, i) => new
                {
                    span = i + 1,
                    spanId = labels ? item.Span.Id : null,
                    parent = item.Span.Parent is string parent && positions.TryGetValue(parent, out int n)
                        ? (int?)n : null,
                    parentMissing = item.Span.Parent is string id && !positions.ContainsKey(id),
                    service = labels ? item.Span.Service : null,
                    name = labels ? item.Span.Name : null,
                    error = item.Span.Error,
                    startNs = Ns(item.Span.Start - origin),
                    durationNs = Ns(item.Span.Duration),
                    coveredNs = Ns(item.Covered),
                    uncoveredNs = Ns(item.Uncovered),
                    peakChildren = item.PeakChildren
                })
            };
        })
    };

    internal static object Compare(Comparison result, int minimum, ulong threshold, bool labels) => new
    {
        schema = 1,
        mode = "compare",
        status = result.Status,
        minSamples = minimum,
        maxDeltaNs = Ns(threshold),
        beforeExcluded = result.BeforeExcluded,
        afterExcluded = result.AfterExcluded,
        routes = result.Routes.Select((row, i) => new
        {
            route = i + 1,
            service = labels ? row.Route.Service : null,
            name = labels ? row.Route.Name : null,
            before = Population(row.Before),
            after = Population(row.After),
            p95DeltaNs = row.P95Delta?.ToString(CultureInfo.InvariantCulture),
            status = row.Status
        })
    };

    private static object Population(Population population) => new
    {
        count = population.Count,
        p50Ns = population.P50 is ulong p50 ? Ns(p50) : null,
        p95Ns = population.P95 is ulong p95 ? Ns(p95) : null
    };
}
