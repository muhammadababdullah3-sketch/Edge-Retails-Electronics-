namespace EdgeRetails.Central.Application.Common;

public interface ICanonicalJsonSerializer
{
    string SerializeCanonical<T>(T value);
    string SerializeCanonical(string jsonString);
    string ComputeSha256Hex(string canonicalJson);
    (string CanonicalJson, string Sha256Hex) SerializeAndHash<T>(T value);
}
