using System.Text.Json;
using EdgeRetails.Application.Features.Purchasing;

namespace EdgeRetails.Desktop.Services;

internal static class PhysicalIntakeOperationIntent
{
    public static string Key(Guid purchaseId, Guid productUnitId) =>
        $"physical-intake:{purchaseId:D}:{productUnitId:D}";

    public static string Payload(ReceiveProductIntakeCommand command) =>
        JsonSerializer.Serialize(new
        {
            command.PurchaseId,
            command.ProductId,
            command.ProductUnitId,
            command.EnteredQuantity,
            command.EnteredUnitCost,
            command.SerializedUnits,
            command.CreatedBy,
            command.Note
        });
}
