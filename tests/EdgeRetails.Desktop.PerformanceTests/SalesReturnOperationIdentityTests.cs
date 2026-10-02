using System.IO;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.UnitTests;

public sealed class SalesReturnOperationIdentityTests
{
    [Fact]
    public async Task AmbiguousReturn_RestartReusesIdentity_AndRejectsChangedPayload()
    {
        var directory = Directory.CreateTempSubdirectory("edge-retails-sale-return-intent-");
        try
        {
            var intentPath = Path.Combine(directory.FullName, "operation-intents.json");
            var transactionService = new RetryCaptureTransactionService();
            var firstViewModel = CreateViewModel(transactionService, new FileClientOperationIntentStore(intentPath));

            await firstViewModel.ProcessReturnAsync();
            Assert.True(firstViewModel.HasValidationMessage);
            Assert.Single(transactionService.Requests);
            var originalOperationId = transactionService.Requests[0].ClientOperationId;
            Assert.NotEqual(Guid.Empty, originalOperationId);

            var changedPayloadViewModel = CreateViewModel(
                transactionService,
                new FileClientOperationIntentStore(intentPath));
            changedPayloadViewModel.RefundMethod = "Store Credit";
            await changedPayloadViewModel.ProcessReturnAsync();

            Assert.True(changedPayloadViewModel.HasValidationMessage);
            Assert.Single(transactionService.Requests);

            var restartedViewModel = CreateViewModel(
                transactionService,
                new FileClientOperationIntentStore(intentPath));
            await restartedViewModel.ProcessReturnAsync();

            Assert.False(restartedViewModel.HasValidationMessage);
            Assert.Equal(2, transactionService.Requests.Count);
            Assert.Equal(originalOperationId, transactionService.Requests[1].ClientOperationId);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static SalesReturnViewModel CreateViewModel(
        ITransactionService transactionService,
        IClientOperationIntentStore operationIntents)
    {
        var sale = new SaleTransactionItemViewModel(
            1,
            DateTime.UtcNow,
            "Customer",
            string.Empty,
            "Cashier",
            "Cash",
            [new SaleLineItemViewModel(1, Guid.Parse("9bb79228-4a1d-49c7-aa3f-c90ca3da8641").ToString("D"), "Cable", "CBL-1", "", 100m, 1m)],
            invoiceDisplayOverride: "INV-1");
        var viewModel = new SalesReturnViewModel(
            sale,
            transactionService: transactionService,
            operationIntents: operationIntents);
        viewModel.ReturnItems[0].ReturnQuantity = 1m;
        return viewModel;
    }

    private sealed class RetryCaptureTransactionService : ITransactionService
    {
        public List<RecordSaleReturnRequest> Requests { get; } = [];
        public event EventHandler<SaleTransactionRecord>? TransactionRecorded
        {
            add { }
            remove { }
        }

        public event EventHandler<SaleReturnRecord>? ReturnRecorded
        {
            add { }
            remove { }
        }
        public decimal TodaySales => 0m;
        public int TodayCount => 0;
        public decimal TotalReturns => 0m;
        public decimal NetSales => 0m;

        public Task<SaleReturnRecord> RecordReturnAsync(
            RecordSaleReturnRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                throw new InvalidOperationException("Simulated lost response.");
            }

            return Task.FromResult(new SaleReturnRecord
            {
                ReturnNumber = "RET-1",
                InvoiceNumber = request.InvoiceNumber,
                TotalRefundAmount = request.TotalRefundAmount,
                Items = request.Items
            });
        }

        public Task<SaleTransactionRecord> RecordTransactionAsync(RecordSaleRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public SaleTransactionRecord RecordTransaction(RecordSaleRequest request) => throw new NotSupportedException();
        public Task<IReadOnlyList<SaleTransactionRecord>> GetAllTransactionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SaleTransactionRecord>>([]);
        public IReadOnlyList<SaleTransactionRecord> GetAllTransactions() => [];
        public Task<SaleTransactionRecord?> GetByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<SaleTransactionRecord?>(null);
        public SaleTransactionRecord? GetByInvoiceNumber(string invoiceNumber) => null;
        public SaleReturnRecord RecordReturn(RecordSaleReturnRequest request) => throw new NotSupportedException();
        public IReadOnlyList<SaleReturnRecord> GetReturnsForInvoice(string invoiceNumber) => [];
        public IReadOnlyList<SaleReturnRecord> GetAllReturns() => [];
    }
}
