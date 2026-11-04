using System.Globalization;
using System.Text.Json;

namespace TraceBudget;

internal sealed record Span(string Trace, string Id, string? Parent, string Service, string Name,
    ulong Start, ulong End, bool Error)
{
    internal ulong Duration => End - Start;
}

internal static class Otlp
{
    internal const int MaxBytes = 32 * 1024 * 1024;
    internal const int MaxSpans = 100_000;

    internal static List<Span> Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes)
            throw new InvalidDataException("OTLP input exceeds 32 MiB.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("OTLP input size changed during read.");
        try
        {
            return Parse(bytes);
        }
        catch (InvalidOperationException error)
        {
            // JsonDocument validates string contents lazily when decoded.
            throw new InvalidDataException("Invalid text in OTLP JSON.", error);
        }
    }

    internal static List<Span> Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
            throw new InvalidDataException("OTLP input exceeds 32 MiB.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var root = Fields(document.RootElement);
        List<Span> spans = [];
        HashSet<(string, string)> identities = [];
        foreach (var resource in Array(root, "resourceSpans"))
        {
            var resourceFields = Fields(resource);
            var service = Service(resourceFields);
            foreach (var scope in Array(resourceFields, "scopeSpans"))
            foreach (var element in Array(Fields(scope), "spans"))
            {
                if (spans.Count == MaxSpans)
                    throw new InvalidDataException("OTLP input exceeds 100,000 spans.");
                var span = Fields(element);
                var trace = Id(Text(span, "traceId"), 32);
                var id = Id(Text(span, "spanId"), 16);
                var parent = Text(span, "parentSpanId", optional: true);
                var start = Timestamp(span, "startTimeUnixNano");
                var end = Timestamp(span, "endTimeUnixNano");
                if (end < start)
                    throw new InvalidDataException("A span ends before it starts.");
                if (!identities.Add((trace, id)))
                    throw new InvalidDataException("Duplicate span ID in one trace.");
                var statusCode = 0;
                if (span.TryGetValue("status", out var status) &&
                    Fields(status).TryGetValue("code", out var code) &&
                    (code.ValueKind != JsonValueKind.Number ||
                     !code.TryGetInt32(out statusCode) || statusCode is < 0 or > 2))
                    throw new InvalidDataException("Span status code must be 0, 1, or 2.");
                var name = Text(span, "name");
                spans.Add(new(trace, id, parent.Length == 0 ? null : Id(parent, 16),
                    service, name, start, end, statusCode == 2));
            }
        }
        if (spans.Count == 0)
            throw new InvalidDataException("No spans in OTLP export.");
        return spans;
    }

    private static string Service(Dictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("resource", out var resource))
            return "";
        var properties = Fields(resource);
        string? service = null;
        if (properties.ContainsKey("attributes"))
        {
            foreach (var item in Array(properties, "attributes"))
            {
                var attribute = Fields(item);
                if (Text(attribute, "key") != "service.name")
                    continue;
                if (service is not null || !attribute.TryGetValue("value", out var value))
                    throw new InvalidDataException("Ambiguous or missing service.name value.");
                service = Text(Fields(value), "stringValue");
            }
        }
        return service ?? "";
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected an OTLP JSON object.");
        Dictionary<string, JsonElement> fields = new(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!fields.TryAdd(field.Name, field.Value))
                throw new InvalidDataException("Duplicate property in an interpreted OTLP object.");
        return fields;
    }

    private static JsonElement.ArrayEnumerator Array(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
            return default;
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Expected an array for {name}.");
        return value.EnumerateArray();
    }

    private static string Text(Dictionary<string, JsonElement> fields, string name, bool optional = false)
    {
        if (!fields.TryGetValue(name, out var value) && optional)
            return "";
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            text.Length > 512 || (!optional && text.Length == 0) || text.Any(char.IsControl))
            throw new InvalidDataException($"Invalid text for {name}.");
        return text;
    }

    private static string Id(string value, int length)
    {
        if (value.Length != length || value.All(c => c == '0') || !value.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid nonzero OTLP hex identity.");
        return value.ToLowerInvariant();
    }

    private static ulong Timestamp(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
            throw new InvalidDataException($"Missing {name}.");
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        if (text is null || text.Length == 0 || !text.All(char.IsAsciiDigit) ||
            !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw new InvalidDataException($"Invalid unsigned integer for {name}.");
        return result;
    }
}
