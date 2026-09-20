namespace EdgeRetails.Domain.Common;

public static class QuantityMath
{
    public const int QuantityScale = 6;
    public const int ConversionScale = 9;

    public static decimal RoundQuantity(decimal value) =>
        decimal.Round(value, QuantityScale, MidpointRounding.AwayFromZero);

    public static decimal RoundFactor(decimal value) =>
        decimal.Round(value, ConversionScale, MidpointRounding.AwayFromZero);

    public static bool IsWhole(decimal value) => value == decimal.Truncate(value);
}
