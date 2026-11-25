using System.ComponentModel.DataAnnotations;

namespace MyQabayel.Admin.Models;

public class Campaign
{
    public long Id { get; set; }
    [Required, MaxLength(100)] public string GroupCode { get; set; } = "";
    [Required, MaxLength(20)] public string CampaignType { get; set; } = "Flyer";
    [Required, MaxLength(250)] public string Title { get; set; } = string.Empty;
    [MaxLength(150)] public string? Header { get; set; }
    public string? Body { get; set; }
    [Required] public string ImageUrl { get; set; } = string.Empty;
    public DateTime StartDateUtc { get; set; }
    public DateTime EndDateUtc { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "Draft";
    public int? ErpBranchId { get; set; }
    public int AudienceCondition { get; set; } = 1;
    public int? RecentDays { get; set; }
    public int DisplaySeconds { get; set; } = 30;
    public bool AllowSkip { get; set; } = true;
    public long? ErpRecordId { get; set; }
    [MaxLength(40)] public string PublishStatus { get; set; } = "NotPublished";
    public DateTime? PublishedAtUtc { get; set; }
    [MaxLength(1000)] public string? LastPublishError { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<CampaignImage> Images { get; set; } = new List<CampaignImage>();
}

public class CampaignImage
{
    public long Id { get; set; }
    public long CampaignId { get; set; }
    [Required, MaxLength(1000)] public string ImageUrl { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public Campaign Campaign { get; set; } = null!;
}

public class CampaignCreateViewModel
{
    [Required, MaxLength(250)] public string Title { get; set; } = string.Empty;
    [Required] public string CampaignType { get; set; } = "Flyer";
    public string? Header { get; set; }
    public string? Body { get; set; }
    [Required, DataType(DataType.Date)] public DateTime StartDate { get; set; } = DateTime.Today;
    [Required, DataType(DataType.Date)] public DateTime EndDate { get; set; } = DateTime.Today.AddDays(7);
    public int AudienceCondition { get; set; } = 1;
    [Range(1, 365)] public int? RecentDays { get; set; } = 30;
    public int? ErpBranchId { get; set; }
    [Range(1, 300)] public int DisplaySeconds { get; set; } = 30;
    public bool AllowSkip { get; set; } = true;
    [Required] public List<IFormFile> Images { get; set; } = [];
    public bool PublishNow { get; set; }
}

public class CampaignEditViewModel
{
    public long Id { get; set; }
    [Required, MaxLength(250)] public string Title { get; set; } = string.Empty;
    [Required] public string CampaignType { get; set; } = "Flyer";
    public string? Header { get; set; }
    public string? Body { get; set; }
    [Required, DataType(DataType.Date)] public DateTime StartDate { get; set; }
    [Required, DataType(DataType.Date)] public DateTime EndDate { get; set; }
    public int AudienceCondition { get; set; } = 1;
    [Range(1, 365)] public int? RecentDays { get; set; }
    public int? ErpBranchId { get; set; }
    [Range(1, 300)] public int DisplaySeconds { get; set; } = 30;
    public bool AllowSkip { get; set; } = true;
    // Optional on edit: existing campaign images remain when no new files are selected.
    public List<IFormFile>? NewImages { get; set; }
    public IReadOnlyList<CampaignImage> ExistingImages { get; set; } = [];
}

public record CustomerSummary(
    long Id,
    string Name,
    string? MobileNo,
    string? Email,
    string? CardNumber,
    decimal Points,
    decimal OpeningPoints = 0,
    decimal RedeemedOpeningPoints = 0,
    DateTime? OpeningDate = null,
    DateTime? RegisteredAt = null,
    bool Active = true,
    bool ShowFlyers = true,
    bool ShowOffers = true,
    DateTime? LastSeenAtUtc = null)
{
    public decimal NetOpeningPoints => OpeningPoints - RedeemedOpeningPoints;
    public decimal TotalAvailablePoints => Points + NetOpeningPoints;
}
