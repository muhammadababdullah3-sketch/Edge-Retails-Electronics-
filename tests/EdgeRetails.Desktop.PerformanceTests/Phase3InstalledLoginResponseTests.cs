using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3InstalledLoginResponseTests
{
    [Fact]
    public async Task Desktop_accepts_Server_login_and_session_permission_arrays_over_loopback()
    {
        var userId = Guid.CreateVersion7();
        var sessionId = Guid.CreateVersion7();
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri?.AbsolutePath}");
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/auth/login")
            {
                return JsonResponse(new
                {
                    userId,
                    sessionId,
                    displayName = "Mock Owner",
                    roleName = "Owner",
                    initials = "MO",
                    permissionKeys = new[] { "dashboard.view", "sales.pos.use" }
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/auth/session")
            {
                Assert.Equal(sessionId.ToString("D"), request.Headers.GetValues("X-Session-Id").Single());
                Assert.Equal("mock-terminal-secret", request.Headers.GetValues("X-Terminal-Secret").Single());
                return JsonResponse(new
                {
                    userId,
                    sessionId,
                    displayName = "Mock Owner",
                    roleName = "Owner",
                    permissions = new[] { "dashboard.view", "sales.pos.use" }
                });
            }

            throw new InvalidOperationException("Unexpected Desktop API request.");
        })) { BaseAddress = new Uri("http://127.0.0.1:7150") };
        using var api = new DesktopApiClient(http);
        api.SetTerminalContext(Guid.CreateVersion7(), "mock-terminal-secret");
        var identity = new BackendIdentityService(api);

        var result = await identity.AuthenticateAsync(userId.ToString("D"), "2468");

        Assert.Equal(userId, result.UserId);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Contains("sales.pos.use", result.PermissionKeys);
        Assert.Equal(new[] { "POST /api/auth/login", "GET /api/auth/session" }, requests);
    }

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
