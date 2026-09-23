using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Catalog;

public sealed class SupplierProduct : Entity
{
    public Guid SupplierId { get; set; }
    public Guid ProductId { get; set; }
    public long NextItemSequence { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class SupplierCodeSequence
{
    public string Prefix { get; set; } = string.Empty;
    public long NextValue { get; set; } = 1;
}

public static class TraceabilityCodeRules
{
    public static string NormalizeSku(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length == 0)
        {
            throw new BusinessRuleException("catalog.sku_required", "SKU is required.");
        }

        if (normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new BusinessRuleException(
                "catalog.sku_invalid",
                "SKU may contain only A-Z, 0-9, dash, underscore, or dot.");
        }

        return normalized;
    }

    public static string DeriveDealerPrefix(string? supplierName, string? explicitPrefix = null)
    {
        var letters = (supplierName ?? string.Empty)
            .Where(char.IsAsciiLetter)
            .Select(char.ToUpperInvariant)
            .Take(2)
            .ToArray();

        if (letters.Length == 2)
        {
            return new string(letters);
        }

        if (string.IsNullOrWhiteSpace(explicitPrefix))
        {
            throw new BusinessRuleException(
                "parties.dealer_prefix_required",
                "Supplier name must contain at least two Latin letters, or an explicit two-letter dealer prefix (A-Z) must be provided.");
        }

        var normalizedExplicit = explicitPrefix.Trim().ToUpperInvariant();
        if (normalizedExplicit.Length != 2 || !normalizedExplicit.All(char.IsAsciiLetter))
        {
            throw new BusinessRuleException(
                "parties.dealer_prefix_invalid",
                "Explicit dealer prefix must contain exactly two Latin letters (A-Z).");
        }

        return normalizedExplicit;
    }

    public static string BuildDealerCode(string prefix, long sequence)
    {
        if (sequence < 1)
        {
            throw new BusinessRuleException("parties.dealer_sequence_invalid", "Dealer sequence must be positive.");
        }

        var normalizedPrefix = prefix.Trim().ToUpperInvariant();
        if (normalizedPrefix.Length != 2 || !normalizedPrefix.All(char.IsAsciiLetter))
        {
            throw new BusinessRuleException(
                "parties.dealer_prefix_invalid",
                "Dealer prefix must contain exactly two Latin letters (A-Z).");
        }

        return $"{normalizedPrefix}{sequence}";
    }

    public static string BuildTrackingCode(string dealerCode, string sku, long itemSequence)
    {
        if (itemSequence < 1)
        {
            throw new BusinessRuleException("inventory.item_sequence_invalid", "Item sequence must be positive.");
        }

        return $"{dealerCode.Trim().ToUpperInvariant()}-{NormalizeSku(sku)}-{itemSequence.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)}";
    }
}

public static class IdentityNormalizationRules
{
    public static string NormalizeSerialNumber(string rawSerial)
    {
        if (string.IsNullOrWhiteSpace(rawSerial))
        {
            throw new BusinessRuleException("identity.serial_required", "Serial number cannot be empty.");
        }

        return rawSerial.Trim().ToUpperInvariant();
    }

    public static string NormalizeImei(string rawImei)
    {
        if (string.IsNullOrWhiteSpace(rawImei))
        {
            throw new BusinessRuleException("identity.imei_required", "IMEI cannot be empty.");
        }

        var digitsOnly = new string(rawImei.Where(char.IsDigit).ToArray());
        if (digitsOnly.Length is not (14 or 15))
        {
            throw new BusinessRuleException(
                "identity.imei_invalid_length",
                "IMEI must contain exactly 14 or 15 digits.");
        }

        if (digitsOnly.Length == 15 && !ValidateLuhn(digitsOnly))
        {
            throw new BusinessRuleException(
                "identity.imei_checksum_invalid",
                "IMEI Luhn checksum validation failed.");
        }

        return digitsOnly;
    }

    public static bool ValidateLuhn(string number)
    {
        if (string.IsNullOrWhiteSpace(number) || !number.All(char.IsDigit))
        {
            return false;
        }

        int sum = 0;
        bool alternate = false;
        for (int i = number.Length - 1; i >= 0; i--)
        {
            int n = number[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9)
                {
                    n -= 9;
                }
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    public static string NormalizeBarcode(string rawBarcode)
    {
        if (string.IsNullOrWhiteSpace(rawBarcode))
        {
            throw new BusinessRuleException("identity.barcode_required", "Barcode cannot be empty.");
        }

        return rawBarcode.Trim().ToUpperInvariant();
    }
}

public enum ScannerResolutionNamespace
{
    TrackingCode = 1,
    ManufacturerSerialOrImei = 2,
    ProductUnitBarcode = 3,
    ProductBarcode = 4,
    BroaderSearch = 5
}

