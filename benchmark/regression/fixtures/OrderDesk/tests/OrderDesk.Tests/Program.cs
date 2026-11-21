using OrderDesk.Core;

var checks = new List<(string Name, Action Run)>
{
    ("CustomerFormatter handles normal names", () =>
    {
        AssertEqual("Ada Lovelace", CustomerFormatter.FormatName("Ada", "Lovelace"));
    }),
    ("PricingPolicy combines member and coupon discounts", () =>
    {
        AssertEqual(81m, new PricingPolicy().ApplyDiscounts(100m, true, "save10"));
    }),
    ("ShippingEstimator charges below the threshold", () =>
    {
        AssertEqual(8m, new ShippingEstimator().Calculate(99.99m, "local"));
    }),
    ("ShippingEstimator makes exactly 100 free", () =>
    {
        AssertEqual(0m, new ShippingEstimator().Calculate(100m, "remote"));
    }),
    ("BulkOrderPlanner preserves order and page size", () =>
    {
        Order[] orders =
        [
            CreateOrder("A"),
            CreateOrder("B"),
            CreateOrder("C")
        ];
        IReadOnlyList<Order> page = new BulkOrderPlanner().SelectPage(orders, 2, 2);
        AssertEqual(1, page.Count);
        AssertEqual("C", page[0].Id);
    }),
    ("OrderService combines discount and shipping", () =>
    {
        Order order = CreateOrder("D", subtotal: 100m, isMember: true);
        AssertEqual(98m, new OrderService([order]).CalculateTotal(order));
    }),
    ("Order state transitions remain ordered", () =>
    {
        Order order = CreateOrder("E");
        order.MarkPaid();
        order.MarkShipped();
        AssertEqual(OrderStatus.Shipped, order.Status);
    })
};

var failures = new List<string>();
foreach ((string name, Action run) in checks)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"{checks.Count - failures.Count}/{checks.Count} checks passed.");
if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

return 0;

static Order CreateOrder(
    string id,
    decimal subtotal = 50m,
    bool isMember = false)
{
    return new Order
    {
        Id = id,
        CustomerId = "customer-1",
        Subtotal = subtotal,
        IsMember = isMember
    };
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
