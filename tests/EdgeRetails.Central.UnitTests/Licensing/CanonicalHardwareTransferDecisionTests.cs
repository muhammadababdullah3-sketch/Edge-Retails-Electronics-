using EdgeRetails.Central.Domain.Licensing;

namespace EdgeRetails.Central.UnitTests.Licensing;

public sealed class CanonicalHardwareTransferDecisionTests
{
    [Fact]
    public void Hardware_review_persists_contract_decision_and_evidence_and_keeps_rebind_compatible()
    {
        var review = HardwareReview.Create(Guid.NewGuid(), Guid.NewGuid(), "reported-hash", "stored-hash", "change", DateTimeOffset.UtcNow);
        var evidence = Guid.NewGuid();

        Assert.NotEqual(Guid.Empty, review.ReportedMachineFingerprintId);
        Assert.NotEqual(Guid.Empty, review.StoredMachineFingerprintId);
        Assert.NotEqual(review.ReportedMachineFingerprintId, review.StoredMachineFingerprintId);

        review.Decide("APPROVE_REBIND", evidence, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(HardwareReview.StatusApproved, review.Status);
        Assert.Equal("APPROVE_REBIND", review.ContractDecision);
        Assert.Equal(evidence, review.VerificationEvidenceId);
        Assert.Equal(2, review.RowVersion);
    }

    [Fact]
    public void Hardware_review_persists_information_and_transfer_recommendation_as_distinct_states()
    {
        var actor = Guid.NewGuid();
        var evidence = Guid.NewGuid();
        var info = CreateReview();
        info.Decide("REQUEST_INFORMATION", evidence, actor, DateTimeOffset.UtcNow);
        var recommendation = CreateReview();
        recommendation.Decide("RECOMMEND_TRANSFER", evidence, actor, DateTimeOffset.UtcNow);

        Assert.Equal("REQUEST_INFORMATION", info.Status);
        Assert.Equal("RECOMMEND_TRANSFER", recommendation.Status);
        Assert.Equal(evidence, info.VerificationEvidenceId);
        Assert.Equal(evidence, recommendation.VerificationEvidenceId);
    }

    [Fact]
    public void Transfer_request_cancel_and_approval_decisions_are_persisted_with_recovery_evidence()
    {
        var shop = Guid.NewGuid();
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var approver = Guid.NewGuid();
        var evidence = Guid.NewGuid();
        var requested = PrimaryTransfer.Initiate(shop, source, target, "reason", requester, DateTimeOffset.UtcNow);
        requested.ApplyContractDecision("REQUEST", "STANDARD_TRANSFER", evidence, requester);
        Assert.Equal("REQUEST", requested.ContractDecision);
        Assert.Equal("STANDARD_TRANSFER", requested.RecoveryType);
        Assert.Equal(evidence, requested.VerificationEvidenceId);

        requested.Approve(approver, Guid.NewGuid());
        requested.ApplyContractDecision("APPROVE", "STANDARD_TRANSFER", evidence, approver);
        Assert.Equal("APPROVE", requested.ContractDecision);
        Assert.Equal(PrimaryTransfer.StatusApproved, requested.Status);

        var cancelled = PrimaryTransfer.Initiate(shop, source, target, "reason", requester, DateTimeOffset.UtcNow);
        cancelled.ApplyContractDecision("CANCEL", "LOST_HARDWARE_RECOVERY", evidence, approver);
        Assert.Equal("CANCEL", cancelled.ContractDecision);
        Assert.Equal(PrimaryTransfer.StatusCancelled, cancelled.Status);
    }

    private static HardwareReview CreateReview() => HardwareReview.Create(Guid.NewGuid(), Guid.NewGuid(), "reported", "stored", "change", DateTimeOffset.UtcNow);
}
