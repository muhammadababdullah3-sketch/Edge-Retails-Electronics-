using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdgeRetails.Application.Common;

public static class CanonicalPayloadSerializer
{
    private static readonly HashSet<string> ExcludedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "X-Correlation-Id",
        "X-Timestamp",
        "X-Request-Timestamp",
        "X-Client-Timestamp",
        "X-Forwarded-For",
        "TraceParent",
        "TraceState"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static byte[] SerializeToCanonicalBytes(object? payload)
    {
        if (payload is null)
        {
            return Encoding.UTF8.GetBytes("null");
        }

        if (payload is string rawJsonString && IsValidJson(rawJsonString))
        {
            return SerializeJsonStringToCanonicalBytes(rawJsonString);
        }

        var json = JsonSerializer.Serialize(payload, payload.GetType(), SerializerOptions);
        return SerializeJsonStringToCanonicalBytes(json);
    }

    public static string SerializeToCanonicalString(object? payload)
    {
        var bytes = SerializeToCanonicalBytes(payload);
        return Encoding.UTF8.GetString(bytes);
    }

    public static byte[] SerializeJsonStringToCanonicalBytes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var canonicalNode = CanonicalizeElement(doc.RootElement);
        if (canonicalNode is null)
        {
            return Encoding.UTF8.GetBytes("null");
        }

        var canonicalJson = canonicalNode.ToJsonString(SerializerOptions);
        return Encoding.UTF8.GetBytes(canonicalJson);
    }

    public static string SerializeJsonStringToCanonicalString(string json)
    {
        return Encoding.UTF8.GetString(SerializeJsonStringToCanonicalBytes(json));
    }

    private static bool IsValidJson(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return false;
        str = str.Trim();
        if ((str.StartsWith("{") && str.EndsWith("}")) || (str.StartsWith("[") && str.EndsWith("]")))
        {
            try
            {
                using var doc = JsonDocument.Parse(str);
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private static JsonNode? CanonicalizeElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => CanonicalizeObject(element),
            JsonValueKind.Array => CanonicalizeArray(element),
            JsonValueKind.String => JsonValue.Create(element.GetString()),
            JsonValueKind.Number => CanonicalizeNumber(element),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private static JsonObject CanonicalizeObject(JsonElement element)
    {
        var properties = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);

        foreach (var prop in element.EnumerateObject())
        {
            if (ExcludedHeaders.Contains(prop.Name))
            {
                continue;
            }

            properties[prop.Name] = CanonicalizeElement(prop.Value);
        }

        var obj = new JsonObject();
        foreach (var (k, v) in properties)
        {
            obj[k] = v;
        }

        return obj;
    }

    private static JsonArray CanonicalizeArray(JsonElement element)
    {
        var arr = new JsonArray();
        foreach (var item in element.EnumerateArray())
        {
            arr.Add(CanonicalizeElement(item));
        }

        return arr;
    }

    private static JsonValue CanonicalizeNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var intVal))
        {
            return JsonValue.Create(intVal);
        }

        if (element.TryGetDecimal(out var decVal))
        {
            var formatted = decVal.ToString("0.############################", CultureInfo.InvariantCulture);
            if (decimal.TryParse(formatted, NumberStyles.Number, CultureInfo.InvariantCulture, out var normalized))
            {
                return JsonValue.Create(normalized);
            }
            return JsonValue.Create(decVal);
        }

        if (element.TryGetDouble(out var dblVal))
        {
            return JsonValue.Create(dblVal);
        }

        return JsonValue.Create(element.GetRawText());
    }
}
