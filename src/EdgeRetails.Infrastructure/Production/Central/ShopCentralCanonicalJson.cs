using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdgeRetails.Infrastructure.Production.Central;

/// <summary>Must match Central <c>CanonicalJsonSerializer</c> for bootstrap proofs.</summary>
internal sealed class ShopCentralCanonicalJson
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string SerializeCanonical<T>(T value) =>
        SerializeCanonical(JsonSerializer.Serialize(value, DefaultOptions));

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
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            sortedNode.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string ComputeSha256Hex(string canonicalJson)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalJson);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public (string CanonicalJson, string Sha256Hex) SerializeAndHash<T>(T value)
    {
        var canonical = SerializeCanonical(value);
        return (canonical, ComputeSha256Hex(canonical));
    }

    private static JsonNode SortJsonNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var sortedObj = new JsonObject();
            foreach (var prop in obj.Where(kv => !string.Equals(kv.Key, "commandMeta", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (prop.Value is not null)
                {
                    sortedObj.Add(prop.Key, SortJsonNode(prop.Value.DeepClone()));
                }
            }

            return sortedObj;
        }

        if (node is JsonArray array)
        {
            var sortedArray = new JsonArray();
            foreach (var item in array)
            {
                sortedArray.Add(item is null ? null : SortJsonNode(item.DeepClone()));
            }

            return sortedArray;
        }

        return node.DeepClone();
    }
}
