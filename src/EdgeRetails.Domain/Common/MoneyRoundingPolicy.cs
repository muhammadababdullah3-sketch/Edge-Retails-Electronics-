namespace EdgeRetails.Domain.Common;

public static class MoneyRoundingPolicy
{
    public static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    // Caller order is the stable order of the frozen business input. Allocate
    // cumulative shares so early rounding cannot leave a negative final share.
    public static IReadOnlyList<decimal> Allocate(decimal total, IReadOnlyList<decimal> weights)
    {
        total = Round(total);
        if (total < 0m || weights.Count == 0 || weights.Any(x => x < 0m))
        {
            throw new ArgumentException("Allocation requires a nonnegative total and nonnegative weights.");
        }
        var sum = weights.Sum();
        if (sum == 0m)
        {
            if (total != 0m)
            {
                throw new ArgumentException("A positive allocation requires positive weight.");
            }
            return new decimal[weights.Count];
        }
        var result = new decimal[weights.Count];
        decimal cumulativeWeight = 0m, allocated = 0m;
        for (var i = 0; i < weights.Count; i++)
        {
            cumulativeWeight += weights[i];
            var cumulativeAmount = i == weights.Count - 1 ? total : Round(total * (cumulativeWeight / sum));
            result[i] = cumulativeAmount - allocated;
            allocated = cumulativeAmount;
        }
        return result;
    }
}
