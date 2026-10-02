using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3AuthenticatedShutdownTests
{
    private static readonly TimeSpan JoinLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StallDeadline = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task EndSession_OnStaWithNonpumpingContext_CompletesDelayedHttpLogout()
    {
        using var handler = new LogoutHandler(respectCancellation: true);
        using var http = CreateHttpClient(handler);
        using var api = new DesktopApiClient(http);
        var session = CreateSession(api);
        var identity = new BackendIdentityService(api);
        var sta = new ShutdownSta(() => DesktopSessionShutdown.EndSession(identity, session, JoinLimit));

        try
        {
            await handler.Started.Task.WaitAsync(JoinLimit);
            handler.ReleaseResponse();

            Assert.True(sta.Join(JoinLimit), "Authenticated shutdown did not return on the STA thread.");
            Assert.Null(sta.Failure);
            AssertLogoutRequest(handler, session);
            Assert.Null(api.CurrentSessionId);
        }
        finally
        {
            Cleanup(sta, handler);
        }
    }

    [Fact]
    public async Task EndSession_OnStaWithNonpumpingContext_CancelsStalledHttpLogout()
    {
        using var handler = new LogoutHandler(respectCancellation: true);
        using var http = CreateHttpClient(handler);
        using var api = new DesktopApiClient(http);
        var session = CreateSession(api);
        var identity = new BackendIdentityService(api);
        var sta = new ShutdownSta(() => DesktopSessionShutdown.EndSession(identity, session, StallDeadline));

        try
        {
            await handler.Started.Task.WaitAsync(JoinLimit);

            Assert.True(sta.Join(JoinLimit), "Cancellation did not release authenticated shutdown.");
            Assert.Null(sta.Failure);
            await handler.CancellationObserved.Task.WaitAsync(JoinLimit);
            AssertLogoutRequest(handler, session);
            Assert.True(SpinWait.SpinUntil(() => api.CurrentSessionId is null, JoinLimit),
                "The cancelled logout did not clear the matching client session.");
        }
        finally
        {
            Cleanup(sta, handler);
        }
    }

    [Fact]
    public async Task EndSession_OnStaWithNonpumpingContext_ReturnsWhenHttpIgnoresCancellation()
    {
        using var handler = new LogoutHandler(respectCancellation: false);
        using var http = CreateHttpClient(handler);
        using var api = new DesktopApiClient(http);
        var session = CreateSession(api);
        var identity = new BackendIdentityService(api);
        var sta = new ShutdownSta(() => DesktopSessionShutdown.EndSession(identity, session, StallDeadline));

        try
        {
            await handler.Started.Task.WaitAsync(JoinLimit);

            Assert.True(sta.Join(JoinLimit), "Authenticated shutdown waited for a cancellation-ignoring HTTP response.");
            Assert.Null(sta.Failure);
            Assert.False(handler.ResponseReleased);
            Assert.True(handler.RequestCancellation.IsCancellationRequested);
            AssertLogoutRequest(handler, session);
        }
        finally
        {
            Cleanup(sta, handler);
        }
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("http://127.0.0.1:7150"),
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static UserSessionContext CreateSession(DesktopApiClient api)
    {
        var session = new UserSessionContext(
            "Synthetic Owner", "Owner", "SO", userId: Guid.NewGuid(), sessionId: Guid.NewGuid());
        api.SetSession(session.SessionId);
        return session;
    }

    private static void AssertLogoutRequest(LogoutHandler handler, UserSessionContext session)
    {
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/auth/logout", request.Path);
        Assert.Equal(session.SessionId, request.HeaderSessionId);
        Assert.Equal(session.UserId, request.UserId);
        Assert.Equal(session.SessionId, request.BodySessionId);
    }

    private static void Cleanup(ShutdownSta sta, LogoutHandler handler)
    {
        // Failed pre-fix calls can have pending continuations on the deliberately
        // nonpumping context. Release them on pool threads, never by pumping STA.
        sta.ReleasePendingContinuations();
        handler.ReleaseResponse();
        sta.Join(TimeSpan.FromSeconds(1));
    }

    private sealed record LogoutRequest(
        HttpMethod Method, string? Path, Guid HeaderSessionId, Guid UserId, Guid BodySessionId);

    private sealed class LogoutHandler(bool respectCancellation) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _responseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<LogoutRequest> Requests { get; } = new();
        public CancellationToken RequestCancellation { get; private set; }
        public bool ResponseReleased => _responseGate.Task.IsCompleted;

        public void ReleaseResponse() => _responseGate.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(body);
            Requests.Enqueue(new LogoutRequest(
                request.Method,
                request.RequestUri?.AbsolutePath,
                Guid.Parse(request.Headers.GetValues("X-Session-Id").Single()),
                json.RootElement.GetProperty("userId").GetGuid(),
                json.RootElement.GetProperty("sessionId").GetGuid()));
            RequestCancellation = cancellationToken;
            Started.TrySetResult(true);

            try
            {
                if (respectCancellation)
                {
                    await _responseGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _responseGate.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(true);
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { success = true })
            };
        }
    }

    private sealed class ShutdownSta
    {
        private readonly NonpumpingContext _context = new();
        private readonly Thread _thread;
        public Exception? Failure { get; private set; }

        public ShutdownSta(Action shutdown)
        {
            _thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(_context);
                try
                {
                    shutdown();
                }
                catch (Exception exception)
                {
                    Failure = exception;
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                }
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public bool Join(TimeSpan timeout) => _thread.Join(timeout);
        public void ReleasePendingContinuations() => _context.ReleasePending();
    }

    private sealed class NonpumpingContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private bool _released;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                if (!_released)
                {
                    _pending.Enqueue((callback, state));
                    return;
                }
            }

            QueueOnPool(callback, state);
        }

        public void ReleasePending()
        {
            (SendOrPostCallback Callback, object? State)[] pending;
            lock (_gate)
            {
                _released = true;
                pending = _pending.ToArray();
                _pending.Clear();
            }

            foreach (var work in pending)
            {
                QueueOnPool(work.Callback, work.State);
            }
        }

        private static void QueueOnPool(SendOrPostCallback callback, object? state)
            => ThreadPool.QueueUserWorkItem(_ => callback(state));
    }
}
