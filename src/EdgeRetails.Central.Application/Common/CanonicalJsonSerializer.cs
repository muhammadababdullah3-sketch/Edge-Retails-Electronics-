using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdgeRetails.Central.Application.Common;

public class CanonicalJsonSerializer : ICanonicalJsonSerializer
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string SerializeCanonical<T>(T value)
    {
        if (value is null)
        {
            return "null";
        }

        var json = JsonSerializer.Serialize(value, DefaultOptions);
        return SerializeCanonical(json);
    }

    public string SerializeCanonical(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString))
        {
            return "null";
        }

        var node = JsonNode.Parse(jsonString);
        if (node is null)
        {
            return "null";
        }

        var sortedNode = SortJsonNode(node);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            sortedNode.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string ComputeSha256Hex(string canonicalJson)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalJson);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public (string CanonicalJson, string Sha256Hex) SerializeAndHash<T>(T value)
    {
        var canonical = SerializeCanonical(value);
        var hash = ComputeSha256Hex(canonical);
        return (canonical, hash);
    }

    private static JsonNode SortJsonNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var sortedObj = new JsonObject();
            var sortedProperties = obj
                .Where(kv => !string.Equals(kv.Key, "commandMeta", StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToList();

            foreach (var prop in sortedProperties)
            {
                if (prop.Value is not null)
                {
                    sortedObj.Add(prop.Key, SortJsonNode(prop.Value.DeepClone()));
                }
                else
                {
                    sortedObj.Add(prop.Key, null);
                }
            }
            return sortedObj;
        }

        if (node is JsonArray arr)
        {
            var sortedArr = new JsonArray();
            foreach (var item in arr)
            {
                if (item is not null)
                {
                    sortedArr.Add(SortJsonNode(item.DeepClone()));
                }
                else
                {
                    sortedArr.Add(null);
                }
            }
            return sortedArr;
        }

        return node.DeepClone();
    }
}
