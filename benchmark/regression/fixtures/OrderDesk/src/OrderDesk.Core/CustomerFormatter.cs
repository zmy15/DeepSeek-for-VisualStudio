namespace OrderDesk.Core;

public static class CustomerFormatter
{
    public static string FormatName(string? firstName, string? lastName)
    {
        return $"{firstName!.Trim()} {lastName!.Trim()}";
    }
}
