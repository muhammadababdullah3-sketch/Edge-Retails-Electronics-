using System;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Identity;
using Xunit;

namespace EdgeRetails.UnitTests;

/// <summary>
/// Authorization regression tests for the product-unit catalog mutation handlers.
/// </summary>
/// <remarks>
/// <para>
/// Both <see cref="ConfigureProductUnitsHandler"/> and <see cref="SetProductUnitBarcodeHandler"/>
/// previously guarded the permission check with
/// <c>if (_authorizer is not null &amp;&amp; ActorId != Guid.Empty)</c>. That is fail-open: a handler
/// constructed without an authorizer, or invoked with a default actor, silently skipped
/// <c>InventoryManage</c> and wrote the mutation anyway. Every other catalog mutation handler takes
/// a non-nullable authorizer and checks unconditionally, so these two were the deviation.
/// </para>
/// <para>
/// The tests below pin the corrected behavior: an unconfigured authorizer must deny rather than
/// proceed, and an absent actor must not short-circuit the check. They assert on the error code and
/// on the resulting repository state so a future refactor cannot quietly restore the skip.
/// </para>
/// </remarks>
public sealed class ProductUnitHandlerAuthorizationTests
{
    private const string Unavailable = "catalog.authorization_unavailable";

    [Fact]
    public async Task ConfigureUnits_WithoutAuthorizer_DeniesInsteadOfProceeding()
    {
        var catalog = new FakeCatalogRepository();
        var handler = new ConfigureProductUnitsHandler(
            catalog, new PassThroughTransactions(), new NoopUnitOfWork(), authorizer: null);

        var result = await handler.HandleAsync(
            new ConfigureProductUnitsCommand(Guid.NewGuid(), [], ActorId: Guid.NewGuid()),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(Unavailable, result.Error!.Code);

        // Failing closed means nothing was written, not merely that an error surfaced.
        Assert.Empty(catalog.ProductUnits);
    }

    [Fact]
    public async Task ConfigureUnits_WithEmptyActor_StillAttemptsAuthorizationAndDenies()
    {
        var catalog = new FakeCatalogRepository();
        var authorizer = new DenyingAuthorizer();
        var handler = new ConfigureProductUnitsHandler(
            catalog, new PassThroughTransactions(), new NoopUnitOfWork(), authorizer);

        var result = await handler.HandleAsync(
            new ConfigureProductUnitsCommand(Guid.NewGuid(), [], ActorId: Guid.Empty),
            CancellationToken.None);

        // An absent actor must not short-circuit the check: authorization is consulted and denies.
        Assert.Equal(1, authorizer.Calls);
        Assert.False(result.IsSuccess);
        Assert.Empty(catalog.ProductUnits);
    }

    [Fact]
    public async Task ConfigureUnits_WhenAuthorizerAllows_ReachesProductLookup()
    {
        var catalog = new FakeCatalogRepository();
        var authorizer = new AllowingAuthorizer();
        var handler = new ConfigureProductUnitsHandler(
            catalog, new PassThroughTransactions(), new NoopUnitOfWork(), authorizer);

        var result = await handler.HandleAsync(
            new ConfigureProductUnitsCommand(Guid.NewGuid(), [], ActorId: Guid.NewGuid()),
            CancellationToken.None);

        // Authorization passed, so the failure must now come from the absent product. This proves
        // the guard no longer masks the rest of the pipeline.
        Assert.Equal(1, authorizer.Calls);
        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.product_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task SetBarcode_WithoutAuthorizer_DeniesInsteadOfProceeding()
    {
        var catalog = new FakeCatalogRepository();
        var handler = new SetProductUnitBarcodeHandler(
            catalog, new PassThroughTransactions(), new NoopUnitOfWork(), authorizer: null);

        var result = await handler.HandleAsync(
            new SetProductUnitBarcodeCommand(Guid.NewGuid(), "1234567890", Guid.NewGuid()),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(Unavailable, result.Error!.Code);
        Assert.Empty(catalog.ProductUnits);
    }

    [Fact]
    public async Task SetBarcode_WithEmptyActor_StillAttemptsAuthorizationAndDenies()
    {
        var catalog = new FakeCatalogRepository();
        var authorizer = new DenyingAuthorizer();
        var handler = new SetProductUnitBarcodeHandler(
            catalog, new PassThroughTransactions(), new NoopUnitOfWork(), authorizer);

        var result = await handler.HandleAsync(
            new SetProductUnitBarcodeCommand(Guid.NewGuid(), "1234567890", Guid.Empty),
            CancellationToken.None);

        Assert.Equal(1, authorizer.Calls);
        Assert.False(result.IsSuccess);
        Assert.Empty(catalog.ProductUnits);
    }

    [Fact]
    public async Task SetBarcode_WhenAuthorizerAllows_ReachesUnitLookup()
    {
        var catalog = new FakeCatalogRepository();
        var authorizer = new AllowingAuthorizer();
        var handler = new SetProductUnitBarcodeHandler(
            catalog, new PassThroughTransactions(), new NoopUnitOfWork(), authorizer);

        var result = await handler.HandleAsync(
            new SetProductUnitBarcodeCommand(Guid.NewGuid(), "1234567890", Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(1, authorizer.Calls);
        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.product_unit_not_found", result.Error!.Code);
    }

    private sealed class AllowingAuthorizer : IApplicationPermissionAuthorizer
    {
        public int Calls { get; private set; }

        public Task<Result> AuthorizeAsync(Guid actorId, string permissionKey, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class DenyingAuthorizer : IApplicationPermissionAuthorizer
    {
        public int Calls { get; private set; }

        public Task<Result> AuthorizeAsync(Guid actorId, string permissionKey, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result.Failure(
                "identity.permission_denied",
                "The actor is not permitted to manage inventory."));
        }
    }

    private sealed class PassThroughTransactions : ITransactionRunner
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
            => operation(cancellationToken);
    }

    private sealed class NoopUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
