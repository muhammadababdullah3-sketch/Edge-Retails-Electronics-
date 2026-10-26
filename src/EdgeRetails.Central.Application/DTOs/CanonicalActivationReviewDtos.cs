using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CanonicalActivationDecisionDto(
    [property: JsonPropertyName("activationRequestId")] Guid ActivationRequestId,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("reasonCode")] string ReasonCode,
    [property: JsonPropertyName("validationSnapshotVersion")] int ValidationSnapshotVersion);
