using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Application.Services;

public class CommercialService : ICommercialService
{
    private readonly ICentralDbContext _dbContext;
    private readonly ICentralAuditService _auditService;
    private readonly IOperationLedgerService? _operationLedger;
    private readonly ICanonicalJsonSerializer _serializer;

    public CommercialService(
        ICentralDbContext dbContext,
        ICentralAuditService auditService,
        IOperationLedgerService? operationLedger = null,
        ICanonicalJsonSerializer? serializer = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
        _operationLedger = operationLedger;
        _serializer = serializer ?? new CanonicalJsonSerializer();
    }

    public async Task<Customer> CreateCustomerAsync(
        CreateCustomerCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_operationLedger is not null && command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"customer/{command.LegalName.Trim().ToLowerInvariant()}";

            var (_, customer) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var cust = Customer.Create(
                        cmd.LegalName,
                        cmd.ContactEmail,
                        cmd.ContactPhone,
                        now);

                    _dbContext.Customers.Add(cust);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "commercial.customer_created",
                        nameof(Customer),
                        cust.Id.ToString(),
                        _serializer.SerializeCanonical(new { cust.Id, cust.LegalName, cust.ContactEmail, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_CUSTOMER_CREATED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Customer created",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(Customer),
                        cust.Id.ToString(),
                        $"{{\"legalName\":\"{cust.LegalName}\",\"email\":\"{cust.ContactEmail}\"}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (cust, $"customers/{cust.Id}", 1, 1);
                },
                recoverResultAsync: async record =>
                {
                    var custId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.Customers.FindAsync(new object[] { custId }, cancellationToken)
                        ?? throw new CentralDomainException("CUSTOMER_NOT_FOUND", "customer.not_found", $"Customer {custId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return customer;
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, command.LegalName, cancellationToken);

        var nowDirect = DateTimeOffset.UtcNow;
        var customerDirect = Customer.Create(
            command.LegalName,
            command.ContactEmail,
            command.ContactPhone,
            nowDirect);

        _dbContext.Customers.Add(customerDirect);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_CUSTOMER_CREATED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Customer created",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(Customer),
            customerDirect.Id.ToString(),
            $"{{\"legalName\":\"{customerDirect.LegalName}\",\"email\":\"{customerDirect.ContactEmail}\"}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return customerDirect;
    }

    public async Task<Shop> RegisterShopAsync(
        RegisterShopCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_operationLedger is not null && command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"shop/{command.ShopCode.Trim().ToUpperInvariant()}";

            var (_, shop) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var customer = await _dbContext.Customers.FindAsync(new object[] { cmd.CustomerId }, cancellationToken)
                        ?? throw new CentralDomainException("CUSTOMER_NOT_FOUND", "shop.customer_not_found", $"Customer {cmd.CustomerId} does not exist.");

                    var existingCode = await _dbContext.Shops
                        .AnyAsync(s => s.ShopCode == cmd.ShopCode.Trim().ToUpperInvariant(), cancellationToken);

                    if (existingCode)
                    {
                        throw new CentralDomainException("SHOP_CODE_DUPLICATE", "shop.code_duplicate", $"Shop code '{cmd.ShopCode}' already exists.");
                    }

                    var now = DateTimeOffset.UtcNow;
                    var createdShop = Shop.Create(
                        cmd.CustomerId,
                        cmd.ShopCode,
                        cmd.ShopName,
                        now);

                    _dbContext.Shops.Add(createdShop);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "commercial.shop_registered",
                        nameof(Shop),
                        createdShop.Id.ToString(),
                        _serializer.SerializeCanonical(new { createdShop.Id, createdShop.ShopCode, createdShop.PrimaryCapacity, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_SHOP_REGISTERED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Shop registered",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(Shop),
                        createdShop.Id.ToString(),
                        $"{{\"shopCode\":\"{createdShop.ShopCode}\",\"primaryCapacity\":{createdShop.PrimaryCapacity}}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (createdShop, $"shops/{createdShop.Id}", 1, 1);
                },
                recoverResultAsync: async record =>
                {
                    var shopId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.Shops.FindAsync(new object[] { shopId }, cancellationToken)
                        ?? throw new CentralDomainException("SHOP_NOT_FOUND", "shop.not_found", $"Shop {shopId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return shop;
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, command.ShopCode, cancellationToken);

        var customerDirect = await _dbContext.Customers.FindAsync(new object[] { command.CustomerId }, cancellationToken)
            ?? throw new CentralDomainException("CUSTOMER_NOT_FOUND", "shop.customer_not_found", $"Customer {command.CustomerId} does not exist.");

        var existingCodeDirect = await _dbContext.Shops
            .AnyAsync(s => s.ShopCode == command.ShopCode.Trim().ToUpperInvariant(), cancellationToken);

        if (existingCodeDirect)
        {
            throw new CentralDomainException("SHOP_CODE_DUPLICATE", "shop.code_duplicate", $"Shop code '{command.ShopCode}' already exists.");
        }

        var nowDirect = DateTimeOffset.UtcNow;
        var shopDirect = Shop.Create(
            command.CustomerId,
            command.ShopCode,
            command.ShopName,
            nowDirect);

        _dbContext.Shops.Add(shopDirect);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_SHOP_REGISTERED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Shop registered",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(Shop),
            shopDirect.Id.ToString(),
            $"{{\"shopCode\":\"{shopDirect.ShopCode}\",\"primaryCapacity\":{shopDirect.PrimaryCapacity}}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return shopDirect;
    }

    public async Task<PlanCatalogItem> CreateCatalogItemAsync(
        string code,
        string name,
        string purchaseTarget,
        string termKind,
        string? billingPeriodId,
        decimal unitPrice,
        string currency,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.PlanCatalogItems
            .AnyAsync(i => i.Code == code.Trim().ToUpperInvariant(), cancellationToken);

        if (existing)
        {
            throw new CentralDomainException("ITEM_CODE_DUPLICATE", "catalog.code_duplicate", $"Item code '{code}' already exists.");
        }

        var item = PlanCatalogItem.Create(
            code,
            name,
            purchaseTarget,
            termKind,
            billingPeriodId,
            unitPrice,
            currency);

        _dbContext.PlanCatalogItems.Add(item);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_CATALOG_ITEM_CREATED",
            "ADMIN",
            adminId,
            null,
            "Catalog item created",
            null,
            null,
            nameof(PlanCatalogItem),
            item.Id.ToString(),
            $"{{\"code\":\"{item.Code}\",\"target\":\"{item.PurchaseTarget}\",\"term\":\"{item.TermKind}\",\"price\":{item.UnitPrice}}}",
            true,
            null,
            cancellationToken);

        return item;
    }

    public async Task<List<PlanCatalogItem>> GetCatalogItemsAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.PlanCatalogItems
            .Where(i => i.IsActive)
            .OrderBy(i => i.Code)
            .ToListAsync(cancellationToken);
    }

    public async Task<PricingSnapshot> CreatePriceSnapshotAsync(
        CreatePriceSnapshotCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_operationLedger is not null && command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"shop:{command.ShopId}";
            var targetResource = $"pricing_snapshot/{command.ShopId}/{command.CatalogItemId}";

            var (_, snapshot) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: command.ShopId,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var shop = await _dbContext.Shops.FindAsync(new object[] { cmd.ShopId }, cancellationToken)
                        ?? throw new CentralDomainException("SHOP_NOT_FOUND", "pricing.shop_not_found", $"Shop {cmd.ShopId} does not exist.");

                    var item = await _dbContext.PlanCatalogItems.FindAsync(new object[] { cmd.CatalogItemId }, cancellationToken)
                        ?? throw new CentralDomainException("ITEM_NOT_FOUND", "pricing.item_not_found", $"Catalog item {cmd.CatalogItemId} does not exist.");

                    var now = DateTimeOffset.UtcNow;
                    var snap = PricingSnapshot.Create(
                        shop.Id,
                        item,
                        cmd.AuthorityVersion,
                        now);

                    _dbContext.PricingSnapshots.Add(snap);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "commercial.price_snapshot_created",
                        nameof(PricingSnapshot),
                        snap.Id.ToString(),
                        _serializer.SerializeCanonical(new { snap.Id, snap.ShopId, snap.ServerGeneratedDigest, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    return (snap, $"pricing_snapshots/{snap.Id}", 1, snap.AuthorityVersion);
                },
                recoverResultAsync: async record =>
                {
                    var snapId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.PricingSnapshots.FindAsync(new object[] { snapId }, cancellationToken)
                        ?? throw new CentralDomainException("SNAPSHOT_NOT_FOUND", "pricing.snapshot_not_found", $"Snapshot {snapId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return snapshot;
        }

        var shopDirect = await _dbContext.Shops.FindAsync(new object[] { command.ShopId }, cancellationToken)
            ?? throw new CentralDomainException("SHOP_NOT_FOUND", "pricing.shop_not_found", $"Shop {command.ShopId} does not exist.");

        var itemDirect = await _dbContext.PlanCatalogItems.FindAsync(new object[] { command.CatalogItemId }, cancellationToken)
            ?? throw new CentralDomainException("ITEM_NOT_FOUND", "pricing.item_not_found", $"Catalog item {command.CatalogItemId} does not exist.");

        var nowDirect = DateTimeOffset.UtcNow;
        var snapshotDirect = PricingSnapshot.Create(
            shopDirect.Id,
            itemDirect,
            command.AuthorityVersion,
            nowDirect);

        _dbContext.PricingSnapshots.Add(snapshotDirect);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return snapshotDirect;
    }

    public async Task<Order> CreateOrderAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_operationLedger is not null && command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"customer:{command.CustomerId}";
            var targetResource = $"order/{command.SnapshotId}";

            var (_, order) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var snapshot = await _dbContext.PricingSnapshots.FindAsync(new object[] { cmd.SnapshotId }, cancellationToken)
                        ?? throw new CentralDomainException("SNAPSHOT_NOT_FOUND", "order.snapshot_not_found", $"Pricing snapshot {cmd.SnapshotId} does not exist.");

                    var customer = await _dbContext.Customers.FindAsync(new object[] { cmd.CustomerId }, cancellationToken)
                        ?? throw new CentralDomainException("CUSTOMER_NOT_FOUND", "order.customer_not_found", $"Customer {cmd.CustomerId} does not exist.");

                    var shop = await _dbContext.Shops.FindAsync(new object[] { snapshot.ShopId }, cancellationToken)
                        ?? throw new CentralDomainException("SHOP_NOT_FOUND", "order.shop_not_found", $"Shop {snapshot.ShopId} does not exist.");

                    if (shop.CustomerId != customer.Id)
                    {
                        throw new CentralDomainException("CUSTOMER_SHOP_MISMATCH", "order.customer_shop_mismatch", $"Shop {shop.Id} does not belong to customer {customer.Id}.");
                    }

                    var now = DateTimeOffset.UtcNow;
                    var createdOrder = Order.CreateFromSnapshot(snapshot, customer.Id, now);

                    _dbContext.Orders.Add(createdOrder);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "commercial.order_created",
                        nameof(Order),
                        createdOrder.Id.ToString(),
                        _serializer.SerializeCanonical(new { createdOrder.Id, createdOrder.ShopId, createdOrder.TotalAmount, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    return (createdOrder, $"orders/{createdOrder.Id}", 1, 1);
                },
                recoverResultAsync: async record =>
                {
                    var orderId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.Orders.FindAsync(new object[] { orderId }, cancellationToken)
                        ?? throw new CentralDomainException("ORDER_NOT_FOUND", "order.not_found", $"Order {orderId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return order;
        }

        var snapshotDirect = await _dbContext.PricingSnapshots.FindAsync(new object[] { command.SnapshotId }, cancellationToken)
            ?? throw new CentralDomainException("SNAPSHOT_NOT_FOUND", "order.snapshot_not_found", $"Pricing snapshot {command.SnapshotId} does not exist.");

        var customerDirect = await _dbContext.Customers.FindAsync(new object[] { command.CustomerId }, cancellationToken)
            ?? throw new CentralDomainException("CUSTOMER_NOT_FOUND", "order.customer_not_found", $"Customer {command.CustomerId} does not exist.");

        var shopDirect = await _dbContext.Shops.FindAsync(new object[] { snapshotDirect.ShopId }, cancellationToken)
            ?? throw new CentralDomainException("SHOP_NOT_FOUND", "order.shop_not_found", $"Shop {snapshotDirect.ShopId} does not exist.");

        if (shopDirect.CustomerId != customerDirect.Id)
        {
            throw new CentralDomainException("CUSTOMER_SHOP_MISMATCH", "order.customer_shop_mismatch", $"Shop {shopDirect.Id} does not belong to customer {customerDirect.Id}.");
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, shopDirect.Id.ToString(), cancellationToken);

        var nowDirect = DateTimeOffset.UtcNow;
        var orderDirect = Order.CreateFromSnapshot(snapshotDirect, customerDirect.Id, nowDirect);

        _dbContext.Orders.Add(orderDirect);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return orderDirect;
    }

    public async Task<UploadSession> InitUploadSessionAsync(
        InitUploadSessionCommand command,
        Guid uploaderId,
        CancellationToken cancellationToken = default)
    {
        var shop = await _dbContext.Shops.FindAsync(new object[] { command.ShopId }, cancellationToken)
            ?? throw new CentralDomainException("SHOP_NOT_FOUND", "upload.shop_not_found", $"Shop {command.ShopId} does not exist.");

        var order = await _dbContext.Orders.FindAsync(new object[] { command.TargetEntityId }, cancellationToken);
        if (order != null && order.ShopId != command.ShopId)
        {
            throw new CentralDomainException("ORDER_SHOP_MISMATCH", "upload.order_shop_mismatch", "Target order does not belong to specified shop.");
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddHours(24);
        var storagePath = $"uploads/{command.ShopId:N}/{Guid.NewGuid():N}.dat";

        var session = UploadSession.Create(
            command.Purpose,
            command.ShopId,
            uploaderId,
            command.TargetEntityId,
            command.ExpectedBytes,
            command.ExpectedSha256,
            storagePath,
            expiresAt,
            now);

        _dbContext.UploadSessions.Add(session);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return session;
    }

    public async Task AppendUploadChunkAsync(
        Guid sessionId,
        byte[] chunkData,
        CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.UploadSessions.FindAsync(new object[] { sessionId }, cancellationToken)
            ?? throw new CentralDomainException("UPLOAD_SESSION_NOT_FOUND", "upload.session_not_found", $"Session {sessionId} does not exist.");

        if (chunkData == null || chunkData.Length == 0 || chunkData.Length > 10 * 1024 * 1024)
        {
            throw new CentralDomainException("CHUNK_SIZE_EXCEEDED", "upload.chunk_size_exceeded", "Chunk size must be between 1 byte and 10MB.");
        }

        var fullPath = GetStorageFullPath(session.StoragePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var fs = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            await fs.WriteAsync(chunkData, 0, chunkData.Length, cancellationToken);
        }

        session.AppendChunk(chunkData.Length);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<PaymentEvidence> FinalizeUploadSessionAsync(
        Guid sessionId,
        FinalizeUploadSessionCommand command,
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.UploadSessions.FindAsync(new object[] { sessionId }, cancellationToken)
            ?? throw new CentralDomainException("UPLOAD_SESSION_NOT_FOUND", "upload.session_not_found", $"Session {sessionId} does not exist.");

        var fullPath = GetStorageFullPath(session.StoragePath);
        if (!File.Exists(fullPath))
        {
            session.Quarantine("Uploaded content file missing on disk");
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw new CentralDomainException("STORAGE_FILE_NOT_FOUND", "upload.file_not_found", "Uploaded content file not found on server.");
        }

        string serverSha256;
        using (var fs = File.OpenRead(fullPath))
        {
            byte[] hashBytes = await SHA256.HashDataAsync(fs, cancellationToken);
            serverSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        session.FinalizeUpload(serverSha256);

        if (!string.Equals(serverSha256, command.ComputedSha256, StringComparison.OrdinalIgnoreCase))
        {
            session.Quarantine("Computed SHA-256 does not match client value");
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw new CentralDomainException("SHA256_MISMATCH", "upload.sha256_mismatch", "Computed SHA-256 does not match declared digest.");
        }

        var order = await _dbContext.Orders.FindAsync(new object[] { orderId }, cancellationToken)
            ?? throw new CentralDomainException("ORDER_NOT_FOUND", "upload.order_not_found", $"Order {orderId} does not exist.");

        if (session.TargetEntityId != order.Id || session.ShopId != order.ShopId)
        {
            throw new CentralDomainException("ORDER_ATTACHMENT_MISMATCH", "upload.order_mismatch", "Session target or shop does not match the specified order.");
        }

        var now = DateTimeOffset.UtcNow;
        var evidence = PaymentEvidence.CreateFromSession(session, orderId, command.MimeType, now);

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, sessionId.ToString(), cancellationToken);

        _dbContext.PaymentEvidences.Add(evidence);
        _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
            evidence.Id, "payment.evidence_submitted", nameof(PaymentEvidence), evidence.Id.ToString("D"),
            _serializer.SerializeCanonical(new { evidence.Id, evidence.ShopId, evidence.OrderId }), now));
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return evidence;
    }

    public async Task<PaymentReviewRecord> ReviewPaymentAsync(
        ReviewPaymentCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_operationLedger is not null && command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"payment_review/{command.OrderId}";

            var (_, review) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var order = await _dbContext.Orders.FindAsync(new object[] { cmd.OrderId }, cancellationToken)
                        ?? throw new CentralDomainException("ORDER_NOT_FOUND", "review.order_not_found", $"Order {cmd.OrderId} does not exist.");

                    var evidence = await _dbContext.PaymentEvidences.FindAsync(new object[] { cmd.EvidenceId }, cancellationToken)
                        ?? throw new CentralDomainException("EVIDENCE_NOT_FOUND", "review.evidence_not_found", $"Evidence {cmd.EvidenceId} does not exist.");

                    if (evidence.OrderId != order.Id || evidence.ShopId != order.ShopId)
                    {
                        throw new CentralDomainException("EVIDENCE_ORDER_MISMATCH", "review.evidence_order_mismatch", "Payment evidence does not match the specified order or shop.");
                    }

                    var now = DateTimeOffset.UtcNow;
                    var rev = PaymentReviewRecord.Create(
                        evidence.Id,
                        order.Id,
                        adminId,
                        cmd.Decision,
                        cmd.Notes,
                        now);

                    if (cmd.Decision == PaymentReviewRecord.DecisionApprove)
                    {
                        evidence.SetReviewDecision(PaymentEvidence.ReviewApproved);
                        order.ApprovePayment(evidence.Id, adminId, now);
                    }
                    else if (cmd.Decision == PaymentReviewRecord.DecisionReject)
                    {
                        evidence.SetReviewDecision(PaymentEvidence.ReviewRejected);
                        order.Reject(cmd.Notes ?? "Payment evidence rejected");
                    }
                    else
                    {
                        evidence.SetReviewDecision(PaymentEvidence.ReviewClarification);
                    }

                    _dbContext.PaymentReviewRecords.Add(rev);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "commercial.payment_reviewed",
                        nameof(PaymentReviewRecord),
                        rev.Id.ToString(),
                        _serializer.SerializeCanonical(new { rev.Id, cmd.OrderId, cmd.Decision, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_PAYMENT_REVIEWED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? $"Payment decision: {cmd.Decision}",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(PaymentReviewRecord),
                        rev.Id.ToString(),
                        $"{{\"orderId\":\"{order.Id}\",\"decision\":\"{cmd.Decision}\"}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (rev, $"payment_reviews/{rev.Id}", 1, 1);
                },
                recoverResultAsync: async record =>
                {
                    var revId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.PaymentReviewRecords.FindAsync(new object[] { revId }, cancellationToken)
                        ?? throw new CentralDomainException("REVIEW_NOT_FOUND", "review.not_found", $"Review {revId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return review;
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, command.OrderId.ToString(), cancellationToken);

        var orderDirect = await _dbContext.Orders.FindAsync(new object[] { command.OrderId }, cancellationToken)
            ?? throw new CentralDomainException("ORDER_NOT_FOUND", "review.order_not_found", $"Order {command.OrderId} does not exist.");

        var evidenceDirect = await _dbContext.PaymentEvidences.FindAsync(new object[] { command.EvidenceId }, cancellationToken)
            ?? throw new CentralDomainException("EVIDENCE_NOT_FOUND", "review.evidence_not_found", $"Evidence {command.EvidenceId} does not exist.");

        if (evidenceDirect.OrderId != orderDirect.Id || evidenceDirect.ShopId != orderDirect.ShopId)
        {
            throw new CentralDomainException("EVIDENCE_ORDER_MISMATCH", "review.evidence_order_mismatch", "Payment evidence does not match the specified order or shop.");
        }

        var nowDirect = DateTimeOffset.UtcNow;
        var reviewDirect = PaymentReviewRecord.Create(
            evidenceDirect.Id,
            orderDirect.Id,
            adminId,
            command.Decision,
            command.Notes,
            nowDirect);

        if (command.Decision == PaymentReviewRecord.DecisionApprove)
        {
            evidenceDirect.SetReviewDecision(PaymentEvidence.ReviewApproved);
            orderDirect.ApprovePayment(evidenceDirect.Id, adminId, nowDirect);
        }
        else if (command.Decision == PaymentReviewRecord.DecisionReject)
        {
            evidenceDirect.SetReviewDecision(PaymentEvidence.ReviewRejected);
            orderDirect.Reject(command.Notes ?? "Payment evidence rejected");
        }
        else
        {
            evidenceDirect.SetReviewDecision(PaymentEvidence.ReviewClarification);
        }

        _dbContext.PaymentReviewRecords.Add(reviewDirect);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_PAYMENT_REVIEWED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? $"Payment decision: {command.Decision}",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(PaymentReviewRecord),
            reviewDirect.Id.ToString(),
            $"{{\"orderId\":\"{orderDirect.Id}\",\"decision\":\"{command.Decision}\"}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return reviewDirect;
    }

    public async Task<CommercialLicense> IssueLicenseAsync(
        IssueLicenseCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ApprovedOrderId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_ORDER_ID", "license.invalid_order_id", "Approved order ID is required.");
        }

        if (_operationLedger is not null && command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"license/order/{command.ApprovedOrderId}";

            var (_, license) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var order = await _dbContext.Orders.FindAsync(new object[] { cmd.ApprovedOrderId }, cancellationToken)
                        ?? throw new CentralDomainException("ORDER_NOT_FOUND", "license.order_not_found", $"Order {cmd.ApprovedOrderId} does not exist.");

                    var alreadyFulfilled = await _dbContext.CommercialLicenses.AnyAsync(l => l.InitialOrderId == order.Id, cancellationToken);
                    if (alreadyFulfilled)
                    {
                        throw new CentralDomainException("ORDER_ALREADY_FULFILLED", "license.order_already_fulfilled", $"Order {order.Id} has already been fulfilled into a license.");
                    }

                    var now = DateTimeOffset.UtcNow;
                    var lic = CommercialLicense.Issue(
                        order,
                        cmd.ValidFromUtc,
                        cmd.ValidUntilUtc,
                        now);

                    _dbContext.CommercialLicenses.Add(lic);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "commercial.license_issued",
                        nameof(CommercialLicense),
                        lic.Id.ToString(),
                        _serializer.SerializeCanonical(new { lic.Id, lic.ShopId, lic.TermKind, lic.ValidUntilUtc, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_LICENSE_ISSUED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Commercial license issued",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(CommercialLicense),
                        lic.Id.ToString(),
                        $"{{\"shopId\":\"{lic.ShopId}\",\"term\":\"{lic.TermKind}\",\"validUntil\":\"{lic.ValidUntilUtc}\"}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (lic, $"licenses/{lic.Id}", 1, lic.AuthorityRevision);
                },
                recoverResultAsync: async record =>
                {
                    var licId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.CommercialLicenses.FindAsync(new object[] { licId }, cancellationToken)
                        ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return license;
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, command.ApprovedOrderId.ToString(), cancellationToken);

        var orderDirect = await _dbContext.Orders.FindAsync(new object[] { command.ApprovedOrderId }, cancellationToken)
            ?? throw new CentralDomainException("ORDER_NOT_FOUND", "license.order_not_found", $"Order {command.ApprovedOrderId} does not exist.");

        var alreadyFulfilledDirect = await _dbContext.CommercialLicenses.AnyAsync(l => l.InitialOrderId == orderDirect.Id, cancellationToken);
        if (alreadyFulfilledDirect)
        {
            throw new CentralDomainException("ORDER_ALREADY_FULFILLED", "license.order_already_fulfilled", $"Order {orderDirect.Id} has already been fulfilled into a license.");
        }

        var nowDirect = DateTimeOffset.UtcNow;
        var licenseDirect = CommercialLicense.Issue(
            orderDirect,
            command.ValidFromUtc,
            command.ValidUntilUtc,
            nowDirect);

        _dbContext.CommercialLicenses.Add(licenseDirect);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_LICENSE_ISSUED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Commercial license issued",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(CommercialLicense),
            licenseDirect.Id.ToString(),
            $"{{\"shopId\":\"{licenseDirect.ShopId}\",\"term\":\"{licenseDirect.TermKind}\",\"validUntil\":\"{licenseDirect.ValidUntilUtc}\"}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return licenseDirect;
    }

    public async Task<CommercialLicense> RenewLicenseAsync(
        Guid licenseId,
        RenewLicenseCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (licenseId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_LICENSE_ID", "license.invalid_id", "License ID is required.");
        }

        if (_operationLedger is not null && command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"license/{licenseId}";

            var (_, license) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var lic = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
                        ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

                    if (lic.TermKind == CommercialTerm.Lifetime)
                    {
                        throw new CentralDomainException("LIFETIME_CANNOT_RENEW", "license.lifetime_cannot_renew", "Lifetime license has no expiration and cannot be renewed.");
                    }

                    if (!cmd.ApprovedOrderId.HasValue || cmd.ApprovedOrderId.Value == Guid.Empty)
                    {
                        throw new CentralDomainException("ORDER_REQUIRED", "license.renewal_order_required", "License renewal requires an approved paid order.");
                    }

                    var renewalOrder = await _dbContext.Orders.FindAsync(new object[] { cmd.ApprovedOrderId.Value }, cancellationToken)
                        ?? throw new CentralDomainException("ORDER_NOT_FOUND", "license.renewal_order_not_found", $"Renewal order {cmd.ApprovedOrderId.Value} does not exist.");

                    if (renewalOrder.Status != Order.StatusPaidApproved)
                    {
                        throw new CentralDomainException("ORDER_NOT_APPROVED", "license.renewal_order_not_approved", "Renewal order must be paid and approved.");
                    }

                    if (renewalOrder.ShopId != lic.ShopId)
                    {
                        throw new CentralDomainException("FOREIGN_ORDER", "license.foreign_renewal_order", "Renewal order does not belong to the license's shop.");
                    }

                    if (await _dbContext.CommercialLicenses.AnyAsync(l => l.InitialOrderId == renewalOrder.Id, cancellationToken))
                    {
                        throw new CentralDomainException("ORDER_ALREADY_FULFILLED", "license.renewal_order_already_used", $"Order {renewalOrder.Id} has already been fulfilled for license issuance.");
                    }

                    var renewalOrderIdStr = renewalOrder.Id.ToString();
                    var alreadyConsumedInRenewal = await _dbContext.CentralAuditRecords
                        .AnyAsync(a => a.Action == "COMMERCIAL_LICENSE_RENEWED" && a.DetailsJson != null && a.DetailsJson.Contains(renewalOrderIdStr), cancellationToken);
                    if (alreadyConsumedInRenewal)
                    {
                        throw new CentralDomainException("ORDER_ALREADY_FULFILLED", "license.renewal_order_already_fulfilled", $"Order {renewalOrder.Id} has already been fulfilled for a prior renewal.");
                    }

                    var now = DateTimeOffset.UtcNow;
                    lic.RenewFixedTerm(cmd.NewValidUntilUtc, now);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "commercial.license_renewed",
                        nameof(CommercialLicense),
                        lic.Id.ToString(),
                        _serializer.SerializeCanonical(new { lic.Id, renewalOrderId = renewalOrder.Id, lic.ValidUntilUtc, lic.AuthorityRevision, clientOperationId = command.CommandMeta.ClientOperationId }),
                        now));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_LICENSE_RENEWED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Commercial license renewed",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(CommercialLicense),
                        lic.Id.ToString(),
                        $"{{\"orderId\":\"{renewalOrder.Id}\",\"newValidUntil\":\"{lic.ValidUntilUtc}\",\"authorityRevision\":{lic.AuthorityRevision}}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (lic, $"licenses/{lic.Id}", 1, lic.AuthorityRevision);
                },
                recoverResultAsync: async record =>
                {
                    var licId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.CommercialLicenses.FindAsync(new object[] { licId }, cancellationToken)
                        ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return license;
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, licenseId.ToString(), cancellationToken);

        var licenseDirect = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

        if (licenseDirect.TermKind == CommercialTerm.Lifetime)
        {
            throw new CentralDomainException("LIFETIME_CANNOT_RENEW", "license.lifetime_cannot_renew", "Lifetime license has no expiration and cannot be renewed.");
        }

        if (!command.ApprovedOrderId.HasValue || command.ApprovedOrderId.Value == Guid.Empty)
        {
            throw new CentralDomainException("ORDER_REQUIRED", "license.renewal_order_required", "License renewal requires an approved paid order.");
        }

        var renewalOrderDirect = await _dbContext.Orders.FindAsync(new object[] { command.ApprovedOrderId.Value }, cancellationToken)
            ?? throw new CentralDomainException("ORDER_NOT_FOUND", "license.renewal_order_not_found", $"Renewal order {command.ApprovedOrderId.Value} does not exist.");

        if (renewalOrderDirect.Status != Order.StatusPaidApproved)
        {
            throw new CentralDomainException("ORDER_NOT_APPROVED", "license.renewal_order_not_approved", "Renewal order must be paid and approved.");
        }

        if (renewalOrderDirect.ShopId != licenseDirect.ShopId)
        {
            throw new CentralDomainException("FOREIGN_ORDER", "license.foreign_renewal_order", "Renewal order does not belong to the license's shop.");
        }

        if (await _dbContext.CommercialLicenses.AnyAsync(l => l.InitialOrderId == renewalOrderDirect.Id, cancellationToken))
        {
            throw new CentralDomainException("ORDER_ALREADY_FULFILLED", "license.renewal_order_already_used", $"Order {renewalOrderDirect.Id} has already been fulfilled for license issuance.");
        }

        var renewalOrderIdStrDirect = renewalOrderDirect.Id.ToString();
        var alreadyConsumedInRenewalDirect = await _dbContext.CentralAuditRecords
            .AnyAsync(a => a.Action == "COMMERCIAL_LICENSE_RENEWED" && a.DetailsJson != null && a.DetailsJson.Contains(renewalOrderIdStrDirect), cancellationToken);
        if (alreadyConsumedInRenewalDirect)
        {
            throw new CentralDomainException("ORDER_ALREADY_FULFILLED", "license.renewal_order_already_fulfilled", $"Order {renewalOrderDirect.Id} has already been fulfilled for a prior renewal.");
        }

        var nowDirect = DateTimeOffset.UtcNow;
        licenseDirect.RenewFixedTerm(command.NewValidUntilUtc, nowDirect);

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_LICENSE_RENEWED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Commercial license renewed",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(CommercialLicense),
            licenseDirect.Id.ToString(),
            $"{{\"orderId\":\"{renewalOrderDirect.Id}\",\"newValidUntil\":\"{licenseDirect.ValidUntilUtc}\",\"authorityRevision\":{licenseDirect.AuthorityRevision}}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return licenseDirect;
    }

    public async Task<CommercialLicense> ExtendLicenseAsync(
        Guid licenseId,
        ExtendLicenseCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_operationLedger is not null && command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"license/{licenseId}";

            var (_, license) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    var lic = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
                        ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

                    if (lic.TermKind == CommercialTerm.Lifetime)
                    {
                        throw new CentralDomainException("LIFETIME_CANNOT_EXTEND", "license.lifetime_cannot_extend", "Lifetime license has no expiration and cannot be extended.");
                    }

                    lic.Extend(cmd.ExtensionEndUtc);
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "commercial.license_extended",
                        nameof(CommercialLicense),
                        lic.Id.ToString(),
                        _serializer.SerializeCanonical(new { lic.Id, lic.ValidUntilUtc, lic.AuthorityRevision, clientOperationId = command.CommandMeta.ClientOperationId }),
                        DateTimeOffset.UtcNow));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_LICENSE_EXTENDED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? "Commercial license extended",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(CommercialLicense),
                        lic.Id.ToString(),
                        $"{{\"extendedEnd\":\"{lic.ValidUntilUtc}\",\"authorityRevision\":{lic.AuthorityRevision}}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (lic, $"licenses/{lic.Id}", 1, lic.AuthorityRevision);
                },
                recoverResultAsync: async record =>
                {
                    var licId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.CommercialLicenses.FindAsync(new object[] { licId }, cancellationToken)
                        ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return license;
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, licenseId.ToString(), cancellationToken);

        var licenseDirect = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

        if (licenseDirect.TermKind == CommercialTerm.Lifetime)
        {
            throw new CentralDomainException("LIFETIME_CANNOT_EXTEND", "license.lifetime_cannot_extend", "Lifetime license has no expiration and cannot be extended.");
        }

        licenseDirect.Extend(command.ExtensionEndUtc);

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_LICENSE_EXTENDED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? "Commercial license extended",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(CommercialLicense),
            licenseDirect.Id.ToString(),
            $"{{\"extendedEnd\":\"{licenseDirect.ValidUntilUtc}\",\"authorityRevision\":{licenseDirect.AuthorityRevision}}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return licenseDirect;
    }

    public async Task<CommercialEntitlement> RenewAddonAsync(
        Guid licenseId,
        RenewAddonCommand command,
        Guid adminId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_operationLedger is not null && command.CommandMeta is not null && command.CommandMeta.ClientOperationId != Guid.Empty)
        {
            var actorScope = $"admin:{adminId}";
            var targetResource = $"license/{licenseId}/addon";

            var (_, entitlement) = await _operationLedger.ExecuteAndCaptureOrReplayAsync(
                actorScope: actorScope,
                shopId: null,
                meta: command.CommandMeta,
                payload: command,
                executeAsync: async cmd =>
                {
                    if (cmd.NewValidUntilUtc <= DateTimeOffset.UtcNow)
                    {
                        throw new CentralDomainException("INVALID_RENEWAL_DATE", "entitlement.invalid_renewal_date", "Add-on expiry must be a future time.");
                    }

                    var lic = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
                        ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

                    var order = await _dbContext.Orders.FindAsync(new object[] { cmd.ApprovedOrderId }, cancellationToken)
                        ?? throw new CentralDomainException("ORDER_NOT_FOUND", "entitlement.order_not_found", $"Order {cmd.ApprovedOrderId} does not exist.");
                    await SecurityTransaction.BeginAsync(_dbContext, $"commercial-order:{order.Id:D}", cancellationToken);

                    if (order.Status != Order.StatusPaidApproved)
                    {
                        throw new CentralDomainException("ORDER_NOT_APPROVED", "entitlement.order_not_approved", "Add-on renewal requires an approved order.");
                    }

                    if (order.ShopId != lic.ShopId)
                    {
                        throw new CentralDomainException("FOREIGN_ORDER", "entitlement.foreign_order", "Order does not belong to the license's shop.");
                    }

                    if (order.PurchaseTarget == PlanCatalogItem.TargetBaseLicense)
                    {
                        throw new CentralDomainException("INVALID_ADDON_ORDER", "entitlement.base_order_cannot_renew_addon", "Base license order cannot be used to renew an add-on.");
                    }

                    var targetedEntitlement = await _dbContext.CommercialEntitlements
                        .FirstOrDefaultAsync(e => e.LicenseId == licenseId && e.Kind == order.PurchaseTarget, cancellationToken);
                    ValidateAddonOrderTarget(order, licenseId, targetedEntitlement?.Id);
                    if (await _dbContext.CommercialEntitlements.AnyAsync(e => e.LastOrderId == order.Id, cancellationToken))
                    {
                        throw new CentralDomainException("ORDER_ALREADY_FULFILLED", "entitlement.order_already_fulfilled", $"Order {order.Id} has already renewed an add-on.");
                    }

                    if (await _dbContext.CommercialLicenses.AnyAsync(l => l.InitialOrderId == order.Id, cancellationToken))
                    {
                        throw new CentralDomainException("ORDER_ALREADY_FULFILLED", "entitlement.order_already_fulfilled", $"Order {order.Id} has already been fulfilled for license issuance.");
                    }

                    var ent = await _dbContext.CommercialEntitlements
                        .FirstOrDefaultAsync(e => e.LicenseId == licenseId && e.Kind == order.PurchaseTarget, cancellationToken);

                    if (ent == null)
                    {
                        ent = CommercialEntitlement.Create(
                            lic.Id,
                            order.PurchaseTarget,
                            cmd.NewValidUntilUtc,
                            order.Id);
                        _dbContext.CommercialEntitlements.Add(ent);
                    }
                    else
                    {
                        ent.RenewAddon(cmd.NewValidUntilUtc, order.Id);
                    }

                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _dbContext.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                        command.CommandMeta.ClientOperationId,
                        "commercial.addon_renewed",
                        nameof(CommercialEntitlement),
                        ent.Id.ToString(),
                        _serializer.SerializeCanonical(new { ent.Id, ent.Kind, ent.ValidUntilUtc, clientOperationId = command.CommandMeta.ClientOperationId }),
                        DateTimeOffset.UtcNow));

                    await _auditService.RecordAuditAsync(
                        "COMMERCIAL_ADDON_RENEWED",
                        "ADMIN",
                        adminId,
                        null,
                        cmd.CommandMeta?.Reason ?? $"Add-on {order.PurchaseTarget} renewed",
                        command.CommandMeta.ClientOperationId,
                        command.CommandMeta.ClientOperationId,
                        nameof(CommercialEntitlement),
                        ent.Id.ToString(),
                        $"{{\"kind\":\"{ent.Kind}\",\"validUntil\":\"{ent.ValidUntilUtc}\"}}",
                        true,
                        command.CommandMeta.PayloadSha256,
                        cancellationToken);

                    return (ent, $"entitlements/{ent.Id}", 1, 1);
                },
                recoverResultAsync: async record =>
                {
                    var entId = Guid.Parse(record.ResultReference!.Split('/').Last());
                    return await _dbContext.CommercialEntitlements.FindAsync(new object[] { entId }, cancellationToken)
                        ?? throw new CentralDomainException("ENTITLEMENT_NOT_FOUND", "entitlement.not_found", $"Entitlement {entId} not found during replay.");
                },
                targetResource: targetResource,
                cancellationToken: cancellationToken);

            return entitlement;
        }

        await using var securityTransaction = await SecurityTransaction.BeginAsync(_dbContext, licenseId.ToString(), cancellationToken);

        if (command.NewValidUntilUtc <= DateTimeOffset.UtcNow)
        {
            throw new CentralDomainException("INVALID_RENEWAL_DATE", "entitlement.invalid_renewal_date", "Add-on expiry must be a future time.");
        }

        var licenseDirect = await _dbContext.CommercialLicenses.FindAsync(new object[] { licenseId }, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", $"License {licenseId} does not exist.");

        var orderDirect = await _dbContext.Orders.FindAsync(new object[] { command.ApprovedOrderId }, cancellationToken)
            ?? throw new CentralDomainException("ORDER_NOT_FOUND", "entitlement.order_not_found", $"Order {command.ApprovedOrderId} does not exist.");
        await SecurityTransaction.BeginAsync(_dbContext, $"commercial-order:{orderDirect.Id:D}", cancellationToken);

        if (orderDirect.Status != Order.StatusPaidApproved)
        {
            throw new CentralDomainException("ORDER_NOT_APPROVED", "entitlement.order_not_approved", "Add-on renewal requires an approved order.");
        }

        if (orderDirect.ShopId != licenseDirect.ShopId)
        {
            throw new CentralDomainException("FOREIGN_ORDER", "entitlement.foreign_order", "Order does not belong to the license's shop.");
        }

        if (orderDirect.PurchaseTarget == PlanCatalogItem.TargetBaseLicense)
        {
            throw new CentralDomainException("INVALID_ADDON_ORDER", "entitlement.base_order_cannot_renew_addon", "Base license order cannot be used to renew an add-on.");
        }

        var existingEntitlementDirect = await _dbContext.CommercialEntitlements
            .FirstOrDefaultAsync(e => e.LicenseId == licenseId && e.Kind == orderDirect.PurchaseTarget, cancellationToken);
        ValidateAddonOrderTarget(orderDirect, licenseId, existingEntitlementDirect?.Id);
        if (await _dbContext.CommercialEntitlements.AnyAsync(e => e.LastOrderId == orderDirect.Id, cancellationToken))
        {
            throw new CentralDomainException("ORDER_ALREADY_FULFILLED", "entitlement.order_already_fulfilled", $"Order {orderDirect.Id} has already renewed an add-on.");
        }

        if (await _dbContext.CommercialLicenses.AnyAsync(l => l.InitialOrderId == orderDirect.Id, cancellationToken))
        {
            throw new CentralDomainException("ORDER_ALREADY_FULFILLED", "entitlement.order_already_fulfilled", $"Order {orderDirect.Id} has already been fulfilled for license issuance.");
        }

        var entitlementDirect = await _dbContext.CommercialEntitlements
            .FirstOrDefaultAsync(e => e.LicenseId == licenseId && e.Kind == orderDirect.PurchaseTarget, cancellationToken);

        if (entitlementDirect == null)
        {
            entitlementDirect = CommercialEntitlement.Create(
                licenseDirect.Id,
                orderDirect.PurchaseTarget,
                command.NewValidUntilUtc,
                orderDirect.Id);
            _dbContext.CommercialEntitlements.Add(entitlementDirect);
        }
        else
        {
            entitlementDirect.RenewAddon(command.NewValidUntilUtc, orderDirect.Id);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _auditService.RecordAuditAsync(
            "COMMERCIAL_ADDON_RENEWED",
            "ADMIN",
            adminId,
            null,
            command.CommandMeta?.Reason ?? $"Add-on {orderDirect.PurchaseTarget} renewed",
            null,
            command.CommandMeta?.ClientOperationId,
            nameof(CommercialEntitlement),
            entitlementDirect.Id.ToString(),
            $"{{\"kind\":\"{entitlementDirect.Kind}\",\"validUntil\":\"{entitlementDirect.ValidUntilUtc}\"}}",
            true,
            null,
            cancellationToken);

        if (securityTransaction is not null)
        {
            await securityTransaction.CommitAsync(cancellationToken);
        }

        return entitlementDirect;
    }

    private static void ValidateAddonOrderTarget(Order order, Guid licenseId, Guid? existingEntitlementId)
    {
        if (order.BaseLicenseId is Guid targetedLicenseId && targetedLicenseId != licenseId ||
            order.ExistingEntitlementId is Guid targetedEntitlementId && targetedEntitlementId != existingEntitlementId)
        {
            throw new CentralDomainException("TARGET_MISMATCH", "entitlement.order_target_mismatch", "The paid order is bound to a different license or entitlement.");
        }
    }

    private static string GetStorageFullPath(string relativePath)
    {
        var configured = Environment.GetEnvironmentVariable("CENTRAL_PAYMENT_STORAGE_ROOT");
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
        {
            throw new CentralDomainException("PAYMENT_STORAGE_NOT_CONFIGURED", "storage.root_unavailable", "An explicit owned payment-storage root is required.");
        }

        var basePath = Path.GetFullPath(configured);
        if ((Path.IsPathRooted(relativePath) && !Path.IsPathFullyQualified(relativePath)) ||
            relativePath.StartsWith(@"\\?\", StringComparison.Ordinal) || relativePath.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new CentralDomainException("STORAGE_PATH_REJECTED", "storage.path_rejected", "Ambiguous storage path.");
        }

        var path = Path.GetFullPath(relativePath, basePath);
        var relative = Path.GetRelativePath(basePath, path);
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x.Contains(':') || x.EndsWith('.') || x.EndsWith(' ') ||
                System.Text.RegularExpressions.Regex.IsMatch(x, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(\..*)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
        {
            throw new CentralDomainException("STORAGE_PATH_REJECTED", "storage.path_rejected", "Payment storage path is outside its owned root.");
        }

        var volumeRoot = Path.GetPathRoot(path)!;
        var current = volumeRoot;
        foreach (var part in path[volumeRoot.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new CentralDomainException("STORAGE_PATH_REJECTED", "storage.path_rejected", "Reparse storage components are prohibited.");
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }
}
