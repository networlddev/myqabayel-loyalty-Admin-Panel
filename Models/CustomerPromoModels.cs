using System.ComponentModel.DataAnnotations;

namespace MyQabayel.Admin.Models;

public sealed record PromoDepartment(int Id, string Name, string Code);
public sealed record PromoCustomer(long Id, string Name, string MobileNo);

public sealed record PromoMasterSummary(
    int Id, string Name, string Caption, string? Body, string CampaignCode,
    DateTime Validity, int ActiveDepartmentCount, int IssuedCount);

public sealed record PromoIssuedCode(
    string Code, long VoucherNo, long CustomerId, string CustomerName,
    string? MobileNo, decimal Amount, int Percentage, DateTime Expiry,
    int DetailUsed, bool VoucherUsed, long? InvoiceNo, decimal UsedAmount)
{
    public string Status => DetailUsed == 2 ? "Revoked"
        : DetailUsed == 1 || VoucherUsed ? "Redeemed"
        : Expiry < DateTime.Now ? "Expired" : "Issued";
}

public sealed class PromoCreateViewModel
{
    public int? DraftId { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [Required, StringLength(500)] public string Caption { get; set; } = "";
    [StringLength(300)] public string? Body { get; set; }
    [Required, StringLength(230)] public string CampaignCode { get; set; } = "";
    [Required, DataType(DataType.Date)] public DateTime ValidityDate { get; set; } = DateTime.Today.AddDays(7);
    public List<int> DepartmentIds { get; set; } = [];
    public IReadOnlyList<PromoDepartment> AvailableDepartments { get; set; } = [];
    public string AudienceType { get; set; } = "Selected";
    public string? SelectedCustomerIds { get; set; }
    public string DiscountType { get; set; } = "Amount";
    public decimal? DiscountValue { get; set; }
    public bool SendNotification { get; set; }
    public string NotificationEnvironment { get; set; } = "Development";
    [StringLength(150)] public string NotificationTitle { get; set; } = "Your PromoCode is ready";
    [StringLength(500)] public string NotificationMessage { get; set; } =
        "Hi {name}, your promo gives {discount}. Valid through {expiry}. Open the app to view your barcode.";
    public IFormFile? NotificationImageUpload { get; set; }
    public string? NotificationImageUrl { get; set; }
    public string? NotificationImageHttpsUrl { get; set; }
    public bool RemoveNotificationImage { get; set; }
    [StringLength(1000)] public string? ImageUrl { get; set; }
    public IFormFile? ImageUpload { get; set; }
    public bool RemoveImage { get; set; }
}

public sealed record PromoCreateDraftSummary(int Id, string Name, string CampaignCode,
    DateTime UpdatedAtUtc);

public sealed class PromoIssueViewModel
{
    public int PromoId { get; set; }
    [Range(1, long.MaxValue)] public long CustomerId { get; set; }
    [Required] public string DiscountType { get; set; } = "Amount";
    [Range(typeof(decimal), "0.01", "99999999.99")]
    public decimal Value { get; set; }
    [Display(Name = "Notify this customer in the app after issuing the code")]
    public bool SendNotification { get; set; }
    [Display(Name = "App environment")]
    public string NotificationEnvironment { get; set; } = "Production";
    public IFormFile? NotificationImageUpload { get; set; }
    public string? CampaignImageUrl { get; set; }
    public string? NotificationImageHttpsUrl { get; set; }
    public PromoMasterSummary? Promo { get; set; }
    public PromoCustomer? Customer { get; set; }
}

public sealed class PromoEditViewModel
{
    public int Id { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [Required, StringLength(500)] public string Caption { get; set; } = "";
    [StringLength(300)] public string? Body { get; set; }
    public string CampaignCode { get; set; } = "";
    [Required, DataType(DataType.Date)] public DateTime ValidityDate { get; set; }
    public List<int> DepartmentIds { get; set; } = [];
    public IReadOnlyList<PromoDepartment> AvailableDepartments { get; set; } = [];
    public bool CanEditDepartments { get; set; }
    public string DiscountType { get; set; } = "Amount";
    public decimal? DiscountValue { get; set; }
    public int UnusedCount { get; set; }
    public IReadOnlyList<PromoIssuedCode> UnchangedCodes { get; set; } = [];
    public IReadOnlyList<PromoIssuedCode> Codes { get; set; } = [];
    public string CustomerChangesJson { get; set; } = "{}";
    public string OriginalAssignmentsJson { get; set; } = "{}";
    public DateTime? DraftSavedAtUtc { get; set; }
    public string? ImageUrl { get; set; }
    public IFormFile? ImageUpload { get; set; }
    public bool RemoveImage { get; set; }
    public bool SendNotificationAgain { get; set; }
    public string NotificationEnvironment { get; set; } = "Development";
    public IFormFile? NotificationImageUpload { get; set; }
    public string? NotificationImageUrl { get; set; }
    public string? NotificationImageHttpsUrl { get; set; }
    public bool RemoveNotificationImage { get; set; }
}

public sealed record PromoEditResult(int UpdatedUnusedCount, int RedeemedCount,
    int RevokedCount, int ReassignedCount = 0, int SkippedUsedCount = 0);

public sealed record PromoEditDraft(
    string Name, string Caption, string? Body, DateTime ValidityDate,
    string DiscountType, decimal? DiscountValue, int[] DepartmentIds,
    Dictionary<string, long> CustomerChanges,
    Dictionary<string, long> OriginalAssignments,
    string? ImageUrl = null,
    bool SendNotificationAgain = false,
    string NotificationEnvironment = "Development",
    string? NotificationImageUrl = null);

public sealed class PromoDetailsViewModel
{
    public PromoMasterSummary Promo { get; set; } = null!;
    public IReadOnlyList<PromoDepartment> Departments { get; set; } = [];
    public IReadOnlyList<PromoIssuedCode> Codes { get; set; } = [];
    public string? ImageUrl { get; set; }
}

public sealed class PromoPrintViewModel
{
    public PromoMasterSummary Promo { get; set; } = null!;
    public PromoIssuedCode Code { get; set; } = null!;
    public string BarcodeSvg { get; set; } = "";
    public string? CampaignImageUrl { get; set; }
}
