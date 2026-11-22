using System.Reflection;
using OrderDesk.Core;

string? caseId = args.FirstOrDefault();
if (string.IsNullOrWhiteSpace(caseId))
{
    Console.Error.WriteLine("--case is required.");
    return 2;
}

var checks = new List<(string Name, Action Run)>();
switch (caseId)
{
    case "rgr-if-regular":
        checks.Add(("normal names remain stable", () =>
            AssertEqual("Ada Lovelace", CustomerFormatter.FormatName("Ada", "Lovelace"))));
        checks.Add(("null first name is safe", () =>
            AssertEqual("Lovelace", CustomerFormatter.FormatName(null, "Lovelace"))));
        checks.Add(("null last name is safe", () =>
            AssertEqual("Ada", CustomerFormatter.FormatName("Ada", null))));
        checks.Add(("both names null are safe", () =>
            AssertEqual(string.Empty, CustomerFormatter.FormatName(null, null))));
        break;

    case "rgr-cq-regular":
        checks.Add(("blank segments collapse", () =>
            AssertEqual("Ada Lovelace", CustomerFormatter.FormatName(" Ada ", " Lovelace "))));
        checks.Add(("single blank segment collapses", () =>
            AssertEqual("Ada", CustomerFormatter.FormatName(" ", "Ada"))));
        checks.Add(("all blank returns empty", () =>
            AssertEqual(string.Empty, CustomerFormatter.FormatName(" ", "\t"))));
        break;

    case "rgr-pe-regular":
        checks.Add(("page count zero", () =>
            AssertEqual(0, InvokePageCount(0, 5))));
        checks.Add(("page count exact", () =>
            AssertEqual(2, InvokePageCount(10, 5))));
        checks.Add(("page count partial", () =>
            AssertEqual(3, InvokePageCount(11, 5))));
        checks.Add(("page count rejects zero page size", () =>
            AssertThrows<ArgumentOutOfRangeException>(() => InvokePageCount(10, 0))));
        break;

    case "rgr-cq-boundary":
        checks.Add(("max int page count avoids overflow", () =>
            AssertEqual(429496730, InvokePageCount(int.MaxValue, 5))));
        checks.Add(("page count keeps ordinary boundaries", () =>
        {
            AssertEqual(0, InvokePageCount(0, 5));
            AssertEqual(2, InvokePageCount(10, 5));
            AssertEqual(3, InvokePageCount(11, 5));
        }));
        break;

    case "rgr-sp-regular":
        checks.Add(("valid draft order passes", () =>
            InvokeValidate(CreateOrder("A", "customer-1", 10m, OrderStatus.Draft))));
        checks.Add(("null order is rejected", () =>
            AssertThrows<ArgumentNullException>(() => InvokeValidate(null))));
        checks.Add(("empty identifiers are rejected", () =>
        {
            AssertThrows<ArgumentException>(() =>
                InvokeValidate(CreateOrder("", "customer-1", 10m, OrderStatus.Draft)));
            AssertThrows<ArgumentException>(() =>
                InvokeValidate(CreateOrder("A", "", 10m, OrderStatus.Draft)));
        }));
        checks.Add(("negative amount is rejected", () =>
        {
            AssertThrows<ArgumentException>(() =>
                InvokeValidate(CreateOrder("A", "customer-1", -1m, OrderStatus.Draft)));
        }));
        checks.Add(("non-draft order is rejected", () =>
        {
            AssertThrows<InvalidOperationException>(() =>
                InvokeValidate(CreateOrder("A", "customer-1", 10m, OrderStatus.Paid)));
        }));
        break;

    case "rgr-ec-regular":
        checks.Add(("threshold is 150", () =>
            AssertEqual(150m, ShippingEstimator.FreeShippingThreshold)));
        checks.Add(("149.99 still charges shipping", () =>
            AssertEqual(8m, new ShippingEstimator().Calculate(149.99m, "local"))));
        checks.Add(("150 is free", () =>
            AssertEqual(0m, new ShippingEstimator().Calculate(150m, "remote"))));
        break;

    default:
        Console.Error.WriteLine($"Unknown OrderDesk oracle case: {caseId}");
        return 2;
}

int failed = 0;
foreach ((string name, Action run) in checks)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"{checks.Count - failed}/{checks.Count} oracle checks passed.");
return failed == 0 ? 0 : 1;

static int InvokePageCount(int totalItems, int pageSize)
{
    MethodInfo method = typeof(BulkOrderPlanner).GetMethod("GetPageCount")
        ?? throw new InvalidOperationException("BulkOrderPlanner.GetPageCount was not found.");
    try
    {
        return (int)(method.Invoke(new BulkOrderPlanner(), [totalItems, pageSize])
            ?? throw new InvalidOperationException("GetPageCount returned null."));
    }
    catch (TargetInvocationException ex) when (ex.InnerException != null)
    {
        throw ex.InnerException;
    }
}

static void InvokeValidate(Order? order)
{
    MethodInfo method = typeof(OrderService).GetMethod("ValidateForCheckout")
        ?? throw new InvalidOperationException(
            "OrderService.ValidateForCheckout was not found.");
    try
    {
        method.Invoke(new OrderService([]), [order]);
    }
    catch (TargetInvocationException ex) when (ex.InnerException != null)
    {
        throw ex.InnerException;
    }
}

static Order CreateOrder(
    string id,
    string customerId,
    decimal subtotal,
    OrderStatus status)
{
    var order = new Order
    {
        Id = id,
        CustomerId = customerId,
        Subtotal = subtotal,
    };
    if (status == OrderStatus.Paid)
        order.MarkPaid();
    return order;
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(
        $"Expected {typeof(TException).Name}, but no exception was thrown.");
}
