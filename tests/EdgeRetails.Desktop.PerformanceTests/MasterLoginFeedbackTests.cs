using System.IO;
using System.Xml.Linq;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

// NEW_COVERAGE: synthetic identity service only; no installed authentication/PIN.
public sealed class MasterLoginFeedbackTests
{
    [Fact]
    public void FirstThreeDigitsNeverStartANetworkRequest()
    {
        var identity = new ControlledIdentity();
        var view = new LoginViewModel(backendIdentityService: identity);
        foreach (var digit in new[] { "1", "2", "3" }) { view.AppendDigit(digit); }
        Assert.Equal(3, view.PinLength);
        Assert.False(view.IsSigningIn);
        Assert.Equal(0, identity.AuthenticationCalls);
    }

    [Fact]
    public async Task SubmissionClearsPinAndShowsFeedbackWhilePreventingDuplicateRequests()
    {
        var identity = new ControlledIdentity();
        var view = new LoginViewModel(backendIdentityService: identity);
        var finished = ObserveFinished(view);
        foreach (var digit in new[] { "1", "2", "3", "4" }) { view.AppendDigit(digit); }
        Assert.True(view.IsSigningIn);
        Assert.Equal(1, identity.AuthenticationCalls);
        Assert.Equal(0, view.PinLength);
        Assert.Equal("Signing in…", view.HelperText);
        view.AppendDigit("5");
        await view.AuthenticateAsync();
        Assert.Equal(1, identity.AuthenticationCalls);
        identity.Result.SetException(new DesktopApiException("network.timeout", "Synthetic timeout"));
        await finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(view.IsSigningIn);
    }

    [Fact]
    public async Task LoginTimeoutGivesASignInSpecificActionInsteadOfAnUnavailableOperationStatus()
    {
        var identity = new ControlledIdentity();
        var view = new LoginViewModel(backendIdentityService: identity);
        var finished = ObserveFinished(view);
        foreach (var digit in new[] { "1", "2", "3", "4" }) { view.AppendDigit(digit); }
        identity.Result.SetException(new DesktopApiException("network.timeout", "Synthetic timeout"));
        await finished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(view.HasError);
        Assert.Equal(0, view.PinLength);
        Assert.Contains("Sign in timed out", view.HelperText, StringComparison.Ordinal);
        Assert.DoesNotContain("operation status", view.HelperText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoginHelperHasAWidthBoundAndWrapsTheCompleteMessage()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "EdgeRetails.Desktop"))) { root = root.Parent; }
        Assert.NotNull(root);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var document = XDocument.Load(Path.Combine(root.FullName, "src", "EdgeRetails.Desktop", "Views", "LoginView.xaml"));
        var helper = Assert.Single(document.Descendants(presentation + "TextBlock"),
            element => (string?)element.Attribute("Text") == "{Binding HelperText}");
        Assert.Equal("Wrap", (string?)helper.Attribute("TextWrapping"));
        Assert.Equal("342", (string?)helper.Attribute("Width"));
        Assert.Equal("Center", (string?)helper.Attribute("TextAlignment"));
    }

    private static Task ObserveFinished(LoginViewModel view)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LoginViewModel.IsSigningIn) && !view.IsSigningIn) { completed.TrySetResult(); }
        };
        return completed.Task;
    }

    private sealed class ControlledIdentity : IBackendIdentityService
    {
        private readonly Guid _user = Guid.NewGuid();
        public int AuthenticationCalls { get; private set; }
        public TaskCompletionSource<BackendAuthenticatedSession> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<BackendLoginAccount>> GetAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BackendLoginAccount>>([new(_user, "Synthetic Owner", "Owner", "SO", true)]);
        public Task<BackendAuthenticatedSession> AuthenticateAsync(string accountId, string pin, CancellationToken cancellationToken = default)
        {
            AuthenticationCalls++;
            return Result.Task;
        }
        public Task SignOutAsync(ISessionContext session, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
