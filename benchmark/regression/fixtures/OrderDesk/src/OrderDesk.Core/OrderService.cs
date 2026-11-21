namespace OrderDesk.Core;

public sealed class OrderService
{
    private readonly Dictionary<string, Order> _orders;
    private readonly PricingPolicy _pricing = new();
    private readonly ShippingEstimator _shipping = new();

    public OrderService(IEnumerable<Order> orders)
    {
        _orders = orders.ToDictionary(order => order.Id, StringComparer.Ordinal);
    }

    public Order? Find(string orderId)
    {
        return _orders.TryGetValue(orderId, out Order? order) ? order : null;
    }

    public decimal CalculateTotal(Order order)
    {
        decimal discounted = _pricing.ApplyDiscounts(
            order.Subtotal,
            order.IsMember,
            order.CouponCode);
        decimal shipping = _shipping.Calculate(discounted, order.Region);
        return discounted + shipping;
    }

    public void Submit(Order order)
    {
        if (string.IsNullOrWhiteSpace(order.CustomerId))
        {
            throw new ArgumentException("Customer id is required.", nameof(order));
        }

        _orders[order.Id] = order;
    }
}
