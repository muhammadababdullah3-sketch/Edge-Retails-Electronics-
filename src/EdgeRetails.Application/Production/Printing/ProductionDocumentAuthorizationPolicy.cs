namespace EdgeRetails.Application.Production.Printing;

public sealed class ProductionDocumentAuthorizationPolicy : IProductionDocumentAuthorizationPolicy
{
    private readonly IProductionAuthorization _authorization;

    public ProductionDocumentAuthorizationPolicy(IProductionAuthorization authorization)
    {
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
    }

    public async Task EnsureCanPrintAsync(
        ProductionDocumentKind kind,
        Guid businessDocumentId,
        bool isReprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (businessDocumentId == Guid.Empty)
        {
            throw new ArgumentException("Business document ID cannot be empty.", nameof(businessDocumentId));
        }

        // Basic permission check - operator or manager can print; reprints require printing.reprint permission
        await _authorization.EnsureAuthenticatedAsync(cancellationToken);
        if (isReprint)
        {
            await _authorization.EnsurePermissionAsync(ProductionPermissionNames.PrintingReprint, cancellationToken);
        }
    }
}
