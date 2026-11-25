using System.ComponentModel.DataAnnotations;

namespace MyQabayel.Admin.Models;

public sealed class FirebaseIosClientConfiguration
{
    [Required, MaxLength(100)] public string GroupCode { get; set; } = "";
    [Required, MaxLength(20)] public string Environment { get; set; } = "Development";
    [Required, MaxLength(120)] public string ProjectId { get; set; } = "";
    [Required, MaxLength(120)] public string AppId { get; set; } = "";
    [Required, MaxLength(30)] public string MessagingSenderId { get; set; } = "";
    [Required, MaxLength(200)] public string ApiKey { get; set; } = "";
    [Required, MaxLength(200)] public string StorageBucket { get; set; } = "";
    [Required, MaxLength(200)] public string IosBundleId { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
