using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;

namespace EdgeRetails.Central.UnitTests.Licensing;

public class CommercialTermAndPolicyTests
{
    [Fact]
    public void CommercialTerm_Lifetime_AllowsNullExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var term = new CommercialTerm(CommercialTerm.Lifetime, now, null);

        Assert.Equal(CommercialTerm.Lifetime, term.TermKind);
        Assert.Null(term.ValidUntilUtc);
    }

    [Fact]
    public void CommercialTerm_Lifetime_RejectsNonNullExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var ex = Assert.Throws<CentralDomainException>(() =>
            new CommercialTerm(CommercialTerm.Lifetime, now, now.AddYears(1)));

        Assert.Equal("LIFETIME_MUST_HAVE_NULL_EXPIRY", ex.Code);
    }

    [Fact]
    public void CommercialTerm_FixedTerm_RequiresNonNullExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var ex = Assert.Throws<CentralDomainException>(() =>
            new CommercialTerm(CommercialTerm.FixedTerm, now, null));

        Assert.Equal("FIXED_TERM_REQUIRES_EXPIRY", ex.Code);
    }

    [Fact]
    public void Policy_ConfigurableExtension_RequiresExtensionEndDate()
    {
        var now = DateTimeOffset.UtcNow;
        var ex = Assert.Throws<CentralDomainException>(() =>
            new Policy(
                shopId: Guid.NewGuid(),
                licenseId: Guid.NewGuid(),
                policyVersion: 1,
                enforcementMode: Policy.ConfigurableExtension,
                extensionEndUtc: null,
                afterExtensionPolicyId: Guid.NewGuid(),
                leaseDurationSeconds: 86400,
                offlineGraceSeconds: 604800,
                rollbackToleranceSeconds: 300,
                capabilityPolicyId: Guid.NewGuid(),
                approvedByAdminId: Guid.NewGuid(),
                approvedAtUtc: now));

        Assert.Equal("EXTENSION_REQUIRES_END_DATE", ex.Code);
    }

    [Fact]
    public void Policy_FullStop_ValidWithoutExtensionEnd()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new Policy(
            shopId: Guid.NewGuid(),
            licenseId: Guid.NewGuid(),
            policyVersion: 1,
            enforcementMode: Policy.FullStop,
            extensionEndUtc: null,
            afterExtensionPolicyId: Guid.NewGuid(),
            leaseDurationSeconds: 86400,
            offlineGraceSeconds: 604800,
            rollbackToleranceSeconds: 300,
            capabilityPolicyId: Guid.NewGuid(),
            approvedByAdminId: Guid.NewGuid(),
            approvedAtUtc: now);

        Assert.Equal(Policy.FullStop, policy.EnforcementMode);
        Assert.Null(policy.ExtensionEndUtc);
    }
}
