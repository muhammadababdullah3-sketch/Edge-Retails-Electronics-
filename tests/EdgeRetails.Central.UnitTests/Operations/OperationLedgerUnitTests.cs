using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.UnitTests.Operations;

public class OperationLedgerUnitTests : IDisposable
{
    private readonly CentralDbContext _dbContext;
    private readonly CanonicalJsonSerializer _serializer = new();
    private readonly OperationLedgerService _ledger;

    public OperationLedgerUnitTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _dbContext = new CentralDbContext(options);
        _ledger = new OperationLedgerService(_dbContext, _serializer);
    }

    [Fact]
    public async Task ExecuteOrReplay_ExecutesAndRecordsOutcome_WhenFirstEncountered()
    {
        var opId = Guid.NewGuid();
        var command = new AdminActionCommandDto
        {
            AdminUserId = Guid.NewGuid(),
            Action = "ASSIGN_ROLE",
            RoleId = Guid.NewGuid()
        };

        var (_, sha256) = _serializer.SerializeAndHash(command);
        var meta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.action",
            PayloadSha256 = sha256,
            Reason = "Initial assignment",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        var executionCount = 0;
        var outcome = await _ledger.ExecuteOrReplayAsync(
            actorScope: "admin:session1",
            shopId: null,
            meta: meta,
            payload: command,
            executeAsync: _ =>
            {
                executionCount++;
                return Task.FromResult<(bool, string?, int, int)>((true, "ref1", 2, 1));
            });

        Assert.NotNull(outcome);
        Assert.Equal("SUCCEEDED", outcome.State);
        Assert.Equal(1, executionCount);
        Assert.Equal("ref1", outcome.ResultReference);
        Assert.Equal(2, outcome.ResourceVersion);
    }

    [Fact]
    public async Task ExecuteOrReplay_ReplaysRecordedOutcome_WithoutReexecutingBusinessLogic()
    {
        var opId = Guid.NewGuid();
        var command = new AdminActionCommandDto
        {
            AdminUserId = Guid.NewGuid(),
            Action = "ASSIGN_ROLE",
            RoleId = Guid.NewGuid()
        };

        var (_, sha256) = _serializer.SerializeAndHash(command);
        var meta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.action",
            PayloadSha256 = sha256,
            Reason = "Initial assignment",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        var executionCount = 0;
        var firstOutcome = await _ledger.ExecuteOrReplayAsync(
            actorScope: "admin:session1",
            shopId: null,
            meta: meta,
            payload: command,
            executeAsync: _ =>
            {
                executionCount++;
                return Task.FromResult<(bool, string?, int, int)>((true, "ref1", 2, 1));
            });

        // Replay with identical scope, opId, opType, payload
        var replayOutcome = await _ledger.ExecuteOrReplayAsync(
            actorScope: "admin:session1",
            shopId: null,
            meta: meta,
            payload: command,
            executeAsync: _ =>
            {
                executionCount++;
                return Task.FromResult<(bool, string?, int, int)>((true, "ref_WRONG", 99, 99));
            });

        Assert.Equal(1, executionCount); // Business logic was NOT re-executed
        Assert.Equal(firstOutcome.OutcomeId, replayOutcome.OutcomeId);
        Assert.Equal(firstOutcome.ResultReference, replayOutcome.ResultReference);
        Assert.Equal(firstOutcome.ResourceVersion, replayOutcome.ResourceVersion);
    }

    [Fact]
    public async Task ExecuteOrReplay_ThrowsConflict_WhenPayloadDiffersForSameOperationId()
    {
        var opId = Guid.NewGuid();
        var command1 = new AdminActionCommandDto { AdminUserId = Guid.NewGuid(), Action = "DISABLE" };
        var (_, sha256_1) = _serializer.SerializeAndHash(command1);

        var meta1 = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.action",
            PayloadSha256 = sha256_1,
            Reason = "Disable user",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        await _ledger.ExecuteOrReplayAsync(
            actorScope: "admin:session1",
            shopId: null,
            meta: meta1,
            payload: command1,
            executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "ref1", 2, 1)));

        // Differing payload with same ClientOperationId
        var command2 = new AdminActionCommandDto { AdminUserId = Guid.NewGuid(), Action = "REVOKE_ALL" };
        var (_, sha256_2) = _serializer.SerializeAndHash(command2);

        var meta2 = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.action",
            PayloadSha256 = sha256_2,
            Reason = "Revoke all",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _ledger.ExecuteOrReplayAsync(
                actorScope: "admin:session1",
                shopId: null,
                meta: meta2,
                payload: command2,
                executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "ref2", 3, 1))));

        Assert.Equal("OPERATION_ID_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task ExecuteOrReplay_ThrowsPayloadHashMismatch_WhenClientTamperedHash()
    {
        var opId = Guid.NewGuid();
        var command = new AdminActionCommandDto { AdminUserId = Guid.NewGuid(), Action = "DISABLE" };

        var meta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.action",
            PayloadSha256 = "0000000000000000000000000000000000000000000000000000000000000000",
            Reason = "Disable user",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _ledger.ExecuteOrReplayAsync(
                actorScope: "admin:session1",
                shopId: null,
                meta: meta,
                payload: command,
                executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "ref1", 2, 1))));

        Assert.Equal("PAYLOAD_HASH_MISMATCH", ex.Code);
    }

    [Fact]
    public async Task ExecuteOrReplay_ThrowsConflict_WhenTargetResourceDiffersForSameOperationId()
    {
        var opId = Guid.NewGuid();
        var command = new AdminActionCommandDto { AdminUserId = Guid.NewGuid(), Action = "DISABLE" };
        var (_, sha256) = _serializer.SerializeAndHash(command);

        var meta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.user.action",
            PayloadSha256 = sha256,
            Reason = "Disable user",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        await _ledger.ExecuteOrReplayAsync(
            actorScope: "admin:session1",
            shopId: null,
            meta: meta,
            payload: command,
            executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "admin_users/targetA", 2, 1)),
            targetResource: "admin_users/targetA");

        // Attempt replay targeting a different resource targetB
        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _ledger.ExecuteOrReplayAsync(
                actorScope: "admin:session1",
                shopId: null,
                meta: meta,
                payload: command,
                executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "admin_users/targetB", 2, 1)),
                targetResource: "admin_users/targetB"));

        Assert.Equal("OPERATION_ID_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task ExecuteOrReplay_ThrowsConflict_WhenShopIdDiffersForSameOperationId()
    {
        var opId = Guid.NewGuid();
        var command = new AdminActionCommandDto { AdminUserId = Guid.NewGuid(), Action = "DISABLE" };
        var (_, sha256) = _serializer.SerializeAndHash(command);
        var shopA = Guid.NewGuid();
        var shopB = Guid.NewGuid();

        var meta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.user.action",
            PayloadSha256 = sha256,
            Reason = "Disable user",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        await _ledger.ExecuteOrReplayAsync(
            actorScope: "admin:session1",
            shopId: shopA,
            meta: meta,
            payload: command,
            executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "ref", 2, 1)));

        // Attempt replay with shopB
        var ex = await Assert.ThrowsAsync<CentralDomainException>(() =>
            _ledger.ExecuteOrReplayAsync(
                actorScope: "admin:session1",
                shopId: shopB,
                meta: meta,
                payload: command,
                executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "ref", 2, 1))));

        Assert.Equal("OPERATION_ID_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task GetOperationStatus_EnforcesScopeIsolation()
    {
        var opId = Guid.NewGuid();
        var command = new AdminActionCommandDto { AdminUserId = Guid.NewGuid(), Action = "DISABLE" };
        var (_, sha256) = _serializer.SerializeAndHash(command);

        var meta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.action",
            PayloadSha256 = sha256,
            Reason = "Disable user",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        await _ledger.ExecuteOrReplayAsync(
            actorScope: "admin:scopeA",
            shopId: null,
            meta: meta,
            payload: command,
            executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "ref1", 2, 1)));

        // Scope A can access
        var statusA = await _ledger.GetOperationStatusAsync(opId, "admin:scopeA");
        Assert.NotNull(statusA);

        // Scope B cannot access
        var statusB = await _ledger.GetOperationStatusAsync(opId, "admin:scopeB");
        Assert.Null(statusB);
    }

    [Fact]
    public async Task GetOutcomeAsync_RequiresExactActorScope()
    {
        var opId = Guid.NewGuid();
        var command = new AdminActionCommandDto { AdminUserId = Guid.NewGuid(), Action = "ENABLE" };
        var (_, sha256) = _serializer.SerializeAndHash(command);

        var meta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "admin.action",
            PayloadSha256 = sha256,
            Reason = "Enable user",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        await _ledger.ExecuteOrReplayAsync(
            actorScope: "admin:session123",
            shopId: null,
            meta: meta,
            payload: command,
            executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "users/admin1", 3, 2)));

        var outcome = await _ledger.GetOutcomeAsync(opId, "admin:session123");
        Assert.NotNull(outcome);
        Assert.Equal(opId, outcome.ClientOperationId);
        Assert.Equal("SUCCEEDED", outcome.State);
        Assert.Equal("users/admin1", outcome.ResultReference);
        Assert.Equal(3, outcome.ResourceVersion);
        Assert.Equal(2, outcome.AuthorityRevision);
        Assert.Null(await _ledger.GetOutcomeAsync(opId, "admin:session999"));

        // Non-admin scope cannot access
        var nonAdminOutcome = await _ledger.GetOutcomeAsync(opId, "installation:inst1");
        Assert.Null(nonAdminOutcome);
    }

    [Fact]
    public async Task GetLicensingOutcomeAsync_EnforcesShopScope()
    {
        var opId = Guid.NewGuid();
        var shopId = Guid.NewGuid();
        var command = new AdminActionCommandDto { AdminUserId = Guid.NewGuid(), Action = "SYNC" };
        var (_, sha256) = _serializer.SerializeAndHash(command);

        var meta = new CommandMetaDto
        {
            ClientOperationId = opId,
            OperationType = "licensing.sync",
            PayloadSha256 = sha256,
            Reason = "Sync license",
            ExpectedVersion = 1,
            PolicyVersion = 1
        };

        await _ledger.ExecuteOrReplayAsync(
            actorScope: "installation:pos1",
            shopId: shopId,
            meta: meta,
            payload: command,
            executeAsync: _ => Task.FromResult<(bool, string?, int, int)>((true, "licenses/lic1", 1, 1)));

        // Same shop can access
        var outcome = await _ledger.GetLicensingOutcomeAsync(opId, shopId.ToString());
        Assert.NotNull(outcome);
        Assert.Equal(opId, outcome.ClientOperationId);
        Assert.Equal("SUCCEEDED", outcome.State);

        // Different shop cannot access
        var differentShopOutcome = await _ledger.GetLicensingOutcomeAsync(opId, Guid.NewGuid().ToString());
        Assert.Null(differentShopOutcome);
        Assert.Null(await _ledger.GetLicensingOutcomeAsync(opId, null));
        Assert.Null(await _ledger.GetLicensingOutcomeAsync(opId, "not-a-shop"));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }
}
