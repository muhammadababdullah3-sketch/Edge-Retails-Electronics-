using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace EdgeRetails.Central.Api.Security;

public readonly record struct CentralSessionCredentialResult(bool IsPresent, Guid SessionId, string? ErrorCode)
{
    public bool IsValid => IsPresent && ErrorCode is null && SessionId != Guid.Empty;
}

/// <summary>
/// Reads the existing session credential transports as one credential. A malformed or
/// conflicting transport cannot be hidden by a higher-precedence cookie/header.
/// This does not introduce a new token format or grant authority to a session ID.
/// </summary>
public static class CentralSessionCredentials
{
    public const string CookieName = "central_session";
    public const string AdminSessionHeader = "X-Admin-Session-Id";
    public const string SessionHeader = "X-Session-Id";

    public static CentralSessionCredentialResult Parse(HttpRequest request)
    {
        var candidates = new List<Guid>();
        var present = false;
        var invalid = false;

        void Add(string? value)
        {
            present = true;
            if (!Guid.TryParse(value?.Trim(), out var id) || id == Guid.Empty)
            {
                invalid = true;
                return;
            }
            candidates.Add(id);
        }

        // Read raw cookies too: IRequestCookieCollection otherwise hides duplicate names.
        var foundRawSessionCookie = false;
        foreach (var header in request.Headers.Cookie)
        {
            foreach (var part in (header ?? string.Empty).Split(';'))
            {
                var separator = part.IndexOf('=');
                var name = (separator < 0 ? part : part[..separator]).Trim();
                if (!string.Equals(name, CookieName, StringComparison.Ordinal))
                {
                    continue;
                }
                foundRawSessionCookie = true;
                Add(separator < 0 ? null : part[(separator + 1)..]);
            }
        }
        if (!foundRawSessionCookie && request.Cookies.TryGetValue(CookieName, out var cookie))
        {
            Add(cookie);
        }

        if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            present = true;
            if (authorization.Count != 1 ||
                !AuthenticationHeaderValue.TryParse(authorization[0], out var parsed) ||
                !string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                invalid = true;
            }
            else
            {
                Add(parsed.Parameter);
            }
        }

        foreach (var name in new[] { AdminSessionHeader, SessionHeader })
        {
            if (!request.Headers.TryGetValue(name, out var values))
            {
                continue;
            }
            present = true;
            if (values.Count != 1)
            {
                invalid = true;
                continue;
            }
            Add(values[0]);
        }

        if (invalid)
        {
            return new(present, Guid.Empty, "SESSION_CREDENTIAL_INVALID");
        }
        if (candidates.Distinct().Skip(1).Any())
        {
            return new(true, Guid.Empty, "SESSION_CREDENTIAL_CONFLICT");
        }
        return new(present, candidates.FirstOrDefault(), null);
    }

    /// <summary>
    /// Called only after live session/capability validation. Existing unleased controllers
    /// can then consume a canonical Bearer/cookie value without choosing another actor.
    /// No response cookie is minted and the incoming transport is still subject to CSRF.
    /// </summary>
    public static void NormalizeValidatedRequest(HttpRequest request, Guid sessionId)
    {
        var canonical = sessionId.ToString("D");
        if (request.Cookies.ContainsKey(CookieName))
        {
            var otherCookies = request.Headers.Cookie
                .SelectMany(header => (header ?? string.Empty).Split(';'))
                .Where(part => !string.Equals(part.Split('=', 2)[0].Trim(), CookieName, StringComparison.Ordinal))
                .Where(part => !string.IsNullOrWhiteSpace(part));
            request.Headers.Cookie = string.Join("; ", otherCookies.Append($"{CookieName}={canonical}"));
        }
        request.Headers.Authorization = $"Bearer {canonical}";
        foreach (var name in new[] { AdminSessionHeader, SessionHeader })
        {
            if (request.Headers.ContainsKey(name))
            {
                request.Headers[name] = canonical;
            }
        }
    }
}
