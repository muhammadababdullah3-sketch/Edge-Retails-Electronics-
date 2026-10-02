namespace EdgeRetails.Desktop.Services;

internal static class DesktopSessionShutdown
{
    internal static void EndSession(
        IBackendIdentityService identity,
        ISessionContext session,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var token = cancellation.Token;
        try
        {
            // WPF shutdown no longer pumps its dispatcher. Start the entire
            // logout await chain off that context, and bound even an HTTP
            // response body/handler that fails to observe cancellation.
            Task.Run(() => identity.SignOutAsync(session, token))
                .WaitAsync(token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Server-side session expiry remains authoritative if logout cannot
            // complete. A stalled request must not keep the Desktop process alive.
        }
    }
}
