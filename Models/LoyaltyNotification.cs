using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace QabayelMall.API.Models;

public class LoyaltyNotification
{
    public long Id { get; set; }

    [Required, MaxLength(150)]
    public string Title { get; set; } = "";

    [Required, MaxLength(500)]
    public string Message { get; set; } = "";

    public DateTime ScheduledAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }

    [Required, MaxLength(100)]
    public string GroupCode { get; set; } = "";
    public int CCode { get; set; }
    public int LoyaltyGroupId { get; set; }
    [MaxLength(1000)] public string? ImageUrl { get; set; }
    [Required, MaxLength(20)] public string TargetType { get; set; } = "Notifications";
    public long? TargetId { get; set; }
    [Required, MaxLength(20)]
    public string Environment { get; set; } = "Development";
    [Required, MaxLength(20)]
    public string AudienceType { get; set; } = "All";
    [Required, MaxLength(20)]
    public string DispatchStatus { get; set; } = "Pending";
    public int PushAttemptCount { get; set; }
    public DateTime? LastPushAttemptAtUtc { get; set; }
    public DateTime? PushCompletedAtUtc { get; set; }
    public List<LoyaltyNotificationRecipient> Recipients { get; set; } = [];
    public List<LoyaltyNotificationDelivery> Deliveries { get; set; } = [];
    public List<LoyaltyNotificationImage> Images { get; set; } = [];

    [Required, MaxLength(20)]
    public string Status { get; set; } = "Draft";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class LoyaltyNotificationCreateViewModel
{
    [Required, MaxLength(150)]
    public string Title { get; set; } = "";

    [Required, MaxLength(500), Display(Name = "Short message")]
    public string Message { get; set; } = "";

    [Required, Display(Name = "Date and time")]
    public DateTime ScheduledAt { get; set; } = DateTime.Now;

    [Display(Name = "Expiry date and time")]
    public DateTime? ExpiresAt { get; set; }

    [Display(Name = "Go live at the selected date and time")]
    public bool PublishNow { get; set; } = false;

    [Required, Display(Name = "Firebase environment")]
    public string Environment { get; set; } = "Development";

    [Required, Display(Name = "Recipients")]
    public string AudienceType { get; set; } = "All";

    [Display(Name = "Selected customer IDs (comma separated)")]
    public string? SelectedCustomerIds { get; set; }

    [Display(Name = "Link to a published offer, flyer or advertisement")]
    public long? LinkedCampaignId { get; set; }

    [Display(Name = "Include the linked campaign image")]
    public bool UseLinkedCampaignImage { get; set; }

    [MaxLength(1000), Display(Name = "Image URL (optional)")]
    public string? ImageUrl { get; set; }

    [Display(Name = "Additional images")]
    public List<IFormFile> Images { get; set; } = [];

    public List<SelectListItem> CampaignOptions { get; set; } = [];
}

public sealed class LoyaltyNotificationResendViewModel
{
    public long Id { get; set; }
    [ValidateNever]
    public string Title { get; set; } = "";
    [ValidateNever]
    public string Message { get; set; } = "";
    [ValidateNever]
    public string Environment { get; set; } = "";
    [ValidateNever]
    public string CurrentAudience { get; set; } = "";

    [Required(ErrorMessage = "Choose all customers or specific customers.")]
    public string AudienceType { get; set; } = "";

    [Display(Name = "Selected customer IDs (comma separated)")]
    public string? SelectedCustomerIds { get; set; }
}

public class LoyaltyNotificationRecipient
{
    public long NotificationId { get; set; }
    public long CustomerId { get; set; }
    public LoyaltyNotification Notification { get; set; } = null!;
}

public class LoyaltyNotificationDelivery
{
    public long NotificationId { get; set; }
    public long DeviceId { get; set; }
    public string Status { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? LastError { get; set; }
    public LoyaltyNotification Notification { get; set; } = null!;
}

public sealed class NotificationDeliverySummary
{
    public long NotificationId { get; set; }
    public int DeviceCount { get; set; }
    public int AcceptedCount { get; set; }
    public int FailedCount { get; set; }
    public int WaitingCount { get; set; }
    public int SkippedCount { get; set; }
}

public class LoyaltyNotificationImage
{
    public long Id { get; set; }
    public long NotificationId { get; set; }
    [Required, MaxLength(1000)] public string ImageUrl { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsPrimary { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public LoyaltyNotification Notification { get; set; } = null!;
}
