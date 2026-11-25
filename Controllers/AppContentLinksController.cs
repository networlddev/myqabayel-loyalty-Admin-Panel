using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyQabayel.Admin.Data;
using MyQabayel.Admin.Models;
using MyQabayel.Admin.Services;

namespace MyQabayel.Admin.Controllers;

public sealed class AppContentLinksController(AdminDbContext db, LoyaltyCustomerAudience audience) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var links = await db.AppContentLinks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.GroupCode == groupCode, token);
        return View(new AppContentLinksViewModel
        {
            GroupCode = groupCode,
            TermsAndConditionsWebViewUrl = links?.TermsAndConditionsWebViewUrl ?? "",
            PrivacyPolicyWebViewUrl = links?.PrivacyPolicyWebViewUrl ?? "",
            AboutWebViewUrl = links?.AboutWebViewUrl ?? "",
            FaqLink = links?.FaqLink ?? ""
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(AppContentLinksViewModel model, CancellationToken token)
    {
        model.GroupCode = await audience.GetConfiguredGroupCodeAsync(token);
        model.TermsAndConditionsWebViewUrl = model.TermsAndConditionsWebViewUrl?.Trim() ?? "";
        model.PrivacyPolicyWebViewUrl = model.PrivacyPolicyWebViewUrl?.Trim() ?? "";
        model.AboutWebViewUrl = model.AboutWebViewUrl?.Trim() ?? "";
        model.FaqLink = model.FaqLink?.Trim() ?? "";

        ValidateHttpsUrl(model.TermsAndConditionsWebViewUrl, nameof(model.TermsAndConditionsWebViewUrl));
        ValidateHttpsUrl(model.PrivacyPolicyWebViewUrl, nameof(model.PrivacyPolicyWebViewUrl));
        ValidateHttpsUrl(model.AboutWebViewUrl, nameof(model.AboutWebViewUrl));
        ValidateHttpsUrl(model.FaqLink, nameof(model.FaqLink));
        if (!ModelState.IsValid) return View(model);

        var links = await db.AppContentLinks.SingleOrDefaultAsync(x => x.GroupCode == model.GroupCode, token);
        if (links is null)
        {
            links = new AppContentLinks { GroupCode = model.GroupCode };
            db.AppContentLinks.Add(links);
        }
        links.TermsAndConditionsWebViewUrl = model.TermsAndConditionsWebViewUrl;
        links.PrivacyPolicyWebViewUrl = model.PrivacyPolicyWebViewUrl;
        links.AboutWebViewUrl = model.AboutWebViewUrl;
        links.FaqLink = model.FaqLink;
        links.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        TempData["Success"] = "Mobile app links saved.";
        return RedirectToAction(nameof(Index));
    }

    private void ValidateHttpsUrl(string value, string propertyName)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            ModelState.AddModelError(propertyName, "Enter a valid HTTPS URL (up to 2048 characters).");
    }
}
