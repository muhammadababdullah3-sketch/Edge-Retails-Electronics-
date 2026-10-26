using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

public class LoginRequestDto
{
    [JsonPropertyName("username")]
    [Required]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("passwordOrProviderAssertion")]
    public string PasswordOrProviderAssertion { get; set; } = string.Empty;

    [JsonPropertyName("challengeId")]
    public Guid? ChallengeId { get; set; }

    /// <summary>
    /// Local disposable development only. When true, password is skipped and the next step is TOTP.
    /// The API ignores this flag outside Development + localhost.
    /// </summary>
    [JsonPropertyName("localPasswordBypass")]
    public bool LocalPasswordBypass { get; set; }
}

public class PreAuthChallengeDto
{
    [JsonPropertyName("kind")]
    public string Kind => "MFA_CHALLENGE";

    [JsonPropertyName("challengeId")]
    public Guid ChallengeId { get; set; }

    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset ExpiresAtUtc { get; set; }

    [JsonPropertyName("businessAuthority")]
    public bool BusinessAuthority => false;
}

public class MfaRequestDto
{
    [JsonPropertyName("loginChallengeId")]
    public Guid LoginChallengeId { get; set; }

    [JsonPropertyName("proof")]
    [Required]
    public string Proof { get; set; } = string.Empty;

    [JsonPropertyName("actionScope")]
    public string? ActionScope { get; set; }
}

public class AdminSessionDto
{
    [JsonPropertyName("kind")]
    public string Kind => "AUTHENTICATED";

    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; set; }

    [JsonPropertyName("adminUserId")]
    public Guid AdminUserId { get; set; }

    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset ExpiresAtUtc { get; set; }

    [JsonPropertyName("absoluteExpiresAtUtc")]
    public DateTimeOffset AbsoluteExpiresAtUtc { get; set; }

    [JsonPropertyName("mfaSatisfied")]
    public bool MfaSatisfied => true;

    [JsonPropertyName("adminSecurityRevision")]
    public int AdminSecurityRevision { get; set; }

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();
}

public class StepUpRequestDto
{
    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; set; }

    [JsonPropertyName("actionScope")]
    [Required]
    public string ActionScope { get; set; } = string.Empty;

    [JsonPropertyName("targetPayloadSha256")]
    [Required]
    public string TargetPayloadSha256 { get; set; } = string.Empty;

    [JsonPropertyName("proof")]
    [Required]
    public string Proof { get; set; } = string.Empty;

    [JsonPropertyName("challengeId")]
    public Guid ChallengeId { get; set; }
}

public class StepUpGrantDto
{
    [JsonPropertyName("sessionId")]
    public Guid SessionId { get; set; }

    [JsonPropertyName("actionScope")]
    public string ActionScope { get; set; } = string.Empty;

    [JsonPropertyName("targetPayloadSha256")]
    public string TargetPayloadSha256 { get; set; } = string.Empty;

    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset ExpiresAtUtc { get; set; }

    [JsonPropertyName("stepUpGrantId")]
    public Guid StepUpGrantId { get; set; }
}
