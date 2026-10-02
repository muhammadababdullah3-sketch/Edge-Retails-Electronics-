using System.Security.Cryptography;
using EdgeRetails.Recovery;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class RecoveryGovernanceKeyBindingTests
{
    [Fact]
    public void Matching_public_key_is_bound_to_the_governance_signer()
    {
        using var signer = RSA.Create(3072);

        Assert.True(RecoveryGovernanceKeyBinding.MatchesSignerPublicKey(
            signer,
            signer.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void Different_public_key_is_rejected()
    {
        using var signer = RSA.Create(3072);
        using var attacker = RSA.Create(3072);

        Assert.False(RecoveryGovernanceKeyBinding.MatchesSignerPublicKey(
            signer,
            attacker.ExportSubjectPublicKeyInfoPem()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-public-key")]
    [InlineData("-----BEGIN " + "PRIVATE KEY-----\ninvalid\n-----END " + "PRIVATE KEY-----")]
    public void Malformed_or_private_key_material_is_rejected(string publicKeyPem)
    {
        using var signer = RSA.Create(3072);

        Assert.False(RecoveryGovernanceKeyBinding.MatchesSignerPublicKey(signer, publicKeyPem));
    }

    [Fact]
    public void Undersized_signer_is_rejected()
    {
        using var signer = RSA.Create(2048);

        Assert.False(RecoveryGovernanceKeyBinding.MatchesSignerPublicKey(
            signer,
            signer.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void Undersized_staged_public_key_is_rejected()
    {
        using var signer = RSA.Create(3072);
        using var undersizedPublicKey = RSA.Create(2048);

        Assert.False(RecoveryGovernanceKeyBinding.MatchesSignerPublicKey(
            signer,
            undersizedPublicKey.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void Valid_private_key_pem_is_rejected_as_server_trust()
    {
        using var signer = RSA.Create(3072);

        Assert.False(RecoveryGovernanceKeyBinding.MatchesSignerPublicKey(
            signer,
            signer.ExportPkcs8PrivateKeyPem()));
    }
}
