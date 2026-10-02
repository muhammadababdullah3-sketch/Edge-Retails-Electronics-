using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Features.Terminals;

namespace EdgeRetails.Desktop.Services;

public sealed class DesktopApiException : Exception
{
    public DesktopApiException(string code, string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public HttpStatusCode? StatusCode { get; }
}

public sealed class DesktopSessionInvalidatedEventArgs(Guid sessionId, string code) : EventArgs
{
    public Guid SessionId { get; } = sessionId;
    public string Code { get; } = code;
}

/// <summary>
/// The single HTTP boundary for Desktop requests to the local shop Server.
/// Session and terminal credentials are applied to each request at send time.
/// </summary>
public sealed class DesktopApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly object _contextGate = new();
    private Guid? _terminalId;
    private string? _terminalSecret;
    private Guid? _sessionId;

    public Guid? CurrentSessionId
    {
        get
        {
            lock (_contextGate)
            {
                return _sessionId;
            }
        }
    }

    public event EventHandler<DesktopSessionInvalidatedEventArgs>? SessionInvalidated;

    public DesktopApiClient(HttpClient httpClient, bool ownsClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsClient = ownsClient;
        if (_httpClient.BaseAddress is null ||
            !_httpClient.BaseAddress.IsAbsoluteUri ||
            _httpClient.BaseAddress.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(_httpClient.BaseAddress.Host, out var address) ||
            !IPAddress.IsLoopback(address))
        {
            throw new ArgumentException(
                "Desktop Server URL must be an HTTP loopback IP address.",
                nameof(httpClient));
        }
    }

    public void SetTerminalContext(Guid terminalId, string terminalSecret)
    {
        if (terminalId == Guid.Empty || string.IsNullOrWhiteSpace(terminalSecret))
        {
            throw new ArgumentException("A registered terminal ID and secret are required.");
        }

        lock (_contextGate)
        {
            _terminalId = terminalId;
            _terminalSecret = terminalSecret;
        }
    }

    public void SetSession(Guid? sessionId)
    {
        lock (_contextGate)
        {
            _sessionId = sessionId;
        }
    }

    public void ClearSessionIfMatches(Guid sessionId)
    {
        lock (_contextGate)
        {
            if (_sessionId == sessionId)
            {
                _sessionId = null;
            }
        }
    }

    internal void InvalidateSessionIfMatches(Guid sessionId, string code)
    {
        lock (_contextGate)
        {
            if (_sessionId != sessionId)
            {
                return;
            }

            _sessionId = null;
        }

        SessionInvalidated?.Invoke(this, new DesktopSessionInvalidatedEventArgs(sessionId, code));
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        return await SendAsync<T>(request, cancellationToken);
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest payload,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        return await SendAsync<TResponse>(request, cancellationToken);
    }

    public async Task<TResponse> PutAsync<TRequest, TResponse>(
        string path,
        TRequest payload,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Put, path);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        return await SendAsync<TResponse>(request, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/api/", StringComparison.Ordinal) ||
            Uri.TryCreate(path, UriKind.Absolute, out _))
        {
            throw new ArgumentException("API path must start with /api/.", nameof(path));
        }

        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Protocol-Version", TerminalProtocol.CurrentProtocolVersion);
        lock (_contextGate)
        {
            if (_terminalId is Guid terminalId)
            {
                request.Headers.Add("X-Terminal-Id", terminalId.ToString("D"));
                request.Headers.Add("X-Terminal-Secret", _terminalSecret);
            }

            if (_sessionId is Guid sessionId)
            {
                request.Headers.Add("X-Session-Id", sessionId.ToString("D"));
            }
        }

        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                ErrorBody? error = null;
                try
                {
                    error = await response.Content.ReadFromJsonAsync<ErrorBody>(JsonOptions, cancellationToken);
                }
                catch (JsonException)
                {
                    // A proxy/server failure may have no canonical JSON body.
                }

                throw new DesktopApiException(
                    error?.Code ?? (response.StatusCode == HttpStatusCode.ServiceUnavailable
                        ? "system.not_ready"
                        : $"http.{(int)response.StatusCode}"),
                    error?.Message ?? error?.FailureReason ??
                    $"Server request failed with HTTP {(int)response.StatusCode}.",
                    response.StatusCode);
            }

            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
            return result ?? throw new DesktopApiException(
                "gateway.empty_response", "Server returned an empty response.");
        }
        catch (HttpRequestException)
        {
            throw new DesktopApiException(
                "network.server_unavailable", "The local Server is unavailable.", null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DesktopApiException(
                "network.timeout", "The local Server did not respond in time.", null);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed record ErrorBody(string? Code, string? Message, string? FailureReason);
}

internal sealed class DesktopSessionForwardingHandler(
    Func<Guid?> getSessionId,
    Action<Guid, string>? invalidateSession = null)
    : DelegatingHandler(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    })
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains("X-Session-Id") && getSessionId() is Guid sessionId)
        {
            request.Headers.Add("X-Session-Id", sessionId.ToString("D"));
        }

        return SendAndObserveSessionAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAndObserveSessionAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) ||
            !Guid.TryParse(request.Headers.TryGetValues("X-Session-Id", out var values)
                ? values.SingleOrDefault()
                : null, out var requestSessionId))
        {
            return response;
        }

        var originalContent = response.Content;
        var body = await originalContent.ReadAsStringAsync(cancellationToken);
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("code", out var codeElement))
            {
                code = codeElement.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-canonical failures do not invalidate a verified session.
        }

        if (code is "auth.session_invalid" or "auth.session_missing" or "auth.user_disabled" or
            "auth.role_disabled" or "auth.terminal_revoked" or "auth.terminal_suspended")
        {
            invalidateSession?.Invoke(requestSessionId, code);
        }

        var replacement = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body));
        foreach (var header in originalContent.Headers)
        {
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = replacement;
        originalContent.Dispose();
        return response;
    }
}
