namespace OrderDesk.Core;

public enum OrderStatus
{
    Draft,
    Paid,
    Shipped,
    Cancelled
}

public sealed class Order
{
    public required string Id { get; init; }

    public required string CustomerId { get; init; }

    public decimal Subtotal { get; init; }

    public bool IsMember { get; init; }

    public string? CouponCode { get; init; }

    public string Region { get; init; } = "local";

    public OrderStatus Status { get; private set; } = OrderStatus.Draft;

    public void MarkPaid()
    {
        if (Status != OrderStatus.Draft)
        {
            throw new InvalidOperationException("Only draft orders can be paid.");
        }

        Status = OrderStatus.Paid;
    }

    public void MarkShipped()
    {
        if (Status != OrderStatus.Paid)
        {
            throw new InvalidOperationException("Only paid orders can be shipped.");
        }

        Status = OrderStatus.Shipped;
    }
}
