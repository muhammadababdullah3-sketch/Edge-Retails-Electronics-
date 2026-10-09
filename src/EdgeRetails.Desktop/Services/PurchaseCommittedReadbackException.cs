using EdgeRetails.Application.Features.Purchasing;

namespace EdgeRetails.Desktop.Services;

/// <summary>The Server confirmed creation; only the subsequent display read failed.</summary>
public sealed class PurchaseCommittedReadbackException(CreatePurchaseResult result, Exception innerException)
    : Exception($"Purchase {result.PurchaseNumber} is saved. Its details are unavailable; refresh without saving again.", innerException)
{
    public CreatePurchaseResult Result { get; } = result;
}
