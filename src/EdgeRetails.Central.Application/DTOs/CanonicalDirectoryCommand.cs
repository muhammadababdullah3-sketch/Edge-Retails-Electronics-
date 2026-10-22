using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CanonicalDirectoryCommand
{
    public required CanonicalDirectoryMeta Meta { get; init; }
    public required JsonElement Payload { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CanonicalDirectoryMeta
{
    public required Guid ClientOperationId { get; init; }
    public required string OperationType { get; init; }
    public required int FingerprintVersion { get; init; }
    public required string PayloadSha256 { get; init; }
    public required int ExpectedVersion { get; init; }
    public required int PolicyVersion { get; init; }
    public required string Reason { get; init; }
    public Guid? SensitiveApprovalId { get; init; }

    public CommandMetaDto ToLedgerMeta() => new()
    {
        ClientOperationId = ClientOperationId, OperationType = OperationType, FingerprintVersion = FingerprintVersion,
        PayloadSha256 = PayloadSha256, ExpectedVersion = ExpectedVersion, PolicyVersion = PolicyVersion,
        Reason = Reason, SensitiveApprovalId = SensitiveApprovalId
    };
}
