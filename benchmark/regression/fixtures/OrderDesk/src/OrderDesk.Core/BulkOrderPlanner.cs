namespace OrderDesk.Core;

public sealed class BulkOrderPlanner
{
    public IReadOnlyList<Order> SelectPage(
        IEnumerable<Order> orders,
        int page,
        int pageSize)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        return orders
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();
    }
}
