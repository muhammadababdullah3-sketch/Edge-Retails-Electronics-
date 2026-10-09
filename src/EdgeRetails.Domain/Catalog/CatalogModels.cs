using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Catalog;

public enum TrackingMode
{
    Quantity = 1,
    Length = 2,
    Serialized = 3,
    IndividualPiece = 4,
    Container = 5,
    Pack = 5
}

public sealed class Unit : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public int DisplayDecimalPlaces { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Company : Entity
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public long Version { get; set; }
}

public sealed class Category : Entity
{
    public string Name { get; set; } = string.Empty;
    public string IdentitySymbol { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public long Version { get; set; }
}

public sealed class Product : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? ModelCode { get; set; }
    public Guid BaseUnitId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? CompanyId { get; set; }
    public TrackingMode TrackingMode { get; set; } = TrackingMode.Quantity;
    public bool SerialTrackingEnabled { get; set; }
    public bool ImeiTrackingEnabled { get; set; }
    public decimal? ReferencePurchaseCost { get; set; }
    public decimal DefaultSalePrice { get; set; }
    public decimal MinimumStockLevel { get; set; }
    public int DefaultWarrantyMonths { get; set; }
    public string? AttributesJson { get; set; }
    public int AttributesSchemaVersion { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public long Version { get; set; }

    public void ValidateTrackingPolicy()
    {
        if (TrackingMode == TrackingMode.Serialized && !SerialTrackingEnabled && !ImeiTrackingEnabled)
        {
            throw new BusinessRuleException(
                "catalog.serialized_identity_required",
                "Serialized tracking requires serial or IMEI tracking to be enabled.");
        }

        if ((TrackingMode == TrackingMode.Quantity || TrackingMode == TrackingMode.Length) &&
            (SerialTrackingEnabled || ImeiTrackingEnabled))
        {
            throw new BusinessRuleException(
                "catalog.quantity_tracking_invalid_serial_policy",
                "Quantity and length tracking cannot have serial or IMEI tracking enabled.");
        }
    }

    public void ValidateAttributes()
    {
        AttributesPolicy.Validate(AttributesJson, AttributesSchemaVersion);
    }
}

public static class AttributesPolicy
{
    public const int DefaultSchemaVersion = 1;

    public static void Validate(string? attributesJson, int schemaVersion)
    {
        if (schemaVersion < 1)
        {
            throw new BusinessRuleException(
                "catalog.attributes_schema_version_invalid",
                "Attributes schema version must be greater than or equal to 1.");
        }

        if (schemaVersion > 2)
        {
            throw new BusinessRuleException(
                "catalog.attributes_schema_version_unsupported",
                $"Attributes schema version {schemaVersion} is not supported. Supported versions are 1 and 2.");
        }

        if (string.IsNullOrWhiteSpace(attributesJson))
        {
            return;
        }

        var trimmed = attributesJson.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            throw new BusinessRuleException(
                "catalog.attributes_json_invalid",
                "Attributes JSON must be a valid JSON object structure.");
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new BusinessRuleException(
                    "catalog.attributes_json_invalid",
                    "Attributes JSON root element must be an object.");
            }

            // Version 1 is the declared legacy primitive-extension profile. Version 2
            // uses typed optional specifications; neither profile carries business authority.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? profile = null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!seen.Add(prop.Name.Trim()))
                {
                    throw new BusinessRuleException("catalog.attributes_duplicate_key", "Attribute names must be unique.");
                }

                if (schemaVersion == 2 && prop.Name.Trim().Equals("profile", StringComparison.OrdinalIgnoreCase))
                {
                    if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                    {
                        throw new BusinessRuleException("catalog.attributes_type_invalid", "Profile must be a string.");
                    }

                    profile = prop.Value.GetString()?.ToLowerInvariant();
                    if (profile is not ("fan" or "cable" or "bulb" or "general"))
                    {
                        throw new BusinessRuleException("catalog.attributes_profile_invalid", "Unsupported electrical profile.");
                    }
                }
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var key = prop.Name.Trim();
                if (string.IsNullOrWhiteSpace(key) || key.Length > 60 || !System.Text.RegularExpressions.Regex.IsMatch(key, @"^[a-zA-Z0-9_\-]+$"))
                {
                    throw new BusinessRuleException(
                        "catalog.attributes_key_invalid",
                        $"Attribute key '{prop.Name}' is invalid. Keys must be 1-60 alphanumeric, underscore, or hyphen characters.");
                }

                if (prop.Value.ValueKind is System.Text.Json.JsonValueKind.Array or System.Text.Json.JsonValueKind.Object)
                {
                    throw new BusinessRuleException(
                        "catalog.attributes_structure_invalid",
                        $"Attribute '{prop.Name}' cannot be an array or nested object.");
                }

                if (schemaVersion == 2)
                {
                    ValidateSpecification(key.ToLowerInvariant(), prop.Value, profile);
                }
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new BusinessRuleException(
                "catalog.attributes_json_malformed",
                $"Attributes JSON is malformed: {ex.Message}");
        }
    }

    private static void ValidateSpecification(string key, System.Text.Json.JsonElement value, string? profile)
    {
        if (key == "profile")
        {
            return;
        }
        // Explicit extension namespace retains bounded primitive descriptive data.
        // Unprefixed unknown keys are rejected in v2; legacy v1 remains editable.
        if (key.StartsWith("x_", StringComparison.Ordinal))
        {
            return;
        }

        if (key is "color" or "socket_type" or "sockettype" or "insulation" or "brand")
        {
            if (value.ValueKind != System.Text.Json.JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 120)
            {
                throw new BusinessRuleException("catalog.attributes_type_invalid", "Descriptive electrical attributes require a nonempty string of at most 120 characters.");
            }

            if (profile is not null && profile != "general" && (key is "socket_type" or "sockettype") && profile != "bulb")
            {
                throw new BusinessRuleException("catalog.attributes_profile_mismatch", "Socket type applies to the bulb profile.");
            }

            return;
        }
        (decimal Min, decimal Max, bool Whole, string? Profile) specification = key switch
        {
            "wattage" or "ratedwattage" => (0.01m, 100000m, false, (string?)null),
            "voltage" or "ratedvoltage" => (0.01m, 100000m, false, (string?)null),
            "frequency" => (0.01m, 10000m, false, (string?)null),
            "sweep" => (0.01m, 10000m, false, "fan"),
            "blade_count" or "bladecount" => (1m, 100m, true, "fan"),
            "cores" or "corecount" => (1m, 1000m, true, "cable"),
            "roll_length" or "length" => (0.001m, 1000000m, false, "cable"),
            "conductor_cross_section" or "conductorcrosssection" => (0.001m, 10000m, false, "cable"),
            "color_temperature" or "colortemperature" => (1000m, 20000m, true, "bulb"),
            _ => throw new BusinessRuleException("catalog.attributes_key_unknown", "Unknown v2 attribute; descriptive extensions must use the x_ prefix.")
        };
        if (profile is not null && profile != "general" && specification.Profile is not null && profile != specification.Profile)
        {
            throw new BusinessRuleException("catalog.attributes_profile_mismatch", "Attribute does not apply to the selected profile.");
        }

        if (value.ValueKind != System.Text.Json.JsonValueKind.Number)
        {
            throw new BusinessRuleException("catalog.attributes_type_invalid", "Electrical specification must be a JSON number.");
        }

        if (!value.TryGetDecimal(out var number) || number < specification.Min || number > specification.Max ||
            (specification.Whole && !QuantityMath.IsWhole(number)))
        {
            throw new BusinessRuleException("catalog.attributes_range_invalid", "Electrical specification is outside its supported range.");
        }
    }
}

public sealed class ProductUnit : Entity
{
    public Guid ProductId { get; set; }
    public Guid UnitId { get; set; }
    public decimal FactorToBaseUnit { get; set; } = 1m;
    public bool CanPurchase { get; set; }
    public bool CanSell { get; set; }
    public bool CanUseInThaka { get; set; }
    public bool IsDefaultPurchaseUnit { get; set; }
    public bool IsDefaultSaleUnit { get; set; }
    public bool IsActive { get; set; } = true;

    public decimal ToBaseQuantity(decimal enteredQuantity, TrackingMode trackingMode)
    {
        if (enteredQuantity <= 0)
        {
            throw new BusinessRuleException(
                "catalog.quantity_positive",
                "Entered quantity must be greater than zero.");
        }

        if (FactorToBaseUnit <= 0)
        {
            throw new BusinessRuleException(
                "catalog.conversion_factor_positive",
                "Unit conversion factor must be greater than zero.");
        }

        if (trackingMode == TrackingMode.Container)
        {
            if (!QuantityMath.IsWhole(enteredQuantity))
            {
                throw new BusinessRuleException(
                    "catalog.container_quantity_whole",
                    "Container quantity must be an exact whole integer count.");
            }

            if (!QuantityMath.IsWhole(FactorToBaseUnit))
            {
                throw new BusinessRuleException(
                    "catalog.container_conversion_whole",
                    "Container conversion factor must be an exact whole integer count.");
            }
        }

        var exactBaseQuantity = enteredQuantity * FactorToBaseUnit;

        if ((trackingMode == TrackingMode.Serialized || trackingMode == TrackingMode.IndividualPiece || trackingMode == TrackingMode.Container) && !QuantityMath.IsWhole(exactBaseQuantity))
        {
            throw new BusinessRuleException(
                "catalog.serialized_whole_quantity",
                "Tracked product quantity must resolve to an exact whole base quantity before rounding.");
        }

        return (trackingMode == TrackingMode.Serialized || trackingMode == TrackingMode.IndividualPiece || trackingMode == TrackingMode.Container)
            ? exactBaseQuantity
            : QuantityMath.RoundQuantity(exactBaseQuantity);
    }
}

public sealed class ProductUnitBarcode : Entity
{
    public Guid ProductUnitId { get; set; }
    public string Barcode { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public void Normalize()
    {
        Barcode = Barcode.Trim();
        if (Barcode.Length == 0)
        {
            throw new BusinessRuleException("catalog.barcode_required", "Barcode is required.");
        }
    }
}

public sealed record TransactionQuantitySnapshot(
    Guid ProductUnitId,
    decimal EnteredQuantity,
    decimal FactorToBaseSnapshot,
    decimal BaseQuantity)
{
    public static TransactionQuantitySnapshot Create(
        ProductUnit unit,
        decimal enteredQuantity,
        TrackingMode trackingMode)
    {
        return new TransactionQuantitySnapshot(
            unit.Id,
            QuantityMath.RoundQuantity(enteredQuantity),
            QuantityMath.RoundFactor(unit.FactorToBaseUnit),
            unit.ToBaseQuantity(enteredQuantity, trackingMode));
    }
}
