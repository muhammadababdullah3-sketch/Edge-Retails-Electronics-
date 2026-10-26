using EdgeRetails.Central.Api.Middleware;
using EdgeRetails.Central.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace EdgeRetails.Central.UnitTests.Security;

public sealed class CentralExceptionHandlingMiddlewareTests
{
    [Fact]
    public async Task DomainException_LogsOnlySafeCode_AndDoesNotReturnSensitiveMessage()
    {
        const string secret = "customer@example.invalid password=synthetic-secret";
        var logger = new CapturingLogger();
        var context = NewContext();
        var middleware = new CentralExceptionHandlingMiddleware(
            _ => Task.FromException(new CentralDomainException("SAFE_CODE", "safe.error", secret)),
            logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var response = await ReadResponseAsync(context);
        Assert.DoesNotContain(secret, response);
        Assert.All(logger.Messages, message => Assert.DoesNotContain(secret, message, StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("SAFE_CODE", StringComparison.Ordinal));
        AssertRequestIdCorrelates(response, logger);
    }

    [Fact]
    public async Task UnhandledException_LogsTypeWithoutMessageOrStackTrace()
    {
        const string secret = "session-token=synthetic-token pii=customer@example.invalid";
        var logger = new CapturingLogger();
        var context = NewContext();
        var middleware = new CentralExceptionHandlingMiddleware(
            _ => Task.FromException(new InvalidOperationException(secret)),
            logger);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        var response = await ReadResponseAsync(context);
        Assert.DoesNotContain(secret, response);
        Assert.All(logger.Messages, message => Assert.DoesNotContain(secret, message, StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        AssertRequestIdCorrelates(response, logger);
    }

    [Fact]
    public async Task FailureAfterResponseStarted_AbortsWithoutLoggingExceptionContents()
    {
        const string secret = "mfa-code=synthetic-code customer@example.invalid";
        var logger = new CapturingLogger();
        var context = NewContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        Assert.True(context.Response.HasStarted);
        var middleware = new CentralExceptionHandlingMiddleware(
            _ => Task.FromException(new InvalidOperationException(secret)),
            logger);

        await middleware.InvokeAsync(context);

        Assert.All(logger.Messages, message => Assert.DoesNotContain(secret, message, StringComparison.Ordinal));
        Assert.Single(logger.RequestIds);
        Assert.Contains(logger.RequestIds[0].ToString("D"), string.Join('\n', logger.Messages), StringComparison.Ordinal);
    }

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private static void AssertRequestIdCorrelates(string response, CapturingLogger logger)
    {
        using var document = System.Text.Json.JsonDocument.Parse(response);
        var responseId = document.RootElement.GetProperty("requestId").GetGuid();

        Assert.Contains(responseId.ToString("D"), string.Join('\n', logger.Messages), StringComparison.Ordinal);
        Assert.Contains(responseId, logger.RequestIds);
    }

    private sealed class CapturingLogger : ILogger<CentralExceptionHandlingMiddleware>
    {
        public List<string> Messages { get; } = [];
        public List<Guid> RequestIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object>> values &&
                values.FirstOrDefault(value => value.Key == "CentralRequestId").Value is Guid requestId)
            {
                RequestIds.Add(requestId);
            }

            return null;
        }
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
