using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Application.Production.Licensing;

namespace EdgeRetails.Infrastructure.Production.Licensing;

public sealed class RsaSha256LicenseSignatureVerifier : ILicenseSignatureVerifier
{
    private readonly ILicensePublicKeyProvider _publicKey;
    public string Algorithm => "RS256";

    public RsaSha256LicenseSignatureVerifier(ILicensePublicKeyProvider publicKey)
        => _publicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));

    public bool Verify(byte[] payload, byte[] signature)
    {
        var pem = _publicKey.GetPublicKeyPem();
        if (string.IsNullOrWhiteSpace(pem) ||
            pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("License verification requires public-key material only.");
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}

public sealed class SignedLicenseValidator : ILicenseValidator
{
    private const int MaximumSignedLicenseBytes = 1024 * 1024;
    private readonly ILicenseSignatureVerifier _signatureVerifier;
    private readonly IDeviceIdentityProvider _deviceIdentity;
    private readonly TimeProvider _timeProvider;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SignedLicenseValidator(ILicenseSignatureVerifier signatureVerifier, IDeviceIdentityProvider deviceIdentity, TimeProvider? timeProvider = null)
    {
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _deviceIdentity = deviceIdentity ?? throw new ArgumentNullException(nameof(deviceIdentity));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signedLicenseJson))
        {
            return new LicenseValidationResult(
                LicenseValidationStatus.Corrupt,
                null,
                "License content is empty.",
                "license.empty");
        }

        if (Encoding.UTF8.GetByteCount(signedLicenseJson) > MaximumSignedLicenseBytes)
        {
            return new LicenseValidationResult(
                LicenseValidationStatus.Corrupt,
                null,
                "License artifact exceeds the supported size limit.",
                "license.too_large");
        }

        try
        {
            SignedLicenseEnvelope? envelope;
            try { envelope = JsonSerializer.Deserialize<SignedLicenseEnvelope>(signedLicenseJson, JsonOptions); }
            catch (JsonException) { return new(LicenseValidationStatus.Corrupt, null, "License file is not valid JSON.", "license.corrupt_json"); }

            if (envelope is null || string.IsNullOrWhiteSpace(envelope.Algorithm) || string.IsNullOrWhiteSpace(envelope.PayloadBase64) || string.IsNullOrWhiteSpace(envelope.SignatureBase64))
            {
                return new(LicenseValidationStatus.Corrupt, null, "License envelope is incomplete.", "license.incomplete_envelope");
            }

            if (!string.Equals(envelope.Algorithm, _signatureVerifier.Algorithm, StringComparison.Ordinal))
            {
                return new(LicenseValidationStatus.UnsupportedAlgorithm, null, "License signature algorithm is unsupported by this application build.", "license.unsupported_algorithm");
            }

            byte[] payloadBytes;
            byte[] signatureBytes;
            LicensePayload? payload;
            try
            {
                payloadBytes = Convert.FromBase64String(envelope.PayloadBase64);
                signatureBytes = Convert.FromBase64String(envelope.SignatureBase64);
                payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes, JsonOptions);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return new(LicenseValidationStatus.Corrupt, null, "License payload is corrupt.", "license.corrupt_payload");
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.LicenseId) || string.IsNullOrWhiteSpace(payload.DeviceId))
            {
                return new(LicenseValidationStatus.Corrupt, payload, "License payload is incomplete.", "license.incomplete_payload");
            }

            bool signatureValid;
            try { signatureValid = _signatureVerifier.Verify(payloadBytes, signatureBytes); }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                return new(LicenseValidationStatus.ValidationUnavailable, payload, "License signature verification could not be completed safely.", "license.crypto_unavailable");
            }
            if (!signatureValid)
            {
                return new(LicenseValidationStatus.InvalidSignature, payload, "License signature validation failed.", "license.invalid_signature");
            }

            var deviceId = await _deviceIdentity.GetDeviceIdAsync(cancellationToken);
            if (!CryptographicEquals(payload.DeviceId, deviceId))
            {
                return new(LicenseValidationStatus.DeviceMismatch, payload, "License is bound to a different device.", "license.device_mismatch");
            }

            var now = _timeProvider.GetUtcNow();
            if (payload.ExpiryDate is not null && payload.ExpiryDate.Value <= now)
            {
                return new(LicenseValidationStatus.Expired, payload, "License has expired.", "license.expired");
            }

            return new(LicenseValidationStatus.Valid, payload, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new LicenseValidationResult(
                LicenseValidationStatus.ValidationUnavailable,
                null,
                "License validation could not be completed safely.",
                "license.validation_unavailable");
        }
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
