using System.Reflection;
using System.Net;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase234CustomerSaveMessagesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailuresDistinguishRejectedFromUnknown(bool rejected)
    {
        var messages = new List<string>();
        var backend = CreateProxy<IBackendBusinessOperationsService>((_, _) => Task.FromException(
            new DesktopApiException("test.failure", "Test save failure", rejected ? HttpStatusCode.Forbidden : null)));
        var vm = new CustomerEditViewModel(CreateProxy<IToastService>((method, args) =>
        {
            if (method.Name == "Show")
            {
                messages.Add((string)args![0]!);
            }
            return null;
        }), () => { }, backendService: backend) { Name = "Test" };
        await Save(vm);
        Assert.Contains(rejected ? "save was rejected" : "save outcome is unconfirmed", Assert.Single(messages));
        Assert.DoesNotContain("details could not be loaded", messages[0]);
    }

    [Fact]
    public async Task ConfirmedSaveCallbackFailureDoesNotInviteAnotherSave()
    {
        var calls = 0;
        var messages = new List<string>();
        var backend = CreateProxy<IBackendBusinessOperationsService>((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });
        var toast = CreateProxy<IToastService>((method, args) =>
        {
            if (method.Name == "Show")
            {
                messages.Add((string)args![0]!);
            }
            return null;
        });
        var vm = new CustomerEditViewModel(toast, () => { }, saved: () => throw new InvalidOperationException("Refresh failed"),
            backendService: backend) { Name = "Test" };
        await Save(vm);
        Assert.Contains(messages, message => message.Contains("Customer saved."));
        await Save(vm);
        Assert.Equal(1, calls);
    }

    private static Task Save(CustomerEditViewModel vm) => (Task)typeof(CustomerEditViewModel)
        .GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)result).Handler = handler;
        return result;
    }
    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
