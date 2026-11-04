using System.Globalization;
using System.Text.Json;

namespace TraceBudget;

internal static class Program
{
    private const string Version = "0.1.0-preview";
    private const string Usage = """
        tracebudget inspect [--include-labels] [--] OTLP.json
        tracebudget compare [--include-labels] [--min-samples N] [--max-delta-ns N] [--] BEFORE.json AFTER.json
        tracebudget --version
        """;

    internal static int Main(string[] args)
    {
        if (args is ["--version"] or ["--help"])
        {
            Console.WriteLine(args[0] == "--version" ? $"tracebudget {Version}" : Usage);
            return 0;
        }
        try
        {
            if (args.Length == 0 || args[0] is not ("inspect" or "compare"))
                throw new InvalidDataException(Usage);
            bool compare = args[0] == "compare", labels = false, positional = false;
            int? minSamples = null;
            ulong? maxDelta = null;
            List<string> files = [];
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--" when !positional: positional = true; break;
                    case "--include-labels" when !positional && !labels: labels = true; break;
                    case "--min-samples" when !positional && compare && minSamples is null:
                        if (++i == args.Length ||
                            !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out int sample) ||
                            sample is < 1 or > Otlp.MaxSpans)
                            throw new InvalidDataException("Expected min-samples from 1 to 100000.");
                        minSamples = sample;
                        break;
                    case "--max-delta-ns" when !positional && compare && maxDelta is null:
                        if (++i == args.Length ||
                            !ulong.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out ulong delta))
                            throw new InvalidDataException("Expected unsigned max-delta-ns.");
                        maxDelta = delta;
                        break;
                    default:
                        if (!positional && arg.StartsWith('-'))
                            throw new InvalidDataException(Usage);
                        files.Add(arg);
                        break;
                }
            }
            if (files.Count != (compare ? 2 : 1))
                throw new InvalidDataException(Usage);

            var before = Analysis.Analyze(Otlp.Read(files[0]));
            if (!compare)
            {
                Output(Reports.Inspect(before, labels));
                return 0;
            }
            var after = Analysis.Analyze(Otlp.Read(files[1]));
            int minimum = minSamples ?? 20;
            ulong threshold = maxDelta ?? 0;
            var result = Budgets.Compare(before, after, minimum, threshold);
            Output(Reports.Compare(result, minimum, threshold, labels));
            return result.Status switch { "within-budget" => 0, "regressed" => 1, _ => 3 };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
                                     UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine(error is InvalidDataException ? $"tracebudget: {error.Message}"
                : "tracebudget: unable to read, decode, or write the requested data.");
            return 2;
        }
    }

    private static void Output(object report)
    {
        using var stream = Console.OpenStandardOutput();
        JsonSerializer.Serialize(stream, report);
        stream.WriteByte((byte)'\n');
    }
}
