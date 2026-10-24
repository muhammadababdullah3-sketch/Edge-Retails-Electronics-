using System;
using System.Linq;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase11CatalogV3HierarchyTests
{
    [Fact]
    public void TaxonomyStore_ContainsExactlyNineDivisions()
    {
        Assert.Equal(9, TaxonomyV3Store.Divisions.Count);
        var codes = TaxonomyV3Store.Divisions.Select(d => d.Code).OrderBy(c => c).ToList();
        Assert.Equal(new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I" }, codes);
    }

    [Fact]
    public void TaxonomyStore_ContainsExactlySixtyFiveDepartments()
    {
        Assert.Equal(65, TaxonomyV3Store.Departments.Count);
        Assert.All(TaxonomyV3Store.Departments, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Code));
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.Contains(d.DivisionCode, new[] { "A", "B", "C", "D", "E", "F", "G", "H", "I" });
        });
    }

    [Fact]
    public void TaxonomyStore_ContainsExactly732ProductClasses_WithExactCategoryCounts()
    {
        Assert.Equal(732, TaxonomyV3Store.ProductClasses.Count);

        var activeLeaves = TaxonomyV3Store.ProductClasses.Where(c => c.IsActiveLeaf).ToList();
        var redirects = TaxonomyV3Store.ProductClasses.Where(c => c.IsRedirect).ToList();
        var navOnly = TaxonomyV3Store.ProductClasses.Where(c => c.IsNavigationOnly).ToList();

        Assert.Equal(687, activeLeaves.Count);
        Assert.Equal(42, redirects.Count);
        Assert.Equal(3, navOnly.Count);
        Assert.Equal(732, activeLeaves.Count + redirects.Count + navOnly.Count);
    }

    [Fact]
    public void NavigationOnlyParents_AreExactlyThreeAndCannotBeResolvedAsSellableLeaves()
    {
        var navOnly = TaxonomyV3Store.ProductClasses.Where(c => c.IsNavigationOnly).ToList();
        Assert.Equal(3, navOnly.Count);

        var expectedRefs = new[] { "A04-12", "D05-10", "D07-10" };
        var actualRefs = navOnly.Select(c => c.ClassRef).OrderBy(r => r).ToList();
        Assert.Equal(expectedRefs, actualRefs);

        foreach (var parent in navOnly)
        {
            var ex = Assert.Throws<BusinessRuleException>(() =>
                TaxonomyV3Store.RedirectEngine.ResolveClass(parent.ClassRef));
            Assert.Equal("catalog.class_non_sellable_parent", ex.Code);
        }
    }

    [Fact]
    public void ClassRedirects_All42ResolveDeterministicallyToActiveLeaves_WithZeroCyclesOrChains()
    {
        Assert.Equal(42, TaxonomyV3Store.ClassRedirects.Count);

        foreach (var redirect in TaxonomyV3Store.ClassRedirects)
        {
            var resolved = TaxonomyV3Store.RedirectEngine.ResolveClass(redirect.LegacyClassRef);
            Assert.NotNull(resolved);
            Assert.True(resolved.IsActiveLeaf, $"Target '{resolved.ClassRef}' must be an active leaf.");
            Assert.Equal(redirect.CanonicalClassRef, resolved.ClassRef);
            Assert.NotEqual(redirect.LegacyClassRef, resolved.ClassRef);
        }
    }

    [Fact]
    public void SearchEngine_AmbiguousTerms_RequireDisambiguationAndNeverAutoPick()
    {
        Assert.Equal(7, TaxonomyV3Store.AmbiguousTerms.Count);

        foreach (var term in TaxonomyV3Store.AmbiguousTerms)
        {
            var result = TaxonomyV3Store.SearchEngine.Search(term.QueryToken);
            Assert.Equal(SearchOutcome.AmbiguousRequiresSelection, result.Outcome);
            Assert.Null(result.ResolvedClass);
            Assert.NotEmpty(result.CandidateReferences);
            Assert.False(string.IsNullOrWhiteSpace(result.DisambiguationNote));
        }
    }

    [Fact]
    public void SearchEngine_DirectClassRefAndAliases_ResolveCorrectly()
    {
        // Direct active leaf match
        var directResult = TaxonomyV3Store.SearchEngine.Search("A01-01");
        Assert.Equal(SearchOutcome.ExactMatch, directResult.Outcome);
        Assert.NotNull(directResult.ResolvedClass);
        Assert.Equal("A01-01", directResult.ResolvedClass.ClassRef);

        // Redirect match (resolves to canonical)
        var redirectResult = TaxonomyV3Store.SearchEngine.Search("A01-09");
        Assert.Equal(SearchOutcome.ExactMatch, redirectResult.Outcome);
        Assert.NotNull(redirectResult.ResolvedClass);
        Assert.True(redirectResult.ResolvedClass.IsActiveLeaf);
    }

    [Fact]
    public void DimensionGatePolicy_EnforcesGateDPolicy()
    {
        // Supported dimensions
        DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Count);
        DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Length);

        // Gated dimensions throw catalog.dimension_not_supported_gate_d
        var massEx = Assert.Throws<BusinessRuleException>(() =>
            DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Mass));
        Assert.Equal("catalog.dimension_not_supported_gate_d", massEx.Code);

        var volEx = Assert.Throws<BusinessRuleException>(() =>
            DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Volume));
        Assert.Equal("catalog.dimension_not_supported_gate_d", volEx.Code);

        var areaEx = Assert.Throws<BusinessRuleException>(() =>
            DimensionGatePolicy.AssertDimensionSupported(PhysicalDimension.Area));
        Assert.Equal("catalog.dimension_not_supported_gate_d", areaEx.Code);
    }

    [Fact]
    public void DimensionGatePolicy_CrossDimensionConversions_AreProhibited()
    {
        // Prohibited cross-dimension
        var ex = Assert.Throws<BusinessRuleException>(() =>
            DimensionGatePolicy.AssertDimensionCompatibility(PhysicalDimension.Count, PhysicalDimension.Length));
        Assert.Equal("catalog.cross_dimension_conversion_forbidden", ex.Code);

        // Permitted same dimension
        DimensionGatePolicy.AssertDimensionCompatibility(PhysicalDimension.Count, PhysicalDimension.Count);
        DimensionGatePolicy.AssertDimensionCompatibility(PhysicalDimension.Length, PhysicalDimension.Length);
    }

    [Fact]
    public void DimensionGatePolicy_QuantityValidation_EnforcesWholeCounts()
    {
        // Fractional count rejected
        var ex = Assert.Throws<BusinessRuleException>(() =>
            DimensionGatePolicy.ValidateQuantityForDimension(1.5m, PhysicalDimension.Count));
        Assert.Equal("catalog.count_quantity_whole", ex.Code);

        // Whole count accepted
        DimensionGatePolicy.ValidateQuantityForDimension(5.0m, PhysicalDimension.Count);

        // Fractional length accepted
        DimensionGatePolicy.ValidateQuantityForDimension(2.75m, PhysicalDimension.Length);
    }

    [Fact]
    public void AttributeOptionPolicy_EmptyEnum_RejectsNewSelection_PreservesHistorical()
    {
        var emptyEnumProfile = new AttributeProfile(
            "PROF-001", "A01", "special_type", "Special Type", "ENUM", null, null, null, null, null, null);

        Assert.True(emptyEnumProfile.IsEmptyEnum);

        // New selection disabled
        var ex = Assert.Throws<BusinessRuleException>(() =>
            AttributeOptionPolicy.ValidateOptionSelection(emptyEnumProfile, "OptionA", isHistoricalRecord: false));
        Assert.Equal("catalog.empty_enum_selection_disabled", ex.Code);

        // Historical selection grandfathered
        AttributeOptionPolicy.ValidateOptionSelection(emptyEnumProfile, "OptionA", isHistoricalRecord: true);
    }

    [Fact]
    public void AttributeOptionPolicy_DefinedEnum_ValidatesAllowedOptions()
    {
        var definedEnumProfile = new AttributeProfile(
            "PROF-002", "A01", "color", "Color", "ENUM", null, "Red, Blue, Green", null, null, null, null);

        Assert.False(definedEnumProfile.IsEmptyEnum);

        // Permitted options
        AttributeOptionPolicy.ValidateOptionSelection(definedEnumProfile, "Red", isHistoricalRecord: false);
        AttributeOptionPolicy.ValidateOptionSelection(definedEnumProfile, "Blue", isHistoricalRecord: false);

        // Unpermitted option rejected for new items
        var ex = Assert.Throws<BusinessRuleException>(() =>
            AttributeOptionPolicy.ValidateOptionSelection(definedEnumProfile, "Yellow", isHistoricalRecord: false));
        Assert.Equal("catalog.enum_value_not_permitted", ex.Code);

        // Unpermitted option grandfathered for historical records
        AttributeOptionPolicy.ValidateOptionSelection(definedEnumProfile, "Yellow", isHistoricalRecord: true);
    }
}
