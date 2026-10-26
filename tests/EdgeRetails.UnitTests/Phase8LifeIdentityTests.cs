using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Infrastructure.Services;

namespace EdgeRetails.UnitTests;

public sealed class Phase8LifeIdentityTests
{
    private readonly Phase2TestDoubles _fakes = new();

    [Fact]
    public void CashSession_VoidByOwner_RequiresClosedStatus()
    {
        var session = new CashSession { Status = CashSessionStatus.Open };
        var ex = Assert.Throws<Domain.Common.BusinessRuleException>(() =>
            session.VoidByOwner(Guid.CreateVersion7(), DateTimeOffset.UtcNow, "test"));
        Assert.Equal("cash.session_not_closed", ex.Code);
    }

    [Fact]
    public async Task CashMovement_IncrementsSessionVersion()
    {
        var actorId = Guid.CreateVersion7();
        var openHandler = new OpenCashSessionHandler(
            _fakes.Cash,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.ExceptionMapper,
            _fakes.Authorization);

        var open = await openHandler.HandleAsync(
            new OpenCashSessionCommand(100m, actorId, null),
            CancellationToken.None);
        Assert.True(open.IsSuccess);
        var session = _fakes.Cash.Sessions[open.Value];
        Assert.Equal(0, session.Version);

        var movement = await _fakes.CashMovements.RecordAsync(
            new RecordCashMovementRequest(
                CashMovementType.ManualCashIn,
                CashMovementDirection.In,
                50m,
                actorId,
                null,
                null,
                null,
                null),
            CancellationToken.None);
        Assert.True(movement.IsSuccess);
        Assert.Equal(1, session.Version);
    }

    [Fact]
    public async Task VoidCashSessionByOwner_OnClosedSession_Succeeds()
    {
        var actorId = Guid.CreateVersion7();
        _fakes.Authorization.IsAuthorized = true;
        var openHandler = new OpenCashSessionHandler(
            _fakes.Cash,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.ExceptionMapper,
            _fakes.Authorization);
        var closeHandler = new CloseCashSessionHandler(
            _fakes.Cash,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.ExceptionMapper,
            _fakes.Authorization);

        var open = await openHandler.HandleAsync(new OpenCashSessionCommand(100m, actorId, null), CancellationToken.None);
        var sessionId = open.Value;
        var close = await closeHandler.HandleAsync(
            new CloseCashSessionCommand(sessionId, 100m, actorId, null),
            CancellationToken.None);
        Assert.True(close.IsSuccess);

        var voidHandler = new VoidCashSessionByOwnerHandler(
            _fakes.Cash,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.ExceptionMapper,
            _fakes.Authorization);
        var voidResult = await voidHandler.HandleAsync(
            new VoidCashSessionByOwnerCommand(sessionId, actorId, "Correction"),
            CancellationToken.None);

        Assert.True(voidResult.IsSuccess);
        Assert.Equal(CashSessionStatus.VoidedByOwner, _fakes.Cash.Sessions[sessionId].Status);
    }

    [Fact]
    public void ShopSessionPolicy_IdleTimeout_IsEightHours()
    {
        Assert.Equal(TimeSpan.FromHours(8), ShopSessionPolicy.IdleTimeout);
    }

    [Fact]
    public void ShopSessionPolicy_ScreenIdleTimeout_IsFifteenMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), ShopSessionPolicy.ScreenIdleTimeout);
    }

    [Fact]
    public void UserSession_IsScreenLocked_WhenTimestampSet()
    {
        var session = new Domain.Identity.UserSession { ScreenLockedAt = DateTimeOffset.UtcNow };
        Assert.True(session.IsScreenLocked);
    }

    [Fact]
    public void EfExceptionToErrorMapper_IgnoresUnrelatedExceptions()
    {
        var mapper = new EfExceptionToErrorMapper();
        Assert.False(mapper.TryMap(new InvalidOperationException("x"), out _));
    }

}
