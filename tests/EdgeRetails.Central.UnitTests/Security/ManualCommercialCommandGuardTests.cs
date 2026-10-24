using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.DTOs;

namespace EdgeRetails.Central.UnitTests.Security;

public sealed class ManualCommercialCommandGuardTests
{
    [Theory]
    [InlineData("payment.review")]
    [InlineData("license.issue")]
    public void AcceptsCompleteManualOperationIdentity(string operationType)
    {
        Assert.True(ManualCommercialCommandGuard.IsValid(ValidMeta(operationType), operationType));
    }

    [Fact]
    public void RejectsMissingOperationId()
    {
        var meta = ValidMeta("payment.review");
        meta.ClientOperationId = Guid.Empty;

        Assert.False(ManualCommercialCommandGuard.IsValid(meta, "payment.review"));
    }

    [Fact]
    public void RejectsOperationTypeSubstitution()
    {
        Assert.False(ManualCommercialCommandGuard.IsValid(ValidMeta("license.issue"), "payment.review"));
    }

    [Fact]
    public void RejectsUnsupportedFingerprintVersion()
    {
        var meta = ValidMeta("license.issue");
        meta.FingerprintVersion = 2;

        Assert.False(ManualCommercialCommandGuard.IsValid(meta, "license.issue"));
    }

    [Fact]
    public void RejectsMalformedPayloadDigest()
    {
        var meta = ValidMeta("payment.review");
        meta.PayloadSha256 = new string('A', 64);

        Assert.False(ManualCommercialCommandGuard.IsValid(meta, "payment.review"));
    }

    [Fact]
    public void RejectsNegativeVersionsAndBlankReason()
    {
        var negativeExpectedVersion = ValidMeta("license.issue");
        negativeExpectedVersion.ExpectedVersion = -1;
        var negativePolicyVersion = ValidMeta("license.issue");
        negativePolicyVersion.PolicyVersion = -1;
        var blankReason = ValidMeta("license.issue");
        blankReason.Reason = " ";

        Assert.False(ManualCommercialCommandGuard.IsValid(negativeExpectedVersion, "license.issue"));
        Assert.False(ManualCommercialCommandGuard.IsValid(negativePolicyVersion, "license.issue"));
        Assert.False(ManualCommercialCommandGuard.IsValid(blankReason, "license.issue"));
    }

    private static CommandMetaDto ValidMeta(string operationType) => new()
    {
        ClientOperationId = Guid.NewGuid(),
        OperationType = operationType,
        FingerprintVersion = 1,
        PayloadSha256 = new string('a', 64),
        ExpectedVersion = 1,
        PolicyVersion = 0,
        Reason = "Synthetic manual commercial command identity."
    };
}
