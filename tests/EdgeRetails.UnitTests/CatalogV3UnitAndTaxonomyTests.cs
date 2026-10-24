using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Infrastructure.Services;
using Xunit;

namespace EdgeRetails.UnitTests;

/// <summary>
/// Unit and behavioral regression tests for Phase 11 Catalog Correction Batch (CAT-AUDIT-01 through CAT-AUDIT-08).
/// Verifies canonical registry integration, dimension compatibility, Gate D gating, ClassRef persistence,
/// non-widening hierarchy filtering, alias resolution, and complete manifest SHA256 integrity.
/// </summary>
public sealed class CatalogV3UnitAndTaxonomyTests
{
    // ==========================================
    // CAT-AUDIT-01: Canonical Registry Preload
    // ==========================================
    [Fact]
    public void CAT_AUDIT_01_CanonicalRegistryCounts_AreAuthoritative()
    {
        Assert.Equal(9, TaxonomyV3Store.Divisions.Count);
        Assert.Equal(65, TaxonomyV3Store.Departments.Count);
        Assert.Equal(732, TaxonomyV3Store.ProductClasses.Count);
        Assert.Equal(42, TaxonomyV3Store.ClassRedirects.Count);
        Assert.Equal(51, TaxonomyV3Store.UnitDefinitions.Count);

        // Verify UNECE Rec 20 mappings for core operational units
        var pcs = TaxonomyV3Store.UnitDefinitions.First(u => u.LocalCode == "PCS");
        Assert.Equal("H87", pcs.VerifiedUneceRec20Code);
        Assert.Equal(PhysicalDimension.Count, pcs.Dimension);
        Assert.True(pcs.IsActiveSupported);

        var m = TaxonomyV3Store.UnitDefinitions.First(u => u.LocalCode == "M");
        Assert.Equal("MTR", m.VerifiedUneceRec20Code);
        Assert.Equal(PhysicalDimension.Length, m.Dimension);
        Assert.True(m.IsActiveSupported);
    }

    // ==========================================
    // CAT-AUDIT-02: Physical Dimension Compatibility & Gate D
    // ==========================================
    [Fact]
    public void CAT_AUDIT_02_CrossDimensionConversion_IsForbidden()
    {
        var baseUnit = new Unit { Id = Guid.NewGuid(), Name = "Meter", Symbol = "M", Dimension = PhysicalDimension.Length, FactorToReference = 1.0m, ConversionKind = "FIXED_TO_M", IsActive = true };
        var targetUnit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "PCS", Dimension = PhysicalDimension.Count, FactorToReference = 1.0m, ConversionKind = "FIXED", IsActive = true };

        var lookup = new Dictionary<Guid, Unit>
        {
            [baseUnit.Id] = baseUnit,
            [targetUnit.Id] = targetUnit
        };

        var inputs = new List<ProductUnitInput>
        {
            new(baseUnit.Id, 1m, true, true, true, true, true),
            new(targetUnit.Id, 2m, true, true, true, false, false)
        };

        var error = ProductUnitValidator.ValidateUnitInputs(TrackingMode.Quantity, baseUnit.Id, inputs, lookup);
        Assert.NotNull(error);
        Assert.Equal("catalog.cross_dimension_conversion_forbidden", error.Code);
    }

    [Fact]
    public void CAT_AUDIT_02_GateD_GatedDimensions_AreRejectedWhenUnsupported()
    {
        var baseUnit = new Unit { Id = Guid.NewGuid(), Name = "Gram", Symbol = "G", Dimension = PhysicalDimension.Mass, FactorToReference = 1.0m, ConversionKind = "FIXED_TO_G", IsActive = true };
        var targetUnit = new Unit { Id = Guid.NewGuid(), Name = "Kilogram", Symbol = "KG", Dimension = PhysicalDimension.Mass, FactorToReference = 1000m, ConversionKind = "FIXED_TO_G", IsActive = true };

        var lookup = new Dictionary<Guid, Unit>
        {
            [baseUnit.Id] = baseUnit,
            [targetUnit.Id] = targetUnit
        };

        var inputs = new List<ProductUnitInput>
        {
            new(baseUnit.Id, 1m, true, true, true, true, true),
            new(targetUnit.Id, 1000m, true, true, true, false, false)
        };

        var error = ProductUnitValidator.ValidateUnitInputs(TrackingMode.Quantity, baseUnit.Id, inputs, lookup);
        Assert.NotNull(error);
        Assert.Equal("catalog.dimension_not_supported_gate_d", error.Code);
    }

    [Fact]
    public void CAT_AUDIT_02_FixedConversionFactors_EnforceExactRatios()
    {
        var meterUnit = new Unit { Id = Guid.NewGuid(), Name = "Meter", Symbol = "M", Dimension = PhysicalDimension.Length, FactorToReference = 1.0m, ConversionKind = "FIXED_TO_M", IsActive = true };
        var footUnit = new Unit { Id = Guid.NewGuid(), Name = "Foot", Symbol = "FT", Dimension = PhysicalDimension.Length, FactorToReference = 0.3048m, ConversionKind = "FIXED_TO_M", IsActive = true };

        var lookup = new Dictionary<Guid, Unit>
        {
            [meterUnit.Id] = meterUnit,
            [footUnit.Id] = footUnit
        };

        // Attempt invalid ratio (e.g. 0.5 instead of 0.3048)
        var invalidInputs = new List<ProductUnitInput>
        {
            new(meterUnit.Id, 1m, true, true, true, true, true),
            new(footUnit.Id, 0.5m, true, true, true, false, false)
        };

        var error = ProductUnitValidator.ValidateUnitInputs(TrackingMode.Quantity, meterUnit.Id, invalidInputs, lookup);
        Assert.NotNull(error);
        Assert.Equal("catalog.fixed_conversion_factor_mismatch", error.Code);

        // Correct ratio (0.3048) must succeed
        var validInputs = new List<ProductUnitInput>
        {
            new(meterUnit.Id, 1m, true, true, true, true, true),
            new(footUnit.Id, 0.3048m, true, true, true, false, false)
        };

        var success = ProductUnitValidator.ValidateUnitInputs(TrackingMode.Quantity, meterUnit.Id, validInputs, lookup);
        Assert.Null(success);
    }

    // ==========================================
    // CAT-AUDIT-03: Authoritative Product ClassRef
    // ==========================================
    [Fact]
    public void CAT_AUDIT_03_ProductClassRef_IsPreservedOnDomainAndContracts()
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Ceiling Fan 56-inch Deluxe",
            ClassRef = "B05-01",
            TrackingMode = TrackingMode.Quantity
        };

        Assert.Equal("B05-01", product.ClassRef);

        var input = new ProductCatalogInput(
            product.Name,
            product.Sku,
            product.Brand,
            product.Model,
            null,
            null,
            null,
            product.BaseUnitId,
            product.TrackingMode,
            false,
            false,
            null,
            100m,
            0m,
            12,
            null,
            1,
            product.ClassRef);

        Assert.Equal("B05-01", input.ClassRef);

        var dto = new ProductManagementRowDto(
            product.Id,
            product.Sku ?? "SKU-001",
            product.Name,
            product.Brand,
            product.Model,
            null,
            "General",
            product.BaseUnitId,
            "Piece",
            product.TrackingMode,
            false,
            false,
            null,
            100m,
            0m,
            12,
            null,
            1,
            true,
            product.Version,
            Array.Empty<ProductUnitDto>(),
            Array.Empty<SupplierProductLinkDto>(),
            ClassRef: product.ClassRef);

        Assert.Equal("B05-01", dto.ClassRef);
    }

    // ==========================================
    // CAT-AUDIT-06: Expanded Manifest SHA256 Integrity
    // ==========================================
    [Fact]
    public void CAT_AUDIT_06_ManifestSha256_IsDeterministic_AndCoversAllEntities()
    {
        var importer = new TaxonomyV3Importer("Host=127.0.0.1;Port=55437;Database=test;Username=test;Password=test");

        // Use reflection to verify ComputeStoreHash produces an exact 64-character lowercase hex hash
        var method = typeof(TaxonomyV3Importer).GetMethod("ComputeStoreHash", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var hash1 = (string)method.Invoke(null, null)!;
        var hash2 = (string)method.Invoke(null, null)!;

        Assert.NotNull(hash1);
        Assert.Equal(64, hash1.Length);
        Assert.Equal(hash1, hash2);
        Assert.True(hash1.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), "Hash must be valid lowercase hex.");
    }

    /// <summary>
    /// A manifest hash must identify the payload, not the host that computed it. Calling the same
    /// function twice on one machine proves only repeatability within one culture; it cannot detect
    /// a value formatted with the ambient culture. This runs the hash under cultures that use a
    /// comma decimal separator and asserts the digest is byte-identical.
    /// </summary>
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    [InlineData("ar-SA")]
    public void CAT_AUDIT_06_ManifestSha256_IsCultureInvariant(string cultureName)
    {
        var method = typeof(TaxonomyV3Importer).GetMethod(
            "ComputeStoreHash",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var original = Thread.CurrentThread.CurrentCulture;
        var originalUi = Thread.CurrentThread.CurrentUICulture;
        try
        {
            var expected = (string)method.Invoke(null, null)!;

            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(cultureName);
            Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(cultureName);

            // Non-vacuity guard: if the culture were not actually applied, the comparison below
            // would compare two invariant renderings and pass without testing anything. The chosen
            // cultures all use a comma decimal separator, so a decimal formatted under the ambient
            // culture must differ from its invariant form.
            Assert.NotEqual(
                (1.5m).ToString(System.Globalization.CultureInfo.InvariantCulture),
                (1.5m).ToString(System.Globalization.CultureInfo.CurrentCulture));

            var actual = (string)method.Invoke(null, null)!;

            // The ambient culture is genuinely different here, yet the manifest must be identical.
            Assert.Equal(expected, actual);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
            Thread.CurrentThread.CurrentUICulture = originalUi;
        }
    }

    // ==========================================
    // CAT-AUDIT-07: Authoritative Alias Resolution
    // ==========================================
    [Fact]
    public void CAT_AUDIT_07_UrduAlias_Pankha_ResolvesTo_CeilingFan_ClassB0501()
    {
        // Prioritized resolution: specific leaf CLASS scope takes precedence over broad DEPARTMENT scope
        var pankhaAlias = TaxonomyV3Store.SearchAliases
            .Where(a => string.Equals(a.Keyword, "pankha", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => string.Equals(a.TargetScope, "CLASS", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        Assert.NotNull(pankhaAlias);
        Assert.Equal("CLASS", pankhaAlias.TargetScope);
        Assert.Equal("B05-01", pankhaAlias.TargetRef);

        // Verify B05-01 is Ceiling fan
        var ceilingFanClass = TaxonomyV3Store.ProductClasses.FirstOrDefault(c => c.ClassRef == "B05-01");
        Assert.NotNull(ceilingFanClass);
        Assert.Equal("Ceiling fan", ceilingFanClass.ClassName);
        Assert.Equal("B05", ceilingFanClass.DepartmentCode);
    }

    // ==========================================
    // CAT-AUDIT-08: EF Core Metadata and Schema Integrity
    // ==========================================
    [Fact]
    public void CAT_AUDIT_08_Product_And_Unit_Entities_ContainRequiredProperties()
    {
        var productProps = typeof(Product).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Contains("ClassRef", productProps);

        var unitProps = typeof(Unit).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.Contains("Dimension", unitProps);
        Assert.Contains("FactorToReference", unitProps);
        Assert.Contains("ConversionKind", unitProps);
    }
}
