using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Features.Inventory;

public enum HistoricalMissingClassification { CONCLUSIVE, AMBIGUOUS, NOT_MISSING }

// Read-only evidence, including all links for each source movement. A filtered
// single-unit link list cannot prove attribution for a grouped historical event.
public sealed record HistoricalMissingEvidence(
    InventoryUnit Unit,
    IReadOnlyList<InventoryMovement> Movements,
    IReadOnlyList<InventoryMovementUnit> UnitLinks,
    IReadOnlyList<InventoryMovementEffect> Effects,
    IReadOnlyList<InventoryLotConsumption> Consumptions,
    IReadOnlyList<StockAdjustment> Adjustments,
    IReadOnlyList<StockAdjustmentItem> AdjustmentItems,
    IReadOnlyList<Stocktake> Stocktakes,
    IReadOnlyList<StocktakeItem> StocktakeItems,
    IReadOnlyList<StocktakeUnitCheck> StocktakeChecks);

public sealed record HistoricalMissingClassificationResult(
    HistoricalMissingClassification Classification, string Reason, Guid? SourceMovementId = null);

// Classification never changes a status, removes value, or authorizes Found.
// It intentionally refuses grouped history without exact value attribution.
public sealed class HistoricalMissingClassifier
{
    public HistoricalMissingClassificationResult Classify(HistoricalMissingEvidence evidence)
    {
        var unit = evidence.Unit;
        if (unit.Status != InventoryUnitStatus.Scrapped)
        {
            return new(HistoricalMissingClassification.NOT_MISSING, "Not a legacy Scrapped candidate.");
        }
        var transitions = evidence.UnitLinks.Where(x => x.InventoryUnitId == unit.Id)
            .Join(evidence.Movements, link => link.MovementId, movement => movement.Id,
                (link, movement) => (Link: link, Movement: movement)).ToArray();
        var candidates = transitions.Where(x => x.Link.ToStatus == InventoryUnitStatus.Scrapped).ToArray();
        if (candidates.Length != 1)
        {
            return Ambiguous("A unique historical terminal episode is not proven.");
        }
        var (link, source) = candidates[0];
        if (source.ProductId != unit.ProductId || source.ActorId == Guid.Empty || source.CorrelationId == Guid.Empty ||
            transitions.Any(x => x.Movement.Id != source.Id && x.Movement.OccurredAt >= source.OccurredAt))
        {
            return Ambiguous("Source lineage or absence of later contradictory transitions is not proven.");
        }
        var effects = evidence.Effects.Where(x => x.MovementId == source.Id).ToArray();
        if (effects.Any(x => x.StockBucket == InventoryBucket.Scrap && x.QuantityDelta > 0m))
        {
            return new(HistoricalMissingClassification.NOT_MISSING, "Positive retained Scrap destination is recorded.", source.Id);
        }
        if (evidence.UnitLinks.Count(x => x.MovementId == source.Id) != 1 || effects.Length != 1 ||
            link.FromStatus is not (InventoryUnitStatus.InStock or InventoryUnitStatus.Damaged or
                InventoryUnitStatus.Defective or InventoryUnitStatus.WithSupplier))
        {
            return Ambiguous("Exact single-unit source attribution is not proven.");
        }
        var effect = effects[0];
        if (effect.QuantityDelta >= 0m || effect.QuantityAfter < 0m ||
            effect.QuantityBefore + effect.QuantityDelta != effect.QuantityAfter ||
            InventoryUnitAccountingPolicy.GetRule(link.FromStatus.Value).AuthoritativeBucket != effect.StockBucket)
        {
            return Ambiguous("Matching negative source stock effect is not proven.");
        }
        var consumed = evidence.Consumptions.Where(x => x.MovementId == source.Id).ToArray();
        if (unit.InventoryLotId is null || consumed.Length != 1 || consumed[0].LotId != unit.InventoryLotId ||
            consumed[0].Quantity != -effect.QuantityDelta || consumed[0].TotalCostSnapshot < 0m ||
            consumed[0].UnitCostSnapshot < 0m ||
            decimal.Round(consumed[0].Quantity * consumed[0].UnitCostSnapshot, 6, MidpointRounding.AwayFromZero) != consumed[0].TotalCostSnapshot ||
            decimal.Round(consumed[0].TotalCostSnapshot, 2, MidpointRounding.AwayFromZero) != source.RecognizedLossAmount)
        {
            return Ambiguous("Matching lot/value derecognition and recorded loss are not proven.");
        }

        if (source.ReferenceType == "STOCK_ADJUSTMENT" && source.MovementType == InventoryMovementType.StockAdjustment)
        {
            var items = evidence.AdjustmentItems.Where(x => x.Id == source.ReferenceId && x.ProductId == unit.ProductId).ToArray();
            if (items.Length == 1)
            {
                var item = items[0];
                var approved = evidence.Adjustments.Any(x => x.Id == item.StockAdjustmentId &&
                    x.Status == StockAdjustmentStatus.Posted && x.Reason == StockAdjustmentReason.Lost &&
                    x.ActorId == source.ActorId && x.CorrelationId == source.CorrelationId);
                if (approved && item.Direction == StockAdjustmentDirection.Decrease && item.TargetBucket == effect.StockBucket &&
                    item.BaseQuantity == -effect.QuantityDelta && item.TotalCostSnapshot == consumed[0].TotalCostSnapshot)
                {
                    return new(HistoricalMissingClassification.CONCLUSIVE, "Posted explicit Lost source has exact stock/lot/value lineage.", source.Id);
                }
            }
        }
        else if (source.ReferenceType == "STOCKTAKE" && source.MovementType == InventoryMovementType.PhysicalCountCorrection)
        {
            var posted = evidence.Stocktakes.Any(x => x.Id == source.ReferenceId && x.Status == StocktakeStatus.Posted && x.PostedAt.HasValue);
            var items = evidence.StocktakeItems.Where(x => x.StocktakeId == source.ReferenceId && x.ProductId == unit.ProductId).ToArray();
            if (posted && items.Length == 1 && items[0].VarianceQty == effect.QuantityDelta &&
                evidence.StocktakeChecks.Count(x => x.StocktakeItemId == items[0].Id && x.InventoryUnitId == unit.Id &&
                    x.Result == StocktakeUnitCheckResult.Missing) == 1)
            {
                return new(HistoricalMissingClassification.CONCLUSIVE, "Posted stocktake shortage has exact Missing check and stock/lot/value lineage.", source.Id);
            }
        }
        return Ambiguous("Approved explicit Lost or posted Missing stocktake authority is not proven.");
    }

    private static HistoricalMissingClassificationResult Ambiguous(string reason) =>
        new(HistoricalMissingClassification.AMBIGUOUS, reason);
}
