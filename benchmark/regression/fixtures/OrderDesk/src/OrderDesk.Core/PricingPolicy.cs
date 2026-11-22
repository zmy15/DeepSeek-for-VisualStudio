namespace OrderDesk.Core;

public sealed class PricingPolicy
{
    public decimal ApplyDiscounts(decimal subtotal, bool isMember, string? couponCode)
    {
        if (subtotal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subtotal));
        }

        decimal total = subtotal;
        if (isMember)
        {
            total *= 0.90m;
        }

        if (string.Equals(couponCode, "SAVE10", StringComparison.OrdinalIgnoreCase))
        {
            total *= 0.90m;
        }

        return decimal.Round(total, 2, MidpointRounding.AwayFromZero);
    }
}
