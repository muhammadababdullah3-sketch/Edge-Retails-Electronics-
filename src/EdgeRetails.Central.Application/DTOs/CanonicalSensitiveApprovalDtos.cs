using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CanonicalSensitiveApprovalMeta
{
    public required Guid ClientOperationId { get; init; }
    public required string OperationType { get; init; }
    public required int FingerprintVersion { get; init; }
    public required string PayloadSha256 { get; init; }
    public required int ExpectedVersion { get; init; }
    public required int PolicyVersion { get; init; }
    public required string Reason { get; init; }
    public Guid? SensitiveApprovalId { get; init; }

    public CanonicalDirectoryMeta ToCanonicalMeta() => new()
    {
        ClientOperationId = ClientOperationId,
        OperationType = OperationType,
        FingerprintVersion = FingerprintVersion,
        PayloadSha256 = PayloadSha256,
        ExpectedVersion = ExpectedVersion,
        PolicyVersion = PolicyVersion,
        Reason = Reason,
        SensitiveApprovalId = SensitiveApprovalId
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CanonicalSensitiveApprovalDecisionPayload
{
    public required string TargetActionType { get; init; }
    public required Guid TargetId { get; init; }
    public required string TargetPayloadSha256 { get; init; }
    public required Guid RequesterAdminId { get; init; }
    public required Guid ApproverAdminId { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required string Decision { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CanonicalSensitiveApprovalCommand
{
    public required CanonicalSensitiveApprovalMeta Meta { get; init; }
    public required CanonicalSensitiveApprovalDecisionPayload Payload { get; init; }
}
