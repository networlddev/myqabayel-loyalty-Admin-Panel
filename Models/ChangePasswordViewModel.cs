using System.ComponentModel.DataAnnotations;

namespace QabayelMall.API.Models;

public sealed class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password), Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = "";

    [Required, MinLength(8), DataType(DataType.Password), Display(Name = "New password")]
    public string NewPassword { get; set; } = "";

    [Required, DataType(DataType.Password), Compare(nameof(NewPassword)), Display(Name = "Confirm new password")]
    public string ConfirmNewPassword { get; set; } = "";
}
