using Microsoft.AspNetCore.Mvc;
using MyQabayel.Admin.Data;
using MyQabayel.Admin.Models;
using MyQabayel.Admin.Services;

namespace MyQabayel.Admin.Controllers;

public sealed class SettingsController(AdminDbContext db, LoyaltyCustomerAudience audience,
    IConfiguration configuration,
    LoyaltyCurrencyService currencies) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var model = new LoyaltyGroupSettingsViewModel
        {
            GroupCode = groupCode,
            CurrencyCode = await currencies.GetCodeAsync(groupCode, token),
            PushEnvironment = await audience.GetConfiguredPushEnvironmentAsync(token)
        };
        try
        {
            ApplyCheck(model, await audience.CheckGroupCodeAsync(groupCode, token));
        }
        catch (Exception)
        {
            model.GroupStatus = "Unavailable";
            model.GroupStatusMessage = "The configured ERP database could not be checked right now.";
        }
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> CheckGroupCode(string? groupCode, CancellationToken token)
    {
        try
        {
            var check = await audience.CheckGroupCodeAsync(groupCode, token);
            return Json(new
            {
                status = check.Status,
                message = check.Message,
                cCode = check.Group?.CCode,
                loyaltyGroupId = check.Group?.LoyaltyGroupId
            });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "Unavailable",
                message = "The configured ERP database could not be checked right now."
            });
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(LoyaltyGroupSettingsViewModel model, CancellationToken token)
    {
        model.GroupCode = configuration["LoyaltyGroup:GroupCode"]?.Trim().ToUpperInvariant() ?? "QABAYEL";
        model.CurrencyCode = model.CurrencyCode?.Trim().ToUpperInvariant() ?? "";
        model.PushEnvironment = model.PushEnvironment?.Trim() ?? "";
        ModelState.Remove(nameof(model.GroupCode));
        ModelState.Remove(nameof(model.CurrencyCode));
        ModelState.Remove(nameof(model.PushEnvironment));
        if (!TryValidateModel(model))
        {
            model.GroupStatus = "Invalid";
            model.GroupStatusMessage = "Use 1 to 100 letters, numbers, or underscores.";
            return View(model);
        }

        LoyaltyGroupCheckResult check;
        try { check = await audience.CheckGroupCodeAsync(model.GroupCode, token); }
        catch (Exception)
        {
            model.GroupStatus = "Unavailable";
            model.GroupStatusMessage = "The configured ERP database could not be checked right now.";
            ModelState.AddModelError(nameof(model.GroupCode), model.GroupStatusMessage);
            return View(model);
        }
        ApplyCheck(model, check);
        if (!check.IsActive || check.Group is null)
        {
            ModelState.AddModelError(nameof(model.GroupCode), check.Message);
            return View(model);
        }

        var setting = await db.AdminPanelSettings.FindAsync([1], token);
        if (setting is null)
        {
            db.AdminPanelSettings.Add(new AdminPanelSetting
            {
                Id = 1, GroupCode = check.Group.GroupCode,
                PushEnvironment = model.PushEnvironment
            });
        }
        else
        {
            setting.GroupCode = check.Group.GroupCode;
            setting.PushEnvironment = model.PushEnvironment;
            setting.UpdatedAtUtc = DateTime.UtcNow;
        }
        var currency = await db.LoyaltyCurrencies.FindAsync([check.Group.GroupCode], token);
        if (currency is null)
        {
            currency = new LoyaltyCurrencySetting { GroupCode = check.Group.GroupCode };
            db.LoyaltyCurrencies.Add(currency);
        }
        currency.CurrencyCode = model.CurrencyCode;
        currency.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        TempData["Success"] = $"Loyalty group {check.Group.GroupCode} uses {model.CurrencyCode}; app notifications target {model.PushEnvironment}.";
        return RedirectToAction(nameof(Index));
    }

    private static void ApplyCheck(LoyaltyGroupSettingsViewModel model, LoyaltyGroupCheckResult check)
    {
        model.GroupStatus = check.Status;
        model.GroupStatusMessage = check.Message;
        model.CCode = check.Group?.CCode;
        model.LoyaltyGroupId = check.Group?.LoyaltyGroupId;
    }
}
