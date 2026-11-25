using System.Globalization;
using Microsoft.EntityFrameworkCore;
using MyQabayel.Admin.Data;

namespace MyQabayel.Admin.Services;

public sealed class LoyaltyCurrencyService(AdminDbContext db)
{
    public static bool IsSupported(string? code) => code is "AED" or "OMR" or "QAR";

    public async Task<string> GetCodeAsync(string groupCode, CancellationToken token)
    {
        string? code = await db.LoyaltyCurrencies.AsNoTracking()
            .Where(x => x.GroupCode == groupCode)
            .Select(x => x.CurrencyCode).SingleOrDefaultAsync(token);
        return IsSupported(code) ? code! : "AED";
    }

    public static string FormatAmount(decimal amount, string currencyCode) =>
        $"{currencyCode} {amount.ToString("N2", CultureInfo.InvariantCulture)}";
}
