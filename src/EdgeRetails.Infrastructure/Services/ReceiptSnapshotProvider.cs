using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Common;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class ReceiptSnapshotProvider : IReceiptSnapshotProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly EdgeRetailsDbContext _db;

    public ReceiptSnapshotProvider(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public async Task<string> CaptureAsync(CancellationToken cancellationToken)
    {
        var shop = await _db.ShopProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.ProfileKey == "PRIMARY",
                cancellationToken)
            ?? throw new BusinessRuleException(
                "system.shop_profile_missing",
                "Shop profile must be configured before completing a sale.");

        var receipt = await _db.ReceiptTemplateSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.TemplateKey == "PRIMARY",
                cancellationToken)
            ?? throw new BusinessRuleException(
                "system.receipt_template_missing",
                "Receipt settings must be configured before completing a sale.");

        return JsonSerializer.Serialize(
            new ReceiptSnapshot(
                shop.ShopName,
                shop.Phone,
                shop.Address,
                receipt.Header,
                receipt.Footer,
                receipt.ShowCustomer,
                receipt.ShowCashier,
                receipt.LogoBehavior,
                receipt.TemplateVersion),
            JsonOptions);
    }

    private sealed record ReceiptSnapshot(
        string ShopName,
        string? Phone,
        string? Address,
        string? Header,
        string? Footer,
        bool ShowCustomer,
        bool ShowCashier,
        string LogoBehavior,
        int TemplateVersion);
}
