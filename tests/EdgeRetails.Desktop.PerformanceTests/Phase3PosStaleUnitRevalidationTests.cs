using System.Reflection;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

public sealed class Phase3PosStaleUnitRevalidationTests
{
    [Fact]
    public async Task CheckoutAwaitsLiveUnitRevalidationAndDoesNotSubmitStaleSelection()
    {
        var transactionService = DispatchProxy.Create<ITransactionService, RejectSaleProxy>();
        var proxy = (RejectSaleProxy)(object)transactionService;
        var validationCount = 0;
        var checkout = new CompleteSaleViewModel(
            totalToPay: 125m,
            transactionService: transactionService,
            preCommitValidation: () => null,
            preCommitAsyncValidation: async () =>
            {
                await Task.Yield();
                validationCount++;
                return "Physical unit SN-STALE changed. Remove it and select an available unit again.";
            });

        await checkout.CompleteSaleAsync();

        Assert.Equal(1, validationCount);
        Assert.Equal(0, proxy.InvocationCount);
        Assert.Equal(
            "Physical unit SN-STALE changed. Remove it and select an available unit again.",
            checkout.ValidationMessage);
        Assert.Null(checkout.DialogResult);
    }

    private class RejectSaleProxy : DispatchProxy
    {
        public int InvocationCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            InvocationCount++;
            throw new InvalidOperationException(
                $"Stale exact-unit checkout must not call {targetMethod?.Name}.");
        }
    }
}
