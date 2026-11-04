namespace TraceBudget;

internal static class Program
{
    internal static int Main(string[] args)
    {
        if (args.Length != 1)
            return 2;
        try
        {
            Console.WriteLine(Otlp.Read(args[0]).Count);
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }
}
