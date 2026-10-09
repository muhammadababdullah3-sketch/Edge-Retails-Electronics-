using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.UnitTests;

public sealed class TrackingManufacturerIdentityTests
{
    [Theory]
    [InlineData("AB\u200BC")]
    [InlineData("AB\U000E0001C")]
    [InlineData("AB\u0001C")]
    public void Serial_rejects_control_and_format_codepoints_including_supplementary(string raw)
    {
        var ex = Assert.Throws<EdgeRetails.Domain.Common.BusinessRuleException>(() => IdentityNormalizationRules.NormalizeSerialNumber(raw));
        Assert.Equal("identity.serial_unsupported_character", ex.Code);
    }

    [Fact]
    public void Imei_maps_supplementary_unicode_decimal_digits_to_ascii_without_checksum_semantics()
    {
        var raw = string.Concat("86012345678901".Select(c => char.ConvertFromUtf32(0x104A0 + c - '0')));
        Assert.Equal("86012345678901", IdentityNormalizationRules.NormalizeImeiIdentity(raw));
    }

    [Fact]
    public void Serial_normalization_is_case_insensitive_and_unicode_deterministic()
    {
        var normalized = IdentityNormalizationRules.NormalizeSerialNumber("  ａｂ-C12  ");
        Assert.Equal("AB-C12", normalized);
    }

    [Fact]
    public void Imei_identity_normalization_accepts_visual_separators_without_changing_digits()
    {
        var normalized = IdentityNormalizationRules.NormalizeImeiIdentity("356-938-035-643-809");
        Assert.Equal("356938035643809", normalized);
    }

    [Fact]
    public void Imei_identity_normalization_rejects_non_visual_characters()
    {
        var ex = Assert.Throws<EdgeRetails.Domain.Common.BusinessRuleException>(
            () => IdentityNormalizationRules.NormalizeImeiIdentity("35693803564380X"));
        Assert.Equal("identity.imei_unsupported_character", ex.Code);
    }

    [Fact]
    public void Inventory_repository_canonicalizes_legacy_columns_and_adds_identity_claims_atomically()
    {
        using var db = CreateDb();
        var repository = new InventoryRepository(db);
        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            SerialNumber = "  ab-C12  ",
            Imei1 = "356-938-035-643-809",
            Imei2 = "860-123-456-789-01",
            CreatedAt = DateTimeOffset.UtcNow
        };

        repository.AddInventoryUnit(unit);

        Assert.Equal("AB-C12", unit.SerialNumber);
        Assert.Equal("356938035643809", unit.Imei1);
        Assert.Equal("86012345678901", unit.Imei2);
        var claims = db.InventoryUnitIdentityClaims.Local.OrderBy(x => x.IdentifierSlot).ToArray();
        Assert.Equal(3, claims.Length);
        Assert.All(claims, x => Assert.Equal(IdentityNormalizationRules.ManufacturerIdentityNormalizationVersion, x.NormalizationVersion));
    }

    [Fact]
    public void Immutable_claim_history_retains_unit_slot_uniqueness_and_active_ownership_is_globally_unique()
    {
        using var db = CreateDb();
        var entity = db.Model.FindEntityType(typeof(InventoryUnitIdentityClaim));
        Assert.NotNull(entity);
        var indexes = entity!.GetIndexes().Where(x => x.IsUnique).ToArray();

        var historyIndex = Assert.Single(entity.GetIndexes(), x =>
            x.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(InventoryUnitIdentityClaim.IdentifierType), nameof(InventoryUnitIdentityClaim.NormalizedValue) }));
        Assert.False(historyIndex.IsUnique);
        Assert.Contains(indexes, x =>
            x.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(InventoryUnitIdentityClaim.InventoryUnitId), nameof(InventoryUnitIdentityClaim.IdentifierSlot) }));
        var ownership = db.Model.FindEntityType(typeof(InventoryUnitIdentityOwnership));
        Assert.NotNull(ownership);
        Assert.Equal(new[] { nameof(InventoryUnitIdentityOwnership.NormalizedValue) },
            ownership!.FindPrimaryKey()!.Properties.Select(x => x.Name).ToArray());
        Assert.Contains(ownership.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { nameof(InventoryUnitIdentityOwnership.InventoryUnitIdentityClaimId) }));
        Assert.Contains(ownership.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(InventoryUnitIdentityClaim) &&
            x.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(InventoryUnitIdentityOwnership.InventoryUnitIdentityClaimId), nameof(InventoryUnitIdentityOwnership.NormalizedValue)
            }) && x.PrincipalKey.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(InventoryUnitIdentityClaim.Id), nameof(InventoryUnitIdentityClaim.NormalizedValue)
            }));
    }

    private static EdgeRetailsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql("Host=localhost;Database=tracking_model_test;Username=tracking_model_test")
            .Options;
        return new EdgeRetailsDbContext(options);
    }
}
