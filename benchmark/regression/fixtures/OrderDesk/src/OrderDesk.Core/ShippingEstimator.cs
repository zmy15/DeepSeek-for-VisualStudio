namespace OrderDesk.Core;

public sealed class ShippingEstimator
{
    public const decimal FreeShippingThreshold = 100m;

    public decimal Calculate(decimal subtotal, string region)
    {
        if (subtotal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subtotal));
        }

        if (subtotal >= FreeShippingThreshold)
        {
            return 0m;
        }

        return string.Equals(region, "remote", StringComparison.OrdinalIgnoreCase)
            ? 12m
            : 8m;
    }
}
