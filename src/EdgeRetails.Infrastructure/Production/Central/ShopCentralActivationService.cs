using System.Runtime.Versioning;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production.CentralActivation;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Infrastructure.Production.Client;

namespace EdgeRetails.Infrastructure.Production.Central;

[SupportedOSPlatform("windows")]
public sealed class ShopCentralActivationService : IShopCentralActivationService, IDisposable
{
    private readonly HttpClient _http;
    private readonly ShopCentralCanonicalJson _canonical = new();
    private readonly ClientInstallationIdentityStore _identityStore;
    private readonly IDeviceIdentityProvider _deviceIdentity;
    private readonly string _statePath;

    public ShopCentralActivationService(string centralBaseUrl, IDeviceIdentityProvider deviceIdentity, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(centralBaseUrl);
        _deviceIdentity = deviceIdentity ?? throw new ArgumentNullException(nameof(deviceIdentity));
        _identityStore = new ClientInstallationIdentityStore(ClientTerminalCredential.InstallationDirectory);
        _statePath = Path.Combine(ClientTerminalCredential.InstallationDirectory, "activation-state.json");
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(centralBaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public static ShopCentralActivationService? TryCreateFromEnvironment(IDeviceIdentityProvider deviceIdentity) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDGE_RETAILS_CENTRAL_BASE_URL"))
            ? null
            : new ShopCentralActivationService(
                Environment.GetEnvironmentVariable("EDGE_RETAILS_CENTRAL_BASE_URL")!,
                deviceIdentity);

    public bool IsConfigured => _http.BaseAddress is not null;

    public async Task<ShopActivationSubmissionResult> SubmitActivationTokenAsync(
        string activationToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(activationToken))
        {
            return Fail(Guid.Empty, "activation.token_required", "Activation token is required.");
        }

        using var identity = _identityStore.LoadOrCreate();
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(activationToken.Trim()))).ToLowerInvariant();
        var requestNonce = Guid.NewGuid();

        var challengeRequest = new
        {
            installationId = identity.InstallationId,
            installationPublicKey = identity.PublicKeyPem,
            activationTokenHash = tokenHash,
            requestNonce
        };

        using var challengeResponse = await _http.PostAsJsonAsync("api/licensing/v1/bootstrap/challenge", challengeRequest, cancellationToken);
        if (!challengeResponse.IsSuccessStatusCode)
        {
            return Fail(identity.InstallationId, "activation.challenge_failed", await challengeResponse.Content.ReadAsStringAsync(cancellationToken));
        }

        var challenge = await challengeResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var challengeId = challenge.GetProperty("challengeId").GetGuid();
        var challengeNonce = challenge.GetProperty("challengeNonce").GetString()!;
        var shopId = challenge.GetProperty("shopId").GetGuid();
        var licenseId = challenge.GetProperty("licenseId").GetGuid();
        identity.Bind(shopId, licenseId);
        _identityStore.SaveBinding(shopId, licenseId, identity);

        var deviceId = await _deviceIdentity.GetDeviceIdAsync(cancellationToken);
        var deviceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId))).ToLowerInvariant();
        var hardwareHash = deviceHash;

        var registrationPayload = new
        {
            shopId,
            licenseId,
            installationId = identity.InstallationId,
            installationPublicKey = identity.PublicKeyPem,
            installationPublicKeyFingerprint = identity.PublicKeyFingerprint,
            bootstrapChallengeId = challengeId,
            deviceIdentityVersion = 1,
            deviceIdentityHash = deviceHash,
            hardwareFingerprintVersion = 1,
            hardwareFingerprintHash = hardwareHash,
            signalQuality = "STRONG",
            applicationVersion = typeof(ShopCentralActivationService).Assembly.GetName().Version?.ToString() ?? "1.0.0"
        };

        var (_, registrationDigest) = _canonical.SerializeAndHash(registrationPayload);
        var registrationEnvelope = _canonical.SerializeCanonical(new
        {
            profile = "CENTRAL_INSTALLATION_BOOTSTRAP_V1",
            purpose = "installation.register",
            httpMethod = "POST",
            httpPath = "/api/licensing/v1/primary/register",
            challengeId,
            challengeNonce,
            requestNonce,
            activationTokenHash = tokenHash,
            payloadDigest = registrationDigest
        });
        var registrationProof = Convert.ToHexString(identity.PrivateKey.SignData(
            Encoding.UTF8.GetBytes(registrationEnvelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)).ToLowerInvariant();

        var registrationBody = new
        {
            meta = CreateMeta("installation.register", registrationPayload, "Client installation registration"),
            payload = new
            {
                registrationPayload.shopId,
                registrationPayload.licenseId,
                registrationPayload.installationId,
                registrationPayload.installationPublicKey,
                registrationPayload.installationPublicKeyFingerprint,
                registrationPayload.bootstrapChallengeId,
                registrationPayload.deviceIdentityVersion,
                registrationPayload.deviceIdentityHash,
                registrationPayload.hardwareFingerprintVersion,
                registrationPayload.hardwareFingerprintHash,
                registrationPayload.signalQuality,
                registrationPayload.applicationVersion,
                proofOfPossession = registrationProof
            }
        };

        using var registerResponse = await _http.PostAsJsonAsync("api/licensing/v1/primary/register", registrationBody, cancellationToken);
        if (!registerResponse.IsSuccessStatusCode)
        {
            return Fail(identity.InstallationId, "activation.register_failed", await registerResponse.Content.ReadAsStringAsync(cancellationToken));
        }

        var validationNonce = Guid.NewGuid();
        var redeemPayload = new
        {
            activationToken = activationToken.Trim(),
            installationId = identity.InstallationId,
            hardwareFingerprintHash = hardwareHash,
            installationPublicKeyFingerprint = identity.PublicKeyFingerprint,
            validationNonce,
            bootstrapChallengeId = challengeId
        };
        var (_, redeemDigest) = _canonical.SerializeAndHash(redeemPayload);
        var redeemEnvelope = _canonical.SerializeCanonical(new
        {
            profile = "CENTRAL_INSTALLATION_BOOTSTRAP_V1",
            purpose = "activation.redeem",
            httpMethod = "POST",
            httpPath = "/api/licensing/v1/activation/redeem",
            challengeId,
            challengeNonce,
            requestNonce,
            validationNonce,
            activationTokenHash = tokenHash,
            payloadDigest = redeemDigest
        });
        var redeemProof = Convert.ToHexString(identity.PrivateKey.SignData(
            Encoding.UTF8.GetBytes(redeemEnvelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)).ToLowerInvariant();

        var redeemBody = new
        {
            meta = CreateMeta("activation.redeem", redeemPayload, "Client activation redemption"),
            payload = new
            {
                redeemPayload.activationToken,
                redeemPayload.installationId,
                redeemPayload.hardwareFingerprintHash,
                redeemPayload.installationPublicKeyFingerprint,
                redeemPayload.validationNonce,
                redeemPayload.bootstrapChallengeId,
                proofOfPossession = redeemProof
            }
        };

        using var redeemResponse = await _http.PostAsJsonAsync("api/licensing/v1/activation/redeem", redeemBody, cancellationToken);
        if (!redeemResponse.IsSuccessStatusCode)
        {
            return Fail(identity.InstallationId, "activation.redeem_failed", await redeemResponse.Content.ReadAsStringAsync(cancellationToken));
        }

        var outcome = await redeemResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var resultReference = outcome.GetProperty("resultReference").GetString()!;
        var redemptionId = Guid.Parse(resultReference.Split('/').Last());

        SaveState(new ActivationState(identity.InstallationId, redemptionId, shopId, licenseId));

        return new ShopActivationSubmissionResult(true, redemptionId, identity.InstallationId, null, null);
    }

    public async Task<ShopActivationDeliveryStatus> PollDeliveryAsync(CancellationToken cancellationToken = default)
    {
        var state = LoadState();
        if (state is null)
        {
            return new ShopActivationDeliveryStatus(ShopActivationDeliveryState.NoRequest, null, null, null, null);
        }

        using var identity = _identityStore.LoadOrCreate();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/licensing/v1/installations/{state.InstallationId:D}/delivery");
        AddInstallationProof(request, identity, string.Empty);

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new ShopActivationDeliveryStatus(
                ShopActivationDeliveryState.Failed,
                state.RedemptionId,
                null,
                null,
                await response.Content.ReadAsStringAsync(cancellationToken));
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var deliveryState = json.GetProperty("state").GetString() ?? string.Empty;
        return deliveryState switch
        {
            "READY" => new ShopActivationDeliveryStatus(
                ShopActivationDeliveryState.Ready,
                state.RedemptionId,
                json.TryGetProperty("certificateId", out var cert) ? cert.GetGuid() : null,
                json.GetProperty("signedLicenseJson").GetString(),
                null),
            "PENDING_ADMIN_APPROVAL" => new ShopActivationDeliveryStatus(
                ShopActivationDeliveryState.PendingAdminApproval,
                state.RedemptionId,
                null,
                null,
                "Waiting for admin approval in Edge Retails cloud."),
            "REJECTED" => new ShopActivationDeliveryStatus(
                ShopActivationDeliveryState.Rejected,
                state.RedemptionId,
                null,
                null,
                "Activation was rejected by admin."),
            "APPROVED_AWAITING_CERTIFICATE" => new ShopActivationDeliveryStatus(
                ShopActivationDeliveryState.ApprovedAwaitingCertificate,
                state.RedemptionId,
                null,
                null,
                "Approved. Waiting for license certificate from admin."),
            _ => new ShopActivationDeliveryStatus(
                ShopActivationDeliveryState.NoRequest,
                state.RedemptionId,
                null,
                null,
                deliveryState)
        };
    }

    public void Dispose() => _http.Dispose();

    private object CreateMeta(string operationType, object payload, string reason)
    {
        var (_, hash) = _canonical.SerializeAndHash(payload);
        return new
        {
            clientOperationId = Guid.CreateVersion7(),
            operationType,
            payloadSha256 = hash,
            fingerprintVersion = 1,
            expectedVersion = 1,
            policyVersion = 0,
            reason
        };
    }

    private void AddInstallationProof(HttpRequestMessage request, ClientInstallationIdentity identity, string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid();
        var bodyDigest = body.Length == 0
            ? _canonical.ComputeSha256Hex(string.Empty)
            : _canonical.ComputeSha256Hex(_canonical.SerializeCanonical(body));
        var path = "/" + (request.RequestUri?.OriginalString ?? string.Empty).TrimStart('/');
        var envelope = _canonical.SerializeCanonical(new
        {
            profile = "CENTRAL_INSTALLATION_REQUEST_V1",
            purpose = "installation.request",
            keyId = identity.PublicKeyFingerprint,
            installationId = identity.InstallationId,
            shopId = identity.ShopId,
            licenseId = identity.LicenseId,
            timestamp,
            nonce,
            httpMethod = request.Method.Method.ToUpperInvariant(),
            httpPath = path,
            bodyDigest
        });
        var signature = identity.PrivateKey.SignData(Encoding.UTF8.GetBytes(envelope), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.Headers.Add("X-Installation-Id", identity.InstallationId.ToString("D"));
        request.Headers.Add("X-Installation-Key-Id", identity.PublicKeyFingerprint);
        request.Headers.Add("X-Installation-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Installation-Nonce", nonce.ToString("D"));
        request.Headers.Add("X-Installation-Signature", Convert.ToBase64String(signature));
    }

    private static ShopActivationSubmissionResult Fail(Guid installationId, string code, string? message) =>
        new(false, null, installationId, code, message);

    private void SaveState(ActivationState state) =>
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state));

    private ActivationState? LoadState() =>
        File.Exists(_statePath)
            ? JsonSerializer.Deserialize<ActivationState>(File.ReadAllText(_statePath))
            : null;

    private sealed record ActivationState(Guid InstallationId, Guid RedemptionId, Guid ShopId, Guid LicenseId);
}
