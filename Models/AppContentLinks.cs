using System.ComponentModel.DataAnnotations;

namespace QabayelMall.API.Models;

public sealed class AppContentLinks
{
    [Key, MaxLength(100)] public string GroupCode { get; set; } = "";
    [Required, MaxLength(2048)] public string TermsAndConditionsWebViewUrl { get; set; } = "";
    [Required, MaxLength(2048)] public string PrivacyPolicyWebViewUrl { get; set; } = "";
    [Required, MaxLength(2048)] public string AboutWebViewUrl { get; set; } = "";
    [Required, MaxLength(2048)] public string FaqLink { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AppContentLinksViewModel
{
    public string GroupCode { get; set; } = "";

    [Required, MaxLength(2048), Display(Name = "Terms and conditions URL")]
    public string TermsAndConditionsWebViewUrl { get; set; } = "";

    [Required, MaxLength(2048), Display(Name = "Privacy policy URL")]
    public string PrivacyPolicyWebViewUrl { get; set; } = "";

    [Required, MaxLength(2048), Display(Name = "About URL")]
    public string AboutWebViewUrl { get; set; } = "";

    [Required, MaxLength(2048), Display(Name = "FAQ URL")]
    public string FaqLink { get; set; } = "";
}
