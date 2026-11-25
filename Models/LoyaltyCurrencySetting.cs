using System.ComponentModel.DataAnnotations;

namespace MyQabayel.Admin.Models;

public sealed class LoyaltyCurrencySetting
{
    [Key, MaxLength(100)] public string GroupCode { get; set; } = "";
    [Required, MaxLength(3)] public string CurrencyCode { get; set; } = "AED";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
