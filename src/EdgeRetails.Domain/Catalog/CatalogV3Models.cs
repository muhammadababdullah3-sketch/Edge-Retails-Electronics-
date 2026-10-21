using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Catalog;

public enum PhysicalDimension
{
    Count = 1,
    Length = 2,
    Mass = 3,
    Volume = 4,
    Area = 5,
    Service = 6,
    Time = 7,
    Any = 8
}

public static class DimensionGatePolicy
{
    public static readonly IReadOnlySet<PhysicalDimension> SupportedDimensions = new HashSet<PhysicalDimension>
    {
        PhysicalDimension.Count,
        PhysicalDimension.Length
    };

    public static readonly IReadOnlySet<PhysicalDimension> GatedDimensions = new HashSet<PhysicalDimension>
    {
        PhysicalDimension.Mass,
        PhysicalDimension.Volume,
        PhysicalDimension.Area
    };

    public static void AssertDimensionSupported(PhysicalDimension dimension)
    {
        if (GatedDimensions.Contains(dimension))
        {
            throw new BusinessRuleException(
                "catalog.dimension_not_supported_gate_d",
                $"Physical dimension '{dimension}' is gated and disabled server-side under Gate D policy until live precision and valuation authority is independently certified.");
        }

        if (!SupportedDimensions.Contains(dimension) && dimension != PhysicalDimension.Service && dimension != PhysicalDimension.Any)
        {
            throw new BusinessRuleException(
                "catalog.dimension_not_supported",
                $"Physical dimension '{dimension}' is not supported.");
        }
    }

    public static void AssertDimensionCompatibility(PhysicalDimension sourceDimension, PhysicalDimension targetDimension)
    {
        if (sourceDimension != targetDimension && sourceDimension != PhysicalDimension.Any && targetDimension != PhysicalDimension.Any)
        {
            throw new BusinessRuleException(
                "catalog.cross_dimension_conversion_forbidden",
                $"Cross-dimension conversion from '{sourceDimension}' to '{targetDimension}' is prohibited. Conversions must occur strictly within the same physical dimension.");
        }
    }

    public static void ValidateQuantityForDimension(decimal quantity, PhysicalDimension dimension)
    {
        if (quantity <= 0)
        {
            throw new BusinessRuleException("catalog.quantity_positive", "Quantity must be greater than zero.");
        }

        if (dimension == PhysicalDimension.Count && !QuantityMath.IsWhole(quantity))
        {
            throw new BusinessRuleException(
                "catalog.count_quantity_whole",
                "Count dimension quantity must be an exact whole integer count.");
        }
    }
}

public sealed record Division(
    string Code,
    string Name);

public sealed record Department(
    string Code,
    string Name,
    string DivisionCode,
    string DivisionName,
    int V3ActiveLeafCount,
    int V3DeprecatedCount,
    int V3NavigationOnlyCount);

public sealed record ProductClass(
    string ClassRef,
    string ClassName,
    string DivisionCode,
    string DepartmentCode,
    string ClassType,
    string ClassStatus,
    string CatalogRole,
    string? CanonicalClassRef = null,
    string? ClassificationRule = null)
{
    public bool IsActiveLeaf => ClassStatus == "ACTIVE_PROPOSAL" && CatalogRole == "LEAF";
    public bool IsRedirect => ClassStatus == "DEPRECATED_REDIRECT";
    public bool IsNavigationOnly => ClassStatus == "NON_SELLABLE_PARENT" || CatalogRole == "NAVIGATION_ONLY";
}

public sealed record ClassRedirect(
    string LegacyClassRef,
    string LegacyLabel,
    string CanonicalClassRef,
    string CanonicalLabel,
    string Reason,
    string MigrationPolicy,
    string Status);

public sealed record UnitDefinition(
    string LocalCode,
    string Name,
    PhysicalDimension Dimension,
    decimal FactorToReference,
    string ConversionKind,
    string? VerifiedUneceRec20Code,
    bool IsActiveSupported);

public sealed record AttributeProfile(
    string ProfileCode,
    string DepartmentCode,
    string AttributeKey,
    string AttributeName,
    string ValueType,
    string? UnitSymbol,
    string? EnumSuggestedValues,
    string? NumericMin,
    string? NumericMax,
    string? VariantRole,
    string? EffectiveRequiredness)
{
    public bool IsEmptyEnum => ValueType == "ENUM" && string.IsNullOrWhiteSpace(EnumSuggestedValues);
    public bool IsNumericUnbounded => ValueType == "NUMBER" && string.IsNullOrWhiteSpace(NumericMin) && string.IsNullOrWhiteSpace(NumericMax);
}

public sealed record ClassFacetRule(
    string ClassRef,
    string FacetName,
    string ValueType,
    string? SuggestedEnumValues,
    string Applicability,
    string VariantIdentityPolicy,
    string Requiredness);

public sealed record SearchAlias(
    string TargetScope,
    string TargetRef,
    string Keyword,
    string Language,
    string LookupBehavior,
    string AmbiguityPolicy)
{
    public bool IsUnreviewedLanguage => Language == "ROMAN_URDU_OR_ENGLISH_UNREVIEWED";
}

public sealed record AmbiguousSearchTerm(
    string QueryToken,
    IReadOnlyList<string> CandidateScopes,
    string Reason,
    string ResolutionRule);

public sealed class CatalogRedirectEngine
{
    private readonly Dictionary<string, ProductClass> _classes;
    private readonly Dictionary<string, ClassRedirect> _redirects;

    public CatalogRedirectEngine(
        IEnumerable<ProductClass> classes,
        IEnumerable<ClassRedirect> redirects)
    {
        _classes = classes.ToDictionary(c => c.ClassRef, StringComparer.OrdinalIgnoreCase);
        _redirects = redirects.ToDictionary(r => r.LegacyClassRef, StringComparer.OrdinalIgnoreCase);

        ValidateIntegrity();
    }

    private void ValidateIntegrity()
    {
        // 1. Validate target existence and active leaf status
        foreach (var r in _redirects.Values)
        {
            if (!_classes.TryGetValue(r.CanonicalClassRef, out var targetClass))
            {
                throw new BusinessRuleException(
                    "catalog.redirect_target_not_found",
                    $"Redirect target '{r.CanonicalClassRef}' for legacy class '{r.LegacyClassRef}' does not exist in product classes.");
            }

            if (!targetClass.IsActiveLeaf)
            {
                throw new BusinessRuleException(
                    "catalog.redirect_target_not_active_leaf",
                    $"Redirect target '{r.CanonicalClassRef}' for legacy class '{r.LegacyClassRef}' is not an active sellable leaf class.");
            }
        }

        // 2. Validate cycle and chain prevention (fail-closed)
        foreach (var r in _redirects.Values)
        {
            if (_redirects.ContainsKey(r.CanonicalClassRef))
            {
                throw new BusinessRuleException(
                    "catalog.redirect_chain_prohibited",
                    $"Redirect chain detected: legacy class '{r.LegacyClassRef}' points to '{r.CanonicalClassRef}' which is also a redirect.");
            }

            if (string.Equals(r.LegacyClassRef, r.CanonicalClassRef, StringComparison.OrdinalIgnoreCase))
            {
                throw new BusinessRuleException(
                    "catalog.redirect_cycle_detected",
                    $"Redirect cycle detected: legacy class '{r.LegacyClassRef}' points to itself.");
            }
        }
    }

    public ProductClass ResolveClass(string classRef)
    {
        if (string.IsNullOrWhiteSpace(classRef))
        {
            throw new BusinessRuleException("catalog.class_ref_required", "Class reference is required.");
        }

        var normalized = classRef.Trim();
        if (!_classes.TryGetValue(normalized, out var pClass))
        {
            throw new BusinessRuleException(
                "catalog.class_not_found",
                $"Product class '{normalized}' was not found in catalog taxonomy.");
        }

        if (pClass.IsActiveLeaf)
        {
            return pClass;
        }

        if (pClass.IsRedirect)
        {
            if (_redirects.TryGetValue(normalized, out var redirect))
            {
                return _classes[redirect.CanonicalClassRef];
            }

            if (!string.IsNullOrWhiteSpace(pClass.CanonicalClassRef) && _classes.TryGetValue(pClass.CanonicalClassRef, out var directTarget))
            {
                return directTarget;
            }

            throw new BusinessRuleException(
                "catalog.redirect_unresolvable",
                $"Redirect class '{normalized}' has no valid canonical mapping.");
        }

        if (pClass.IsNavigationOnly)
        {
            throw new BusinessRuleException(
                "catalog.class_non_sellable_parent",
                $"Product class '{normalized}' is a navigation-only parent and cannot be used as a sellable product leaf.");
        }

        return pClass;
    }
}

public sealed class CatalogSearchEngine
{
    private readonly CatalogRedirectEngine _redirectEngine;
    private readonly IReadOnlyList<SearchAlias> _aliases;
    private readonly Dictionary<string, AmbiguousSearchTerm> _ambiguousTerms;
    private readonly IReadOnlyList<ProductClass> _classes;

    public CatalogSearchEngine(
        CatalogRedirectEngine redirectEngine,
        IEnumerable<SearchAlias> aliases,
        IEnumerable<AmbiguousSearchTerm> ambiguousTerms,
        IEnumerable<ProductClass> classes)
    {
        _redirectEngine = redirectEngine;
        _aliases = aliases.ToList();
        _ambiguousTerms = ambiguousTerms.ToDictionary(a => a.QueryToken.Trim(), StringComparer.OrdinalIgnoreCase);
        _classes = classes.ToList();
    }

    public SearchResult Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchResult(query, SearchOutcome.EmptyQuery, Array.Empty<string>(), null);
        }

        var token = query.Trim();

        // Check for ambiguous search term (requires contextual disambiguation, fail-closed against auto-pick)
        if (_ambiguousTerms.TryGetValue(token, out var ambig))
        {
            return new SearchResult(
                token,
                SearchOutcome.AmbiguousRequiresSelection,
                ambig.CandidateScopes,
                null,
                ambig.Reason);
        }

        // Direct class ref match
        var directClass = _classes.FirstOrDefault(c => string.Equals(c.ClassRef, token, StringComparison.OrdinalIgnoreCase));
        if (directClass != null)
        {
            var canonical = _redirectEngine.ResolveClass(directClass.ClassRef);
            return new SearchResult(token, SearchOutcome.ExactMatch, new[] { canonical.ClassRef }, canonical);
        }

        // Alias match
        var matchingAliases = _aliases
            .Where(a => string.Equals(a.Keyword, token, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingAliases.Count > 0)
        {
            var resolvedRefs = new List<string>();
            ProductClass? primary = null;

            foreach (var a in matchingAliases)
            {
                try
                {
                    var resolved = _redirectEngine.ResolveClass(a.TargetRef);
                    if (!resolvedRefs.Contains(resolved.ClassRef))
                    {
                        resolvedRefs.Add(resolved.ClassRef);
                        primary ??= resolved;
                    }
                }
                catch (BusinessRuleException)
                {
                    // Target might be department code, etc.
                    if (!resolvedRefs.Contains(a.TargetRef))
                    {
                        resolvedRefs.Add(a.TargetRef);
                    }
                }
            }

            return new SearchResult(
                token,
                resolvedRefs.Count == 1 ? SearchOutcome.ExactMatch : SearchOutcome.CandidateList,
                resolvedRefs,
                resolvedRefs.Count == 1 ? primary : null);
        }

        return new SearchResult(token, SearchOutcome.NotFound, Array.Empty<string>(), null);
    }
}

public enum SearchOutcome
{
    EmptyQuery,
    ExactMatch,
    CandidateList,
    AmbiguousRequiresSelection,
    NotFound
}

public sealed record SearchResult(
    string Query,
    SearchOutcome Outcome,
    IReadOnlyList<string> CandidateReferences,
    ProductClass? ResolvedClass,
    string? DisambiguationNote = null);

public static class AttributeOptionPolicy
{
    public static void ValidateOptionSelection(
        AttributeProfile profile,
        string selectedValue,
        bool isHistoricalRecord)
    {
        if (profile.ValueType != "ENUM")
        {
            return;
        }

        if (profile.IsEmptyEnum)
        {
            if (isHistoricalRecord)
            {
                // Grandfathered: existing historic values preserved
                return;
            }

            // New selection is disabled until catalog owner provides approved enum vocabulary
            throw new BusinessRuleException(
                "catalog.empty_enum_selection_disabled",
                $"Attribute '{profile.AttributeKey}' ({profile.ProfileCode}) has no defined enum options. New selection is disabled under Phase 11 catalog policy until an approved vocabulary is registered.");
        }

        var allowed = (profile.EnumSuggestedValues ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToList();

        if (allowed.Count > 0 && !allowed.Any(a => string.Equals(a, selectedValue.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            if (isHistoricalRecord)
            {
                return; // Grandfather historic unlisted values
            }

            throw new BusinessRuleException(
                "catalog.enum_value_not_permitted",
                $"Value '{selectedValue}' is not a permitted option for attribute '{profile.AttributeKey}'.");
        }
    }
}
