namespace TraceBudget;

internal static class Program
{
    internal static int Main(string[] args)
    {
        if (args.Length != 1)
            return 2;
        try
        {
            foreach (var trace in Analysis.Analyze(Otlp.Read(args[0])))
                Console.WriteLine($"{trace.Id} spans={trace.Spans.Length} qualified={trace.Qualified}");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }
}
