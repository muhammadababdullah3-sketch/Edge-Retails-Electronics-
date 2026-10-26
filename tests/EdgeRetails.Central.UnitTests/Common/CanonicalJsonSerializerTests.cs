using EdgeRetails.Central.Application.Common;

namespace EdgeRetails.Central.UnitTests.Common;

public class CanonicalJsonSerializerTests
{
    private readonly CanonicalJsonSerializer _serializer = new();

    [Fact]
    public void SerializeCanonical_SortsObjectPropertiesLexicographically()
    {
        const string inputJson = """{"zebra":1,"apple":"fruit","mango":true}""";
        var result = _serializer.SerializeCanonical(inputJson);

        Assert.Equal("""{"apple":"fruit","mango":true,"zebra":1}""", result);
    }

    [Fact]
    public void SerializeCanonical_SortsDeepNestedObjects()
    {
        const string inputJson = """{"outer":{"z":1,"a":{"b":2,"a":1}}}""";
        var result = _serializer.SerializeCanonical(inputJson);

        Assert.Equal("""{"outer":{"a":{"a":1,"b":2},"z":1}}""", result);
    }

    [Fact]
    public void SerializeCanonical_PreservesArrayOrder_WhileSortingArrayElements()
    {
        const string inputJson = """[{"b":2,"a":1},{"d":4,"c":3}]""";
        var result = _serializer.SerializeCanonical(inputJson);

        Assert.Equal("""[{"a":1,"b":2},{"c":3,"d":4}]""", result);
    }

    [Fact]
    public void ComputeSha256Hex_ProducesDeterministicHash()
    {
        const string canonical = """{"a":1,"b":"test"}""";
        var hash1 = _serializer.ComputeSha256Hex(canonical);
        var hash2 = _serializer.ComputeSha256Hex(canonical);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
        Assert.Matches("^[a-f0-9]{64}$", hash1);
    }

    [Fact]
    public void SerializeAndHash_ReturnsConsistentHashRegardlessOfInitialKeyOrder()
    {
        const string json1 = """{"beta":2,"alpha":1}""";
        const string json2 = """{"alpha":1,"beta":2}""";

        var (canonical1, hash1) = (_serializer.SerializeCanonical(json1), _serializer.ComputeSha256Hex(_serializer.SerializeCanonical(json1)));
        var (canonical2, hash2) = (_serializer.SerializeCanonical(json2), _serializer.ComputeSha256Hex(_serializer.SerializeCanonical(json2)));

        Assert.Equal(canonical1, canonical2);
        Assert.Equal(hash1, hash2);
    }
}
