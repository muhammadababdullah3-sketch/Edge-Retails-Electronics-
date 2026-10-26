using System.Globalization;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.ProtectedSigner.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Security;

public sealed class InstallationRequestProofService
{
    private readonly ICentralDbContext _db;
    private readonly ICanonicalJsonSerializer _canonical;
    private readonly IProtectedSigningEngine _signer;

    public InstallationRequestProofService(ICentralDbContext db, ICanonicalJsonSerializer canonical, IProtectedSigningEngine signer)
    {
        _db = db;
        _canonical = canonical;
        _signer = signer;
    }

    public async Task<bool> AuthenticateAsync(HttpRequest request, bool allowPendingBootstrap, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.Headers["X-Installation-Id"], out var installationId) || installationId == Guid.Empty ||
            !Guid.TryParse(request.Headers["X-Installation-Nonce"], out var requestNonce) || requestNonce == Guid.Empty ||
            !long.TryParse(request.Headers["X-Installation-Timestamp"], NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return false;
        }

        var keyId = request.Headers["X-Installation-Key-Id"].ToString();
        var signature = request.Headers["X-Installation-Signature"].ToString();
        if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 128 || string.IsNullOrWhiteSpace(signature) || signature.Length > 2048)
        {
            return false;
        }

        var windowText = Environment.GetEnvironmentVariable("CENTRAL_INSTALLATION_PROOF_WINDOW_SECONDS");
        if (!int.TryParse(windowText, NumberStyles.None, CultureInfo.InvariantCulture, out var windowSeconds) || windowSeconds is < 1 or > 600)
        {
            return false;
        }
        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        var now = DateTimeOffset.UtcNow;
        if ((now - timestamp).Duration() > TimeSpan.FromSeconds(windowSeconds))
        {
            return false;
        }

        var installation = await _db.Installations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == installationId, cancellationToken);
        if (installation is null || installation.Status != Installation.StatusActive &&
            !(allowPendingBootstrap && installation.Status == Installation.StatusPendingApproval))
        {
            return false;
        }
        var fingerprint = Installation.ComputePublicKeyFingerprintSha256(installation.PublicKeyPem);
        if (!string.Equals(fingerprint, installation.PublicKeyFingerprintSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(keyId, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isBinary = MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) &&
            string.Equals(mediaType.MediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
        ContentRangeHeaderValue? binaryRange = null;
        var chunkDigest = request.Headers["chunk-sha256"].ToString().ToLowerInvariant();
        if (isBinary && (!StorageUploadLimits.TryRead(out var limits) ||
            request.ContentLength is not long declaredLength || declaredLength < 1 || declaredLength > limits.MaxChunkBytes ||
            !ContentRangeHeaderValue.TryParse(request.Headers["Content-Range"], out binaryRange) ||
            !string.Equals(binaryRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            binaryRange.From is null || binaryRange.To is null || binaryRange.Length is null ||
            binaryRange.Length > limits.MaxObjectBytes || binaryRange.To - binaryRange.From + 1 != declaredLength ||
            chunkDigest.Length != 64 || !chunkDigest.All(Uri.IsHexDigit)))
        {
            return false;
        }

        request.EnableBuffering();
        string bodyDigest;
        if (isBinary)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, request.ContentLength!.Value - received + 1)), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                received += read;
                if (received > request.ContentLength)
                {
                    return false;
                }
                hash.AppendData(buffer, 0, read);
            }
            request.Body.Position = 0;
            bodyDigest = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (received != request.ContentLength || !string.Equals(bodyDigest, chunkDigest, StringComparison.Ordinal))
            {
                return false;
            }
        }
        else
        {
            string body;
            using (var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync(cancellationToken);
            }
            request.Body.Position = 0;
            if (body.Length == 0)
            {
                // Bodyless GET requests have one unambiguous representation in the
                // installation proof profile: SHA-256 of zero body bytes.
                bodyDigest = _canonical.ComputeSha256Hex(string.Empty);
            }
            else
            {
                try
                {
                    bodyDigest = _canonical.ComputeSha256Hex(_canonical.SerializeCanonical(body));
                }
                catch (System.Text.Json.JsonException)
                {
                    return false;
                }
            }
        }

        var path = request.PathBase.Add(request.Path).Value ?? string.Empty;
        if (request.QueryString.HasValue)
        {
            path += request.QueryString.Value;
        }
        var proofPayload = new Dictionary<string, object?>
        {
            ["profile"] = isBinary ? "CENTRAL_INSTALLATION_BINARY_REQUEST_V1" : "CENTRAL_INSTALLATION_REQUEST_V1",
            ["purpose"] = "installation.request",
            ["keyId"] = fingerprint,
            ["installationId"] = installationId,
            ["shopId"] = installation.ShopId,
            ["licenseId"] = installation.LicenseId,
            ["timestamp"] = unixSeconds,
            ["nonce"] = requestNonce,
            ["httpMethod"] = request.Method.ToUpperInvariant(),
            ["httpPath"] = path,
            ["bodyDigest"] = bodyDigest
        };
        if (isBinary)
        {
            proofPayload["binaryRange"] = new { from = binaryRange!.From, to = binaryRange.To, totalLength = binaryRange.Length, contentLength = request.ContentLength, chunkSha256 = chunkDigest };
        }
        var envelope = _canonical.SerializeCanonical(proofPayload);
        if (!_signer.VerifySignature(installation.PublicKeyPem, envelope, signature))
        {
            return false;
        }

        if (allowPendingBootstrap)
        {
            var idPart = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(4);
            if (!Guid.TryParse(idPart, out var redemptionId) ||
                !await _db.ActivationRedemptions.AsNoTracking().AnyAsync(x => x.Id == redemptionId && x.InstallationId == installation.Id, cancellationToken))
            {
                return false;
            }
        }

        var nonceValue = requestNonce.ToString("D");
        if (await _db.InstallationRequestNonces.AsNoTracking().AnyAsync(
                x => x.InstallationId == installation.Id && x.Nonce == nonceValue, cancellationToken))
        {
            return false;
        }

        var expiredNonces = await _db.InstallationRequestNonces
            .Where(x => x.TimestampUtc < now.AddSeconds(-600))
            .ToListAsync(cancellationToken);
        _db.InstallationRequestNonces.RemoveRange(expiredNonces);
        var nonceRecord = InstallationRequestNonce.Create(installation.Id, nonceValue, timestamp);
        _db.InstallationRequestNonces.Add(nonceRecord);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _db.InstallationRequestNonces.Remove(nonceRecord);
            return false;
        }
        request.HttpContext.Items["AuthenticatedInstallation"] = installation;
        return true;
    }
}
