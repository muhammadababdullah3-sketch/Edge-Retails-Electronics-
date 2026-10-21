using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Contracts.Packs;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/inventory/packs")]
public sealed class PackContainersController : ControllerBase
{
    private readonly EdgeRetailsDbContext _db;
    private readonly IInventoryRepository _inventory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IPackSubLedgerAllocationAuthority _packAllocations;
    private readonly IInventoryCostAllocator? _costAllocator;

    public PackContainersController(
        EdgeRetailsDbContext db,
        IInventoryRepository inventory,
        IUnitOfWork unitOfWork,
        IClock clock,
        IPackSubLedgerAllocationAuthority packAllocations,
        IInventoryCostAllocator? costAllocator = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _packAllocations = packAllocations ?? throw new ArgumentNullException(nameof(packAllocations));
        _costAllocator = costAllocator;
    }

    [HttpGet]
    public async Task<IActionResult> GetPacks(
        [FromQuery] Guid? productId = null,
        [FromQuery] string? conditionFilter = null,
        [FromQuery] decimal? minContinuousLength = null,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesPosUse);
        if (denied is not null && this.RequirePermission(PermissionKeys.InventoryManage) is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var isCashier = actor is not null && string.Equals(actor.RoleName, "Cashier", StringComparison.OrdinalIgnoreCase);

        var clampedPage = Math.Max(1, pageNumber);
        var clampedSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 100);

        var query = _db.InventoryUnits.AsNoTracking()
            .Where(u => u.PackState != null && u.Status == InventoryUnitStatus.InStock);

        if (productId.HasValue && productId.Value != Guid.Empty)
        {
            query = query.Where(u => u.ProductId == productId.Value);
        }

        if (minContinuousLength.HasValue && minContinuousLength.Value > 0)
        {
            query = query.Where(u => u.RemainingBaseQuantity >= minContinuousLength.Value);
        }

        if (!string.IsNullOrWhiteSpace(conditionFilter))
        {
            var filter = conditionFilter.Trim();
            if (string.Equals(filter, "Open", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(u => u.PackState == PackState.Open);
            }
            else if (string.Equals(filter, "Sealed", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(u => u.PackState == PackState.Sealed);
            }
            else if (string.Equals(filter, "Remnant", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(u => u.IsLooseStock || (u.RemainingBaseQuantity != null && u.RemainingBaseQuantity < 5m));
            }
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // Sort Open packs first (FIFO usage to clear open stock), then Sealed, ordered by remaining quantity
        var units = await query
            .OrderBy(u => u.PackState == PackState.Open ? 0 : 1)
            .ThenBy(u => u.RemainingBaseQuantity)
            .ThenBy(u => u.CreatedAt)
            .Skip((clampedPage - 1) * clampedSize)
            .Take(clampedSize)
            .ToListAsync(cancellationToken);

        // MON-W3-003: Bind actual product/unit dimension, conversions, and continuity
        var productIds = units.Select(u => u.ProductId).Distinct().ToList();
        var products = await _db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var baseUnitIds = products.Values.Select(p => p.BaseUnitId).Distinct().ToList();
        var uoms = await _db.Units.AsNoTracking()
            .Where(u => baseUnitIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, cancellationToken);

        var items = units.Select(u =>
        {
            products.TryGetValue(u.ProductId, out var prod);
            EdgeRetails.Domain.Catalog.Unit? uom = null;
            if (prod is not null)
            {
                uoms.TryGetValue(prod.BaseUnitId, out uom);
            }
            return MapToDto(u, isCashier, prod, uom);
        }).ToList();

        return Ok(new PackContainerQueryResponse(totalCount, items));
    }

    [HttpGet("{trackingCode}")]
    public async Task<IActionResult> GetPackByTrackingCode(
        [FromRoute] string trackingCode,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesPosUse);
        if (denied is not null && this.RequirePermission(PermissionKeys.InventoryManage) is not null)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(trackingCode))
        {
            return BadRequest(new { code = "pack.tracking_code_required", message = "Tracking code is required." });
        }

        var actor = HttpContext.GetActorContext();
        var isCashier = actor is not null && string.Equals(actor.RoleName, "Cashier", StringComparison.OrdinalIgnoreCase);

        var unit = await _db.InventoryUnits.AsNoTracking()
            .FirstOrDefaultAsync(u => u.TrackingCode == trackingCode.Trim(), cancellationToken);

        if (unit is null || unit.PackState is null)
        {
            return NotFound(new
            {
                code = "pack.not_found",
                message = $"Pack container with tracking code '{trackingCode}' not found."
            });
        }

        var prod = await _db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == unit.ProductId, cancellationToken);
        EdgeRetails.Domain.Catalog.Unit? uom = null;
        if (prod is not null)
        {
            uom = await _db.Units.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == prod.BaseUnitId, cancellationToken);
        }

        return Ok(MapToDto(unit, isCashier, prod, uom));
    }

    [HttpPost("allocate")]
    public async Task<IActionResult> AllocatePack(
        [FromBody] EdgeRetails.Application.Contracts.Packs.PackAllocationRequest request,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.SalesPosUse ?? "sales.pos_use");
        if (denied is not null)
        {
            return denied;
        }

        if (request.ContainerId == Guid.Empty)
        {
            return BadRequest(new { code = "pack.container_id_required", message = "Container ID is required." });
        }

        if (request.RequestedQuantity <= 0)
        {
            return BadRequest(new { code = "pack.invalid_quantity", message = "Requested quantity must be positive." });
        }

        // CRITICAL REPLAY LOOKUP: Before checking version/capacity or mutating in-memory unit
        if (request.TransactionId != Guid.Empty)
        {
            var existingAlloc = await _db.PackAllocations
                .FirstOrDefaultAsync(a => a.MovementId == request.TransactionId, cancellationToken);

            if (existingAlloc is not null)
            {
                // Verify request fingerprint/container/quantity/line: reject changed-payload key reuse
                var lineItemMatch = existingAlloc.SaleItemId == (request.LineItemId != Guid.Empty ? request.LineItemId : null);
                if (existingAlloc.PackUnitId != request.ContainerId ||
                    existingAlloc.AllocatedBaseQuantity != request.RequestedQuantity ||
                    !lineItemMatch)
                {
                    return Conflict(new
                    {
                        code = "pack.idempotency_payload_mismatch",
                        message = "Transaction ID was already used for a different pack allocation request payload."
                    });
                }

                // Identical replay: return existing allocation outcome without re-validating capacity/version or re-mutating
                var remnantEvalReplay = _packAllocations.EvaluateRemnant(existingAlloc.RemainingQuantityAfter);
                return Ok(new PackAllocationResponse(
                    AllocationId: existingAlloc.Id,
                    AllocatedQuantity: existingAlloc.AllocatedBaseQuantity,
                    RemainingQuantity: existingAlloc.RemainingQuantityAfter,
                    ContinuousLength: existingAlloc.RemainingQuantityAfter,
                    IsRemnant: remnantEvalReplay.IsShortRemnant));
            }

            var existingMovement = await _db.InventoryMovements
                .FirstOrDefaultAsync(m => m.Id == request.TransactionId, cancellationToken);
            if (existingMovement is not null)
            {
                return Conflict(new
                {
                    code = "pack.transaction_id_collision",
                    message = "Transaction ID already exists for an unrelated inventory movement."
                });
            }
        }

        var unit = await _db.InventoryUnits
            .FirstOrDefaultAsync(u => u.Id == request.ContainerId, cancellationToken);

        if (unit is null || unit.PackState is null)
        {
            return NotFound(new
            {
                code = "pack.not_found",
                message = $"Pack container '{request.ContainerId}' not found."
            });
        }

        if (unit.Status != InventoryUnitStatus.InStock)
        {
            return UnprocessableEntity(new
            {
                code = "pack.not_in_stock",
                message = $"Pack container '{request.ContainerId}' is not in stock."
            });
        }

        if (request.ExpectedVersion.HasValue && unit.Version != request.ExpectedVersion.Value)
        {
            return Conflict(new
            {
                code = "pack.stale_version_conflict",
                message = "Pack container version conflict; another terminal has modified or consumed this pack."
            });
        }

        var currentRemaining = unit.RemainingBaseQuantity ?? 0m;
        if (currentRemaining < request.RequestedQuantity)
        {
            if (!request.AllowSplitCut)
            {
                return UnprocessableEntity(new
                {
                    code = "pack.split_cut_disallowed",
                    message = $"Requested quantity '{request.RequestedQuantity}' exceeds container remaining capacity '{currentRemaining}' and split cuts are disallowed."
                });
            }

            return UnprocessableEntity(new
            {
                code = "pack.insufficient_quantity",
                message = $"Pack container has insufficient quantity ({currentRemaining} < {request.RequestedQuantity})."
            });
        }

        // Lot foreign key integrity - do not pick an unrelated latest lot
        if (!unit.InventoryLotId.HasValue || unit.InventoryLotId.Value == Guid.Empty)
        {
            return UnprocessableEntity(new
            {
                code = "pack.missing_lot",
                message = $"Pack container '{unit.Id}' is not associated with an inventory lot."
            });
        }
        var lotId = unit.InventoryLotId.Value;

        // Container capacity integrity - do not default invalid initial quantity to 1
        if (!unit.InitialBaseQuantity.HasValue || unit.InitialBaseQuantity.Value <= 0m)
        {
            return UnprocessableEntity(new
            {
                code = "pack.invalid_container_capacity",
                message = $"Pack container '{unit.Id}' has invalid initial base quantity."
            });
        }
        var initialQty = unit.InitialBaseQuantity.Value;

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        try
        {
            // Stock balance integrity & Single Stock Balance Invariant (ADR-04)
            var stock = await _inventory.GetStockBalanceForUpdateAsync(unit.ProductId, cancellationToken);
            if (stock is null)
            {
                return UnprocessableEntity(new
                {
                    code = "pack.missing_stock_balance",
                    message = $"Product stock balance was not found for product '{unit.ProductId}'."
                });
            }

            if (stock.SellableQty < request.RequestedQuantity)
            {
                return UnprocessableEntity(new
                {
                    code = "pack.insufficient_sellable_stock",
                    message = $"Product stock balance has insufficient sellable quantity ({stock.SellableQty} < {request.RequestedQuantity})."
                });
            }

            // Lot bucket balance integrity
            var lotBalance = await _inventory.GetLotBucketBalanceForUpdateAsync(lotId, InventoryBucket.Sellable, cancellationToken);
            if (lotBalance is null)
            {
                return UnprocessableEntity(new
                {
                    code = "pack.missing_lot_balance",
                    message = $"Inventory lot bucket balance was not found for lot '{lotId}'."
                });
            }

            if (lotBalance.Quantity < request.RequestedQuantity)
            {
                return UnprocessableEntity(new
                {
                    code = "pack.insufficient_lot_balance",
                    message = $"Inventory lot has insufficient sellable balance ({lotBalance.Quantity} < {request.RequestedQuantity})."
                });
            }

            // Approved fractional costing per base unit and total snapshot conservation
            var unitCostSnapshot = decimal.Round(unit.AcquisitionCost / initialQty, 6, MidpointRounding.AwayFromZero);
            var totalCostSnapshot = decimal.Round(request.RequestedQuantity * unitCostSnapshot, 6, MidpointRounding.AwayFromZero);

            // Apply physical unit consumption
            var fromStatus = unit.Status;
            unit.ConsumeFromPack(request.RequestedQuantity);
            if (unit.PackState == PackState.Consumed)
            {
                unit.Status = InventoryUnitStatus.Sold;
            }

            // Apply stock balance delta
            var quantityBefore = stock.SellableQty;
            stock.ApplyDelta(InventoryBucket.Sellable, -request.RequestedQuantity);
            var quantityAfter = stock.SellableQty;

            // Apply lot bucket balance delta
            lotBalance.Quantity = QuantityMath.RoundQuantity(lotBalance.Quantity - request.RequestedQuantity);

            var actor = HttpContext.GetActorContext();
            var actorId = actor?.UserId ?? Guid.Empty;

            var movementId = request.TransactionId != Guid.Empty ? request.TransactionId : Guid.CreateVersion7();
            var newMovement = new InventoryMovement
            {
                Id = movementId,
                ProductId = unit.ProductId,
                MovementType = InventoryMovementType.SaleOut,
                ReferenceType = "PACK_ALLOCATION",
                ReferenceId = request.LineItemId != Guid.Empty ? request.LineItemId : null,
                UnitCostSnapshot = unitCostSnapshot,
                ActorId = actorId,
                OccurredAt = _clock.UtcNow,
                CorrelationId = movementId,
                Note = $"Pack allocation from container {unit.TrackingCode}"
            };
            _inventory.AddMovement(newMovement);

            var movementEffect = new InventoryMovementEffect
            {
                MovementId = movementId,
                StockBucket = InventoryBucket.Sellable,
                QuantityDelta = -request.RequestedQuantity,
                QuantityBefore = quantityBefore,
                QuantityAfter = quantityAfter
            };
            _inventory.AddMovementEffect(movementEffect);

            var lotConsumption = new InventoryLotConsumption
            {
                LotId = lotId,
                MovementId = movementId,
                Quantity = request.RequestedQuantity,
                UnitCostSnapshot = unitCostSnapshot,
                TotalCostSnapshot = totalCostSnapshot,
                OccurredAt = _clock.UtcNow
            };
            _inventory.AddLotConsumption(lotConsumption);

            if (_costAllocator is not null)
            {
                await _costAllocator.RemoveCarryingValueAsync(
                    unit.ProductId,
                    request.RequestedQuantity,
                    unitCostSnapshot,
                    cancellationToken);
            }

            var movementUnit = new InventoryMovementUnit
            {
                MovementId = movementId,
                InventoryUnitId = unit.Id,
                FromStatus = fromStatus,
                ToStatus = unit.Status
            };
            _inventory.AddMovementUnit(movementUnit);

            var allocation = new PackAllocation
            {
                Id = Guid.CreateVersion7(),
                PackUnitId = unit.Id,
                MovementId = movementId,
                LotId = lotId,
                OperationType = PackAllocationOperationTypes.Sale,
                SaleItemId = request.LineItemId != Guid.Empty ? request.LineItemId : null,
                AllocatedBaseQuantity = request.RequestedQuantity,
                UnitCostSnapshot = unitCostSnapshot,
                TotalCostSnapshot = totalCostSnapshot,
                RemainingQuantityAfter = unit.RemainingBaseQuantity ?? 0m,
                PackStateAfter = unit.PackState ?? PackState.Consumed,
                ActorId = actorId,
                OccurredAt = _clock.UtcNow,
                CreatedAt = _clock.UtcNow
            };

            allocation.ValidateInvariants();
            _inventory.AddPackAllocation(allocation);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            var remnantEvalFinal = _packAllocations.EvaluateRemnant(unit.RemainingBaseQuantity ?? 0m);

            return Ok(new PackAllocationResponse(
                AllocationId: allocation.Id,
                AllocatedQuantity: request.RequestedQuantity,
                RemainingQuantity: unit.RemainingBaseQuantity ?? 0m,
                ContinuousLength: unit.RemainingBaseQuantity ?? 0m,
                IsRemnant: remnantEvalFinal.IsShortRemnant));
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException || IsTransientConcurrencyException(ex))
        {
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch
                {
                    // Ignore rollback failure if transaction is already completed or aborted
                }
            }

            // Clear poisoned entities in DbContext so subsequent readback executes cleanly without aborted state
            _db.ChangeTracker.Clear();

            // Coordinate concurrent duplicate requests through established transactional authority
            if (request.TransactionId != Guid.Empty)
            {
                try
                {
                    var committedAlloc = await _db.PackAllocations.AsNoTracking()
                        .FirstOrDefaultAsync(a => a.MovementId == request.TransactionId, cancellationToken);
                    if (committedAlloc is not null)
                    {
                        var lineMatch = committedAlloc.SaleItemId == (request.LineItemId != Guid.Empty ? request.LineItemId : null);
                        if (committedAlloc.PackUnitId == request.ContainerId &&
                            committedAlloc.AllocatedBaseQuantity == request.RequestedQuantity &&
                            lineMatch)
                        {
                            var remnantEval = _packAllocations.EvaluateRemnant(committedAlloc.RemainingQuantityAfter);
                            return Ok(new PackAllocationResponse(
                                AllocationId: committedAlloc.Id,
                                AllocatedQuantity: committedAlloc.AllocatedBaseQuantity,
                                RemainingQuantity: committedAlloc.RemainingQuantityAfter,
                                ContinuousLength: committedAlloc.RemainingQuantityAfter,
                                IsRemnant: remnantEval.IsShortRemnant));
                        }

                        return Conflict(new
                        {
                            code = "pack.idempotency_payload_mismatch",
                            message = "Transaction ID was already used for a different pack allocation request payload."
                        });
                    }
                }
                catch
                {
                    // Ignore transient/aborted read failures during idempotency recovery
                }
            }

            if (ex is DbUpdateConcurrencyException)
            {
                return Conflict(new
                {
                    code = "pack.stale_version_conflict",
                    message = "Pack container version conflict; another terminal has modified or consumed this pack."
                });
            }

            if (IsTransientConcurrencyException(ex))
            {
                return Conflict(new
                {
                    code = "pack.transient_concurrency_conflict",
                    message = "Transient concurrency conflict (PostgreSQL serialization failure or deadlock); competing command may be retried."
                });
            }

            throw;
        }
    }

    private static PackContainerDto MapToDto(
        InventoryUnit unit,
        bool isCashier,
        Product? product = null,
        EdgeRetails.Domain.Catalog.Unit? baseUnit = null)
    {
        var condition = unit.Status switch
        {
            InventoryUnitStatus.Damaged => "Damaged",
            _ => (unit.IsLooseStock || (unit.RemainingBaseQuantity != null && unit.RemainingBaseQuantity < 5m)) ? "Remnant" : "Intact"
        };

        // MON-W3-003: Derive unit of measure from product base unit / tracking mode
        var uomSymbol = !string.IsNullOrWhiteSpace(baseUnit?.Symbol)
            ? baseUnit.Symbol
            : (product?.TrackingMode == TrackingMode.Length ? "m" : "pcs");

        var continuousLength = unit.RemainingBaseQuantity ?? 0m;

        return new PackContainerDto(
            ContainerId: unit.Id,
            TrackingCode: unit.TrackingCode ?? string.Empty,
            State: unit.PackState?.ToString() ?? "Unknown",
            InitialQuantity: unit.InitialBaseQuantity ?? 0m,
            RemainingQuantity: unit.RemainingBaseQuantity ?? 0m,
            ContinuousLength: continuousLength,
            UnitOfMeasure: uomSymbol,
            Condition: condition,
            ConcurrencyVersion: unit.Version,
            UnitCost: isCashier ? null : unit.AcquisitionCost);
    }

    private static bool IsTransientConcurrencyException(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException pgEx && pgEx.SqlState is "40001" or "40P01")
            {
                return true;
            }
        }

        return false;
    }
}
