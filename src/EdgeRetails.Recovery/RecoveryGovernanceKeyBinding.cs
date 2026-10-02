using System;
using System.Security.Cryptography;

namespace EdgeRetails.Recovery;

internal static class RecoveryGovernanceKeyBinding
{
    public static bool MatchesSignerPublicKey(RSA signer, string? publicKeyPem)
    {
        if (signer is null || signer.KeySize != 3072 ||
            string.IsNullOrWhiteSpace(publicKeyPem) ||
            publicKeyPem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var publicKey = RSA.Create();
            publicKey.ImportFromPem(publicKeyPem);
            if (publicKey.KeySize != 3072)
            {
                return false;
            }

            var expected = publicKey.ExportParameters(false);
            var actual = signer.ExportParameters(false);
            return CryptographicOperations.FixedTimeEquals(expected.Modulus!, actual.Modulus!) &&
                   CryptographicOperations.FixedTimeEquals(expected.Exponent!, actual.Exponent!);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
