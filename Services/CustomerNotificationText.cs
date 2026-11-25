using System.Text.RegularExpressions;

namespace QabayelMall.API.Services;

public static class CustomerNotificationText
{
    public const string NameToken = "{CustomerName}";

    public static string HidePromoCodes(string text) =>
        Regex.Replace(text, @"\bGxPrv\d+\b", "[private barcode]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string ForCustomer(string text, string? customerName) =>
        HidePromoCodes(text.Replace(NameToken,
            string.IsNullOrWhiteSpace(customerName) ? "Customer" : customerName.Trim(),
            StringComparison.OrdinalIgnoreCase));
}
