using System.Globalization;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Thaka;

namespace EdgeRetails.UnitTests;

public sealed class Phase3OperationIdentityTests
{
    [Fact]
    public async Task CreateStocktake_ReusingOperationIdWithChangedPayload_ReturnsConflict()
    {
        var fakes = new Phase2TestDoubles();
        var handler = new CreateStocktakeHandler(
            fakes.Inventory,
            fakes.Transactions,
            fakes.UnitOfWork,
            fakes.Clock,
            outcomeLedger: fakes.OutcomeLedger,
            operationLock: fakes.OperationLock);
        var operationId = Guid.CreateVersion7();

        var first = await handler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, Guid.NewGuid(), "Morning", operationId),
            CancellationToken.None);
        var changed = await handler.HandleAsync(
            new CreateStocktakeCommand(StocktakeScope.FullShop, null, Guid.NewGuid(), "Evening", operationId),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.False(changed.IsSuccess);
        Assert.Equal("inventory.stocktake_operation_conflict", changed.Error?.Code);
    }

    [Fact]
    public async Task ThakaPayment_ReusingOperationIdWithChangedPayload_ReturnsConflict()
    {
        var fakes = new Phase2TestDoubles();
        var operationId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var actorId = Guid.CreateVersion7();
        var priorFingerprint = OperationPayloadFingerprint.ComputeSha256(
            "ThakaPayment", projectId.ToString("D"), "100.00", ThakaPaymentMethod.Cash.ToString(), string.Empty, string.Empty);
        var payment = new ThakaPayment
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            ReceiptNumber = "THK-PAY-TEST",
            ClientOperationId = operationId,
            Amount = 100m,
            PaymentMethod = ThakaPaymentMethod.Cash,
            RecordedBy = actorId
        };
        fakes.Thaka.AddPayment(payment);
        await fakes.OutcomeLedger.RecordSuccessAsync(
            operationId,
            "ThakaPayment",
            payment.Id,
            payment.ReceiptNumber,
            actorId: actorId,
            payloadFingerprint: priorFingerprint,
            cancellationToken: CancellationToken.None);

        var handler = new RecordThakaPaymentHandler(
            fakes.Thaka,
            new EdgeRetails.Application.Features.Finance.CashMovementService(fakes.Cash, fakes.Clock),
            fakes.OperationLock,
            fakes.ResourceLock,
            fakes.Numbers,
            fakes.Audit,
            fakes.Clock,
            fakes.Transactions,
            fakes.Authorization,
            fakes.UnitOfWork,
            fakes.OutcomeLedger);
        var result = await handler.HandleAsync(
            new RecordThakaPaymentCommand(
                operationId,
                projectId,
                125m,
                ThakaPaymentMethod.Cash,
                null,
                null,
                actorId),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("thaka.operation_id_conflict", result.Error?.Code);
        Assert.Single(fakes.Thaka.Payments);
    }
}
