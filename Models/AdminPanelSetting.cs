using System.ComponentModel.DataAnnotations;

namespace MyQabayel.Admin.Models;

public sealed class AdminPanelSetting
{
    public int Id { get; set; } = 1;
    [Required, MaxLength(100)] public string GroupCode { get; set; } = "";
    [Required, MaxLength(20)] public string PushEnvironment { get; set; } = "Development";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LoyaltyGroupSettingsViewModel
{
    [Required, MaxLength(100), Display(Name = "Loyalty application GroupCode")]
    [RegularExpression("^[A-Za-z0-9_]+$", ErrorMessage = "Use letters, numbers, and underscores only.")]
    public string GroupCode { get; set; } = "";
    [Required, Display(Name = "Currency for fixed discounts")]
    [RegularExpression("^(AED|OMR|QAR)$", ErrorMessage = "Choose AED, OMR, or QAR.")]
    public string CurrencyCode { get; set; } = "AED";
    [Required, Display(Name = "App environment")]
    [RegularExpression("^(Development|Production)$", ErrorMessage = "Choose Development or Production.")]
    public string PushEnvironment { get; set; } = "Development";
    public int? CCode { get; set; }
    public int? LoyaltyGroupId { get; set; }
    public string GroupStatus { get; set; } = "Unchecked";
    public string GroupStatusMessage { get; set; } = "Check the GroupCode to confirm its status.";
}
