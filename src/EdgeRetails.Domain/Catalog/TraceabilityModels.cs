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

    public static string NormalizeCompanyCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new BusinessRuleException("catalog.company_code_required", "Company code is required.");
        }

        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length != 2 || !normalized.All(char.IsAsciiLetter))
        {
            throw new BusinessRuleException(
                "catalog.company_code_invalid",
                "Company code must contain exactly two Latin letters (A-Z).");
        }

        return normalized;
    }

    public static string SuggestCompanyCode(string companyName, Func<string, bool> isCodeTaken)
    {
        var cleanName = (companyName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            throw new BusinessRuleException("catalog.company_name_required", "Company name is required.");
        }

        var words = cleanName
            .Split(new[] { ' ', '-', '_', '.', '&', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsAsciiLetter).ToArray()))
            .Where(w => w.Length > 0)
            .ToArray();

        if (words.Length == 0)
        {
            throw new BusinessRuleException("catalog.company_name_letters_required", "Company name must contain Latin letters.");
        }

        var candidates = new List<string>();

        // Preferred: Pak Fan -> PK, Pakistan -> PK
        if (string.Equals(words[0], "Pak", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(words[0], "Pakistan", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("PK");
        }
        else if (words[0].Length >= 2)
        {
            // Royal Fan -> RO, Super Asia -> SU
            candidates.Add(words[0].Substring(0, 2).ToUpperInvariant());
        }

        // Multi-word candidates: Pak Electronics -> PE, Pak Home -> PH, Royal Fan -> RF, Super Asia -> SA
        if (words.Length >= 2)
        {
            var firstChar = char.ToUpperInvariant(words[0][0]);
            for (var w = 1; w < words.Length; w++)
            {
                for (var c = 0; c < words[w].Length; c++)
                {
                    var cand = $"{firstChar}{char.ToUpperInvariant(words[w][c])}";
                    if (!candidates.Contains(cand))
                    {
                        candidates.Add(cand);
                    }
                }
            }
        }

        // Intra-word letter pairs
        if (words[0].Length >= 2)
        {
            var firstChar = char.ToUpperInvariant(words[0][0]);
            for (var i = 1; i < words[0].Length; i++)
            {
                var cand = $"{firstChar}{char.ToUpperInvariant(words[0][i])}";
                if (!candidates.Contains(cand))
                {
                    candidates.Add(cand);
                }
            }
        }

        // Systematic fallback A-Z
        var leadChar = char.ToUpperInvariant(words[0][0]);
        for (var ch = 'A'; ch <= 'Z'; ch++)
        {
            var cand = $"{leadChar}{ch}";
            if (!candidates.Contains(cand))
            {
                candidates.Add(cand);
            }
        }

        foreach (var c in candidates)
        {
            if (!isCodeTaken(c))
            {
                return c;
            }
        }

        for (var c1 = 'A'; c1 <= 'Z'; c1++)
        {
            for (var c2 = 'A'; c2 <= 'Z'; c2++)
            {
                var c = $"{c1}{c2}";
                if (!isCodeTaken(c))
                {
                    return c;
                }
            }
        }

        throw new BusinessRuleException("catalog.company_code_exhausted", "Unable to allocate unique 2-letter company code.");
    }

    public static string NormalizeCategorySymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new BusinessRuleException("catalog.category_symbol_required", "Category symbol is required.");
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        if (normalized.Length is < 1 or > 4 ||
            !char.IsAsciiLetter(normalized[0]) ||
            !normalized.All(char.IsAsciiLetterOrDigit))
        {
            throw new BusinessRuleException(
                "catalog.category_symbol_invalid",
                "Category symbol must be 1 to 4 uppercase alphanumeric characters starting with a letter.");
        }

        return normalized;
    }

    public static string SuggestCategorySymbol(string categoryName, Func<string, bool> isSymbolTaken)
    {
        var cleanName = (categoryName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            throw new BusinessRuleException("catalog.category_name_required", "Category name is required.");
        }

        var letters = new string(cleanName.Where(char.IsAsciiLetter).Select(char.ToUpperInvariant).ToArray());
        if (letters.Length == 0)
        {
            throw new BusinessRuleException("catalog.category_name_letters_required", "Category name must contain Latin letters.");
        }

        var candidates = new List<string>
        {
            // Fan -> F, Bulb -> B, Iron -> I
            letters[0].ToString()
        };

        if (letters.Length >= 2)
        {
            candidates.Add(letters.Substring(0, 2));
        }

        // Consonants of word (Fan -> FN)
        var consonants = new List<char> { letters[0] };
        for (var i = 1; i < letters.Length; i++)
        {
            var ch = letters[i];
            if (ch is not ('A' or 'E' or 'I' or 'O' or 'U'))
            {
                consonants.Add(ch);
            }
        }
        if (consonants.Count >= 2)
        {
            var cStr = new string(consonants.Take(3).ToArray());
            if (!candidates.Contains(cStr))
            {
                candidates.Add(cStr);
            }
        }

        // Numeric disambiguation: F1, F2, ...
        for (var d = 1; d <= 9; d++)
        {
            candidates.Add($"{letters[0]}{d}");
        }

        foreach (var c in candidates)
        {
            if (!isSymbolTaken(c))
            {
                return c;
            }
        }

        for (var ch = 'A'; ch <= 'Z'; ch++)
        {
            var c = $"{letters[0]}{ch}";
            if (!isSymbolTaken(c))
            {
                return c;
            }
        }

        throw new BusinessRuleException("catalog.category_symbol_exhausted", "Unable to allocate unique category symbol.");
    }

    public static string NormalizeModelCode(string modelCode)
    {
        if (string.IsNullOrWhiteSpace(modelCode))
        {
            throw new BusinessRuleException("catalog.model_code_required", "Model code is required.");
        }

        var normalized = modelCode.Trim().ToUpperInvariant();
        if (normalized.Length is < 1 or > 20 ||
            !normalized.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
        {
            throw new BusinessRuleException(
                "catalog.model_code_invalid",
                "Model code must be 1 to 20 uppercase alphanumeric characters (hyphens and underscores allowed).");
        }

        return normalized;
    }

    public static string SuggestModelCode(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "01";
        }

        var words = model.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return "01";
        }

        var sb = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            var letters = word.Where(char.IsAsciiLetter).ToArray();
            var digits = word.Where(char.IsAsciiDigit).ToArray();

            if (letters.Length > 0)
            {
                // Consonants extraction: Deluxe -> DLX
                sb.Append(char.ToUpperInvariant(letters[0]));
                for (var i = 1; i < letters.Length; i++)
                {
                    var ch = char.ToUpperInvariant(letters[i]);
                    if (ch is not ('A' or 'E' or 'I' or 'O' or 'U'))
                    {
                        sb.Append(ch);
                    }
                }
            }

            if (digits.Length > 0)
            {
                sb.Append(digits);
            }
        }

        var candidate = sb.ToString();
        if (candidate.Length == 0)
        {
            var alnum = new string(model.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
            candidate = alnum.Length > 0 ? alnum : "01";
        }

        if (candidate.Length > 16)
        {
            candidate = candidate.Substring(0, 16);
        }

        return candidate;
    }

    public static string BuildProductCode(string companyCode, string categorySymbol, string modelCode)
    {
        var normCompany = NormalizeCompanyCode(companyCode);
        var normCategory = NormalizeCategorySymbol(categorySymbol);
        var normModel = NormalizeModelCode(modelCode);

        return $"{normCompany}{normCategory}-{normModel}";
    }

    public static string SuggestProductCode(string? brand, string name, string? model, string? category = null)
    {
        var brandToken = ExtractBrandToken(brand);
        var modelToken = ExtractModelToken(model);
        var nameToken = ExtractNameToken(name, brand);

        string combined;
        if (!string.IsNullOrWhiteSpace(brandToken))
        {
            if (!string.IsNullOrWhiteSpace(nameToken) && !string.IsNullOrWhiteSpace(modelToken))
            {
                combined = $"{brandToken}-{nameToken}{modelToken}";
            }
            else if (!string.IsNullOrWhiteSpace(modelToken))
            {
                combined = $"{brandToken}-{modelToken}";
            }
            else if (!string.IsNullOrWhiteSpace(nameToken))
            {
                combined = $"{brandToken}-{nameToken}";
            }
            else
            {
                combined = brandToken;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(nameToken) && !string.IsNullOrWhiteSpace(modelToken))
            {
                combined = $"{nameToken}-{modelToken}";
            }
            else if (!string.IsNullOrWhiteSpace(nameToken))
            {
                combined = nameToken;
            }
            else if (!string.IsNullOrWhiteSpace(modelToken))
            {
                combined = $"PRD-{modelToken}";
            }
            else
            {
                combined = "PRD-01";
            }
        }

        var sb = new System.Text.StringBuilder();
        foreach (var ch in combined)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch) && sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var result = sb.ToString().Trim('-', '_', '.');
        if (result.Length > 32)
        {
            result = result.Substring(0, 32).TrimEnd('-', '_', '.');
        }

        return result.Length >= 2 ? result : "PRD-01";
    }

    private static string ExtractBrandToken(string? brand)
    {
        if (string.IsNullOrWhiteSpace(brand))
        {
            return string.Empty;
        }

        var words = brand.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return string.Empty;
        }

        if (words.Length >= 2)
        {
            var first = ExtractConsonantsOrPrefix(words[0], 2);
            var rest = string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0])));
            var candidate = (first + rest).ToUpperInvariant();
            return candidate.Length > 6 ? candidate.Substring(0, 6) : candidate;
        }

        return ExtractConsonantsOrPrefix(words[0], 3).ToUpperInvariant();
    }

    private static string ExtractNameToken(string? name, string? brand)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var brandWords = (brand ?? string.Empty)
            .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToUpperInvariant())
            .ToHashSet();

        var words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !brandWords.Contains(w.ToUpperInvariant()))
            .ToArray();

        if (words.Length == 0)
        {
            return string.Empty;
        }

        var baseToken = ExtractConsonantsOrPrefix(words[0], 3).ToUpperInvariant();
        if (words.Length > 1)
        {
            var digitsWord = words.Skip(1).FirstOrDefault(w => w.Any(char.IsAsciiDigit));
            if (digitsWord != null)
            {
                var digits = new string(digitsWord.Where(char.IsAsciiDigit).ToArray());
                if (!string.IsNullOrEmpty(digits))
                {
                    return $"{baseToken}{digits}";
                }
            }
        }

        return baseToken;
    }

    private static string ExtractModelToken(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        var filtered = new string(model.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return filtered.Length > 10 ? filtered.Substring(0, 10).ToUpperInvariant() : filtered.ToUpperInvariant();
    }

    private static string ExtractConsonantsOrPrefix(string word, int maxLen)
    {
        var letters = word.Where(char.IsAsciiLetter).ToArray();
        if (letters.Length == 0)
        {
            var alnum = new string(word.Where(char.IsAsciiLetterOrDigit).ToArray());
            return alnum.Length > maxLen ? alnum.Substring(0, maxLen) : alnum;
        }

        if (letters.Length <= maxLen)
        {
            return new string(letters).ToUpperInvariant();
        }

        if (letters.All(char.IsUpper))
        {
            return new string(letters.Take(maxLen).ToArray());
        }

        var result = new List<char> { char.ToUpperInvariant(letters[0]) };
        for (var i = 1; i < letters.Length && result.Count < maxLen; i++)
        {
            var c = char.ToUpperInvariant(letters[i]);
            if (c is not ('A' or 'E' or 'I' or 'O' or 'U'))
            {
                result.Add(c);
            }
        }

        if (result.Count < maxLen)
        {
            for (var i = 1; i < letters.Length && result.Count < maxLen; i++)
            {
                var c = char.ToUpperInvariant(letters[i]);
                if (!result.Contains(c))
                {
                    result.Add(c);
                }
            }
        }

        return new string(result.ToArray());
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
    BroaderSearch = 5,
    SerialNumber = 6,
    Imei = 7
}

