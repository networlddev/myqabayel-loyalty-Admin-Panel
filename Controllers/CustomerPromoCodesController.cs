using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using MyQabayel.Admin.Models;
using MyQabayel.Admin.Services;
using ZXing;
using ZXing.Common;

namespace MyQabayel.Admin.Controllers;

public sealed class CustomerPromoCodesController(
    CustomerPromoService promos, PromoNotificationService promoNotifications,
    LoyaltyCustomerAudience audience, PromoEditDraftService promoDrafts,
    PromoCampaignContentService content, PromoImageStorage images) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken token)
    {
        try
        {
            var group = await audience.ResolveGroupAsync(token);
            ViewData["GroupCode"] = group.GroupCode;
            ViewData["DraftCampaignIds"] = await promoDrafts.ListCampaignIdsAsync(group, token);
            ViewData["CreateDrafts"] = await content.ListDraftsAsync(group, token);
            return View(await promos.ListAsync(group, token));
        }
        catch (Exception)
        {
            ViewData["Error"] = "PromoCode data is unavailable. Check the ZWCORE deployment scripts and connection.";
            return View(Array.Empty<PromoMasterSummary>());
        }
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? draftId, CancellationToken token)
    {
        var model = new PromoCreateViewModel();
        if (draftId.HasValue)
        {
            var group = await audience.ResolveGroupAsync(token);
            var draft = await content.GetDraftAsync(draftId.Value, group, token);
            if (draft is null) return NotFound();
            model = new PromoCreateViewModel
            {
                DraftId = draftId, Name = draft.Name, Caption = draft.Caption,
                Body = draft.Body, CampaignCode = draft.CampaignCode,
                ValidityDate = draft.ValidityDate,
                DepartmentIds = draft.DepartmentIds.ToList(), AudienceType = draft.AudienceType,
                SelectedCustomerIds = draft.SelectedCustomerIds,
                DiscountType = draft.DiscountType, DiscountValue = draft.DiscountValue,
                SendNotification = draft.SendNotification,
                NotificationEnvironment = draft.NotificationEnvironment,
                NotificationTitle = RemoveCodeToken(draft.NotificationTitle ?? "Your PromoCode is ready"),
                NotificationMessage = RemoveCodeToken(draft.NotificationMessage ??
                    "Hi {name}, your promo gives {discount}. Valid through {expiry}. Open the app to view your barcode."),
                ImageUrl = draft.ImageUrl,
                NotificationImageUrl = draft.NotificationImageUrl
            };
        }
        model.NotificationEnvironment = await audience.GetConfiguredPushEnvironmentAsync(token);
        model.AvailableDepartments = await promos.GetActiveDepartmentsAsync(token);
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(13_000_000)]
    public async Task<IActionResult> Create(PromoCreateViewModel model, string saveMode,
        CancellationToken token)
    {
        bool draftMode = saveMode == "Draft";
        model.NotificationEnvironment = await audience.GetConfiguredPushEnvironmentAsync(token);
        ModelState.Remove(nameof(model.NotificationEnvironment));
        if (saveMode is not ("Draft" or "Apply"))
            ModelState.AddModelError(string.Empty, "Choose Save as draft or Create campaign.");
        if (draftMode)
        {
            ModelState.Remove(nameof(model.Name));
            ModelState.Remove(nameof(model.Caption));
            ModelState.Remove(nameof(model.CampaignCode));
            ModelState.Remove(nameof(model.ValidityDate));
        }
        if (model.ImageUpload is { Length: > 5 * 1024 * 1024 })
            ModelState.AddModelError(nameof(model.ImageUpload), "Image must be no larger than 5 MB.");
        if (model.NotificationImageUpload is { Length: > 5 * 1024 * 1024 })
            ModelState.AddModelError(nameof(model.NotificationImageUpload), "Notification image must be no larger than 5 MB.");
        if (!IsValidNotificationImageUrl(model.NotificationImageHttpsUrl))
            ModelState.AddModelError(nameof(model.NotificationImageHttpsUrl), "Use a public HTTPS image URL ending in .jpg, .jpeg, .png or .webp.");
        if (model.NotificationImageUpload is { Length: > 0 } && !string.IsNullOrWhiteSpace(model.NotificationImageHttpsUrl))
            ModelState.AddModelError(nameof(model.NotificationImageHttpsUrl), "Choose either an uploaded image or an HTTPS image URL.");
        if (!draftMode && model.DepartmentIds.Count == 0)
            ModelState.AddModelError(nameof(model.DepartmentIds), "Select at least one active department.");
        if (model.AudienceType is not ("Selected" or "All" or "Purchased30" or "Purchased60"
            or "Purchased90" or "NoPurchase30" or "NoPurchase60" or "NoPurchase90" or "NeverPurchased"))
            ModelState.AddModelError(nameof(model.AudienceType), "Choose a valid customer audience.");
        string[] customerParts = (model.SelectedCustomerIds ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        long[] selectedIds = [];
        if (model.AudienceType == "Selected")
        {
            if (customerParts.Length > 50 || customerParts.Any(x => !long.TryParse(x, out long id) || id <= 0))
                ModelState.AddModelError(nameof(model.SelectedCustomerIds), "Select up to 50 valid customers.");
            else selectedIds = customerParts.Select(long.Parse).Distinct().ToArray();
        }
        bool issueNow = !draftMode && (model.AudienceType != "Selected" || selectedIds.Length > 0);
        if (issueNow)
        {
            if (model.DiscountType is not ("Amount" or "Percentage"))
                ModelState.AddModelError(nameof(model.DiscountType), "Choose a fixed amount or percentage.");
            if (model.DiscountValue is null or <= 0)
                ModelState.AddModelError(nameof(model.DiscountValue), "Enter a positive discount value.");
            else if (model.DiscountType == "Amount" &&
                (model.DiscountValue > 99999999.99m || decimal.Round(model.DiscountValue.Value, 2) != model.DiscountValue))
                ModelState.AddModelError(nameof(model.DiscountValue), "Enter a fixed amount with at most two decimals.");
            else if (model.DiscountType == "Percentage" &&
                (model.DiscountValue > 100 || decimal.Truncate(model.DiscountValue.Value) != model.DiscountValue))
                ModelState.AddModelError(nameof(model.DiscountValue), "Enter a whole-number percentage from 1 to 100.");
        }
        else if (!draftMode && (model.SendNotification || model.DiscountValue.HasValue))
            ModelState.AddModelError(nameof(model.SelectedCustomerIds),
                "Select customers or choose an audience to issue this discount, or clear the discount to save only the campaign.");
        if (model.SendNotification && model.NotificationEnvironment is not ("Development" or "Production"))
            ModelState.AddModelError(nameof(model.NotificationEnvironment), "Choose Development or Production.");
        if (!draftMode && model.SendNotification)
        {
            if (!PromoNotificationService.IsValidTemplate(model.NotificationTitle, 150))
                ModelState.AddModelError(nameof(model.NotificationTitle),
                    "Enter a title up to 150 characters. Use {name}, {discount} or {expiry} as needed.");
            if (!PromoNotificationService.IsValidTemplate(model.NotificationMessage, 500))
                ModelState.AddModelError(nameof(model.NotificationMessage),
                    "Enter a message up to 500 characters after customer details are inserted. Use {name}, {discount} or {expiry}.");
        }
        if (!ModelState.IsValid)
        {
            model.AvailableDepartments = await promos.GetActiveDepartmentsAsync(token);
            return View(model);
        }
        try
        {
            var group = await audience.ResolveGroupAsync(token);
            PromoCreateDraft? previousDraft = model.DraftId.HasValue
                ? await content.GetDraftAsync(model.DraftId.Value, group, token) : null;
            if (model.DraftId.HasValue && previousDraft is null) return NotFound();
            if (model.AudienceType == "Selected" && selectedIds.Length > 0 &&
                !await audience.AreActiveCustomersInGroupAsync(selectedIds, group, token))
            {
                ModelState.AddModelError(nameof(model.SelectedCustomerIds),
                    "One or more selected customers are not active in this loyalty group.");
                model.AvailableDepartments = await promos.GetActiveDepartmentsAsync(token);
                return View(model);
            }
            IReadOnlyList<long> customerIds = draftMode ? [] : model.AudienceType == "Selected"
                ? selectedIds : await ResolveAudienceAsync(model.AudienceType, group, token);
            if (issueNow && customerIds.Count is 0 or > 20_000)
            {
                ModelState.AddModelError(nameof(model.AudienceType), customerIds.Count == 0
                    ? "No active customers match this audience. Choose another audience."
                    : "This audience exceeds 20,000 customers. Narrow the purchase filter before issuing codes.");
                model.AvailableDepartments = await promos.GetActiveDepartmentsAsync(token);
                return View(model);
            }
            string? imageUrl = model.RemoveImage ? null : previousDraft?.ImageUrl;
            if (model.ImageUpload is { Length: > 0 })
                imageUrl = await images.SaveAsync(model.ImageUpload, Request, token);
            string? notificationImageUrl = model.RemoveNotificationImage ? null : previousDraft?.NotificationImageUrl;
            if (model.NotificationImageUpload is { Length: > 0 })
                notificationImageUrl = await images.SaveAsync(model.NotificationImageUpload, Request, token);
            if (!string.IsNullOrWhiteSpace(model.NotificationImageHttpsUrl))
                notificationImageUrl = model.NotificationImageHttpsUrl.Trim();
            if (draftMode)
            {
                int draftId = await content.SaveDraftAsync(model.DraftId,
                    new PromoCreateDraft(model.Name?.Trim() ?? "", model.Caption?.Trim() ?? "", model.Body?.Trim(),
                        model.CampaignCode?.Trim() ?? "", model.ValidityDate, model.DepartmentIds.Distinct().ToArray(),
                        model.AudienceType, model.SelectedCustomerIds, model.DiscountType,
                        model.DiscountValue, model.SendNotification, model.NotificationEnvironment,
                        imageUrl, notificationImageUrl,
                        model.NotificationTitle, model.NotificationMessage), group, token);
                TempData["Success"] = "Promo draft saved. No ERP campaign, codes or notifications were created.";
                return RedirectToAction(nameof(Create), new { draftId });
            }
            int id = await promos.CreateAsync(model, group, token);
            try
            {
                await content.SaveImageUrlAsync(id, imageUrl, group, token);
                if (model.DraftId.HasValue)
                    await content.DeleteDraftAsync(model.DraftId.Value, group, token);
            }
            catch
            {
                TempData["Error"] = "Campaign created in ERP, but its image or draft cleanup could not be saved in Admin DB. Do not submit this campaign again.";
                return RedirectToAction(nameof(Details), new { id });
            }
            TempData["Success"] = "Promo master saved. Issue codes from this campaign at any time.";
            if (issueNow)
            {
                int issuedCount;
                try
                {
                    issuedCount = await promos.IssueAudienceAsync(id, customerIds,
                        model.DiscountType, model.DiscountValue!.Value, group, token);
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"The campaign was saved, but no codes were issued: {UserMessage(ex)}";
                    return RedirectToAction(nameof(Details), new { id });
                }
                TempData["Success"] = $"Promo master saved and {issuedCount} customer code(s) issued.";
                if (model.SendNotification)
                {
                    try
                    {
                        var promo = await promos.GetAsync(id, group, token)
                            ?? throw new InvalidOperationException("The saved campaign could not be loaded.");
                        var customerSet = customerIds.ToHashSet();
                        var issuedCodes = (await promos.GetCodesAsync(id, group, token))
                            .Where(x => customerSet.Contains(x.CustomerId) && x.Status == "Issued")
                            .ToList();
                        if (issuedCodes.Count != issuedCount)
                            throw new InvalidOperationException("Some issued customer codes could not be loaded for notification.");
                        await promoNotifications.QueueCampaignAsync(promo, issuedCodes,
                            group, model.NotificationEnvironment, model.NotificationTitle,
                            model.NotificationMessage, token, notificationImageUrl ?? imageUrl);
                        TempData["Success"] = $"{issuedCount} code(s) issued; a personalized app notification was queued for each customer.";
                    }
                    catch (Exception)
                    {
                        TempData["Error"] = "Codes were issued, but the app notification could not be queued. Open an issued code to send it individually.";
                    }
                }
            }
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, UserMessage(ex));
            model.AvailableDepartments = await promos.GetActiveDepartmentsAsync(token);
            return View(model);
        }
    }

    [HttpGet]
    public async Task<IActionResult> PreviewAudience(string? audienceType, CancellationToken token)
    {
        if (audienceType is null or "Selected") return BadRequest();
        try
        {
            var group = await audience.ResolveGroupAsync(token);
            var ids = await ResolveAudienceAsync(audienceType, group, token);
            return Json(new { customerCount = Math.Min(ids.Count, 20_000), tooMany = ids.Count > 20_000 });
        }
        catch (ArgumentOutOfRangeException) { return BadRequest(); }
        catch (InvalidOperationException) { return BadRequest(); }
    }

    private async Task<IReadOnlyList<long>> ResolveAudienceAsync(string type,
        LoyaltyGroupSnapshot group, CancellationToken token) => type switch
    {
        "All" => await audience.GetActiveCustomerIdsAsync(group, token),
        "Purchased30" => await audience.GetCustomersWithRecentPurchaseAsync(30, group, token),
        "Purchased60" => await audience.GetCustomersWithRecentPurchaseAsync(60, group, token),
        "Purchased90" => await audience.GetCustomersWithRecentPurchaseAsync(90, group, token),
        "NoPurchase30" => await audience.GetCustomersWithoutRecentPurchaseAsync(30, group, token),
        "NoPurchase60" => await audience.GetCustomersWithoutRecentPurchaseAsync(60, group, token),
        "NoPurchase90" => await audience.GetCustomersWithoutRecentPurchaseAsync(90, group, token),
        "NeverPurchased" => await audience.GetCustomersWithoutRecentPurchaseAsync(0, group, token),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken token)
    {
        var group = await audience.ResolveGroupAsync(token);
        var promo = await promos.GetAsync(id, group, token);
        if (promo is null) return NotFound();
        ViewData["HasDraft"] = await promoDrafts.GetAsync(id, group, token) is not null;
        return View(new PromoDetailsViewModel
        {
            Promo = promo,
            Departments = await promos.GetDepartmentsAsync(id, group, token),
            Codes = await promos.GetCodesAsync(id, group, token),
            ImageUrl = await content.GetImageUrlAsync(id, group, token)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, CancellationToken token)
    {
        var group = await audience.ResolveGroupAsync(token);
        var promo = await promos.GetAsync(id, group, token);
        if (promo is null) return NotFound();
        var codes = await promos.GetCodesAsync(id, group, token);
        var draft = await promoDrafts.GetAsync(id, group, token);
        var original = draft?.Draft.OriginalAssignments ??
            codes.Where(x => x.Status != "Revoked")
                .ToDictionary(x => x.Code, x => x.CustomerId, StringComparer.Ordinal);
        var data = draft?.Draft;
        return View(new PromoEditViewModel
        {
            Id = id,
            Name = data?.Name ?? promo.Name,
            Caption = data?.Caption ?? promo.Caption,
            Body = data?.Body ?? promo.Body,
            CampaignCode = promo.CampaignCode,
            ValidityDate = data?.ValidityDate ?? promo.Validity.Date,
            DepartmentIds = data?.DepartmentIds.ToList() ??
                (await promos.GetDepartmentsAsync(id, group, token)).Select(x => x.Id).ToList(),
            AvailableDepartments = await promos.GetActiveDepartmentsAsync(token),
            CanEditDepartments = promo.IssuedCount == 0,
            UnusedCount = codes.Count(x => x.Status is "Issued" or "Expired"),
            UnchangedCodes = codes.Where(x => x.Status is "Redeemed" or "Revoked").ToArray(),
            Codes = codes,
            CustomerChangesJson = JsonSerializer.Serialize(ToVoucherAssignments(
                data?.CustomerChanges ?? new Dictionary<string, long>(), codes)),
            OriginalAssignmentsJson = JsonSerializer.Serialize(ToVoucherAssignments(original, codes)),
            DiscountType = data?.DiscountType ?? "Amount",
            DiscountValue = data?.DiscountValue,
            DraftSavedAtUtc = draft?.SavedAtUtc,
            ImageUrl = data is null ? await content.GetImageUrlAsync(id, group, token)
                : data.ImageUrl,
            SendNotificationAgain = data?.SendNotificationAgain ?? false,
            NotificationEnvironment = await audience.GetConfiguredPushEnvironmentAsync(token),
            NotificationImageUrl = data?.NotificationImageUrl
        });
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(7_000_000)]
    public async Task<IActionResult> Edit(PromoEditViewModel model, string saveMode,
        CancellationToken token)
    {
        var group = await audience.ResolveGroupAsync(token);
        model.NotificationEnvironment = await audience.GetConfiguredPushEnvironmentAsync(token);
        ModelState.Remove(nameof(model.NotificationEnvironment));
        var promo = await promos.GetAsync(model.Id, group, token);
        if (promo is null) return NotFound();
        Dictionary<string, long> changes = [], original = [];
        try
        {
            var codes = await promos.GetCodesAsync(model.Id, group, token);
            changes = ToCodeAssignments(ParseVoucherAssignments(model.CustomerChangesJson, 50), codes);
            original = ToCodeAssignments(ParseVoucherAssignments(model.OriginalAssignmentsJson, 20_000), codes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            ModelState.AddModelError(nameof(model.CustomerChangesJson), "The customer changes are invalid. Reload the page.");
        }
        if (saveMode is not ("Draft" or "Apply"))
            ModelState.AddModelError(string.Empty, "Choose Save as draft or Apply changes.");
        if (model.ImageUpload is { Length: > 5 * 1024 * 1024 })
            ModelState.AddModelError(nameof(model.ImageUpload), "Image must be no larger than 5 MB.");
        if (model.NotificationImageUpload is { Length: > 5 * 1024 * 1024 })
            ModelState.AddModelError(nameof(model.NotificationImageUpload), "Notification image must be no larger than 5 MB.");
        if (!IsValidNotificationImageUrl(model.NotificationImageHttpsUrl))
            ModelState.AddModelError(nameof(model.NotificationImageHttpsUrl), "Use a public HTTPS image URL ending in .jpg, .jpeg, .png or .webp.");
        if (model.NotificationImageUpload is { Length: > 0 } && !string.IsNullOrWhiteSpace(model.NotificationImageHttpsUrl))
            ModelState.AddModelError(nameof(model.NotificationImageHttpsUrl), "Choose either an uploaded image or an HTTPS image URL.");
        if (model.SendNotificationAgain && model.NotificationEnvironment is not ("Development" or "Production"))
            ModelState.AddModelError(nameof(model.NotificationEnvironment), "Choose Development or Production.");
        if (model.Name?.Trim().Length == 0)
            ModelState.AddModelError(nameof(model.Name), "Enter a campaign name.");
        if (model.Caption?.Trim().Length == 0)
            ModelState.AddModelError(nameof(model.Caption), "Enter a caption.");
        if (model.DiscountValue.HasValue)
        {
            if (model.DiscountType is not ("Amount" or "Percentage"))
                ModelState.AddModelError(nameof(model.DiscountType), "Choose a fixed amount or percentage.");
            else if (model.DiscountType == "Amount" &&
                (model.DiscountValue is <= 0 or > 99999999.99m ||
                 decimal.Round(model.DiscountValue.Value, 2) != model.DiscountValue))
                ModelState.AddModelError(nameof(model.DiscountValue), "Enter a positive fixed amount with at most two decimals.");
            else if (model.DiscountType == "Percentage" &&
                (model.DiscountValue is < 1 or > 100 ||
                 decimal.Truncate(model.DiscountValue.Value) != model.DiscountValue))
                ModelState.AddModelError(nameof(model.DiscountValue), "Enter a whole-number percentage from 1 to 100.");
        }
        if (promo.IssuedCount == 0 && model.DepartmentIds.Count == 0)
            ModelState.AddModelError(nameof(model.DepartmentIds), "Select at least one active department.");
        if (!ModelState.IsValid)
        {
            await PopulateEditAsync(model, promo, group, token);
            return View(model);
        }
        try
        {
            var savedDraft = await promoDrafts.GetAsync(model.Id, group, token);
            string? imageUrl = model.RemoveImage ? null : savedDraft is null
                ? await content.GetImageUrlAsync(model.Id, group, token)
                : savedDraft.Value.Draft.ImageUrl;
            if (model.ImageUpload is { Length: > 0 })
                imageUrl = await images.SaveAsync(model.ImageUpload, Request, token);
            string? notificationImageUrl = model.RemoveNotificationImage ? null : savedDraft?.Draft.NotificationImageUrl;
            if (model.NotificationImageUpload is { Length: > 0 })
                notificationImageUrl = await images.SaveAsync(model.NotificationImageUpload, Request, token);
            if (!string.IsNullOrWhiteSpace(model.NotificationImageHttpsUrl))
                notificationImageUrl = model.NotificationImageHttpsUrl.Trim();
            if (saveMode == "Draft")
            {
                var codes = await promos.GetCodesAsync(model.Id, group, token);
                var live = codes.Where(x => x.Status != "Revoked")
                    .ToDictionary(x => x.Code, x => x.CustomerId, StringComparer.Ordinal);
                if (live.Count != original.Count ||
                    live.Any(x => !original.TryGetValue(x.Key, out long id) || id != x.Value))
                    throw new InvalidOperationException("The campaign customer list changed. Reload Edit and review it.");
                if (changes.Keys.Any(x => !live.ContainsKey(x)))
                    throw new InvalidOperationException("A changed code is no longer in this campaign.");
                var draft = new PromoEditDraft(model.Name!.Trim(), model.Caption!.Trim(),
                    model.Body?.Trim(), model.ValidityDate, model.DiscountType,
                    model.DiscountValue, model.DepartmentIds.Distinct().ToArray(),
                    changes, original, imageUrl, model.SendNotificationAgain,
                    model.NotificationEnvironment, notificationImageUrl);
                await promoDrafts.SaveAsync(model.Id, draft, group, token);
                TempData["Success"] = "Draft saved. No live PromoCodes or customer assignments were changed.";
                return RedirectToAction(nameof(Edit), new { id = model.Id });
            }
            var result = await promos.EditAsync(model, changes, original, group, token);
            try { await content.SaveImageUrlAsync(model.Id, imageUrl, group, token); }
            catch { TempData["Error"] = "Campaign changes were applied in ERP, but the image could not be saved in Admin DB."; }
            try { await promoDrafts.DeleteAsync(model.Id, group, token); }
            catch { TempData["Error"] = "Changes were applied, but the saved draft could not be cleared. Do not apply it again."; }
            TempData["Success"] = $"Campaign saved. {result.UpdatedUnusedCount} unused voucher(s) updated; " +
                $"{result.ReassignedCount} code(s) reassigned. {result.SkippedUsedCount} requested reassignment(s) " +
                $"became used and were left unchanged. {result.RedeemedCount} redeemed and " +
                $"{result.RevokedCount} revoked code(s) kept their voucher values.";
            if (model.SendNotificationAgain)
            {
                try
                {
                    var updated = await promos.GetAsync(model.Id, group, token)
                        ?? throw new InvalidOperationException("Campaign no longer exists.");
                    var activeIds = (await promos.GetCodesAsync(model.Id, group, token))
                        .Where(x => x.Status == "Issued").Select(x => x.CustomerId)
                        .Distinct().ToArray();
                    await promoNotifications.QueueAgainAsync(updated, activeIds, group,
                        model.NotificationEnvironment, notificationImageUrl ?? imageUrl, token);
                    TempData["Success"] += $" Notification queued for {activeIds.Length} customer(s).";
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"Campaign changes were applied, but the notification was not queued: {UserMessage(ex)}";
                }
            }
            return RedirectToAction(nameof(Details), new { id = model.Id });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, UserMessage(ex));
            await PopulateEditAsync(model, promo, group, token);
            return View(model);
        }
    }

    private async Task PopulateEditAsync(PromoEditViewModel model, PromoMasterSummary promo,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        var codes = await promos.GetCodesAsync(model.Id, group, token);
        model.CampaignCode = promo.CampaignCode;
        model.AvailableDepartments = await promos.GetActiveDepartmentsAsync(token);
        model.CanEditDepartments = promo.IssuedCount == 0;
        model.UnusedCount = codes.Count(x => x.Status is "Issued" or "Expired");
        model.UnchangedCodes = codes.Where(x => x.Status is "Redeemed" or "Revoked").ToArray();
        model.Codes = codes;
        model.ImageUrl ??= await content.GetImageUrlAsync(model.Id, group, token);
    }

    private static Dictionary<long, long> ParseVoucherAssignments(string? json, int maximum)
    {
        var result = JsonSerializer.Deserialize<Dictionary<long, long>>(json ?? "{}")
            ?? throw new InvalidOperationException("Customer changes are empty.");
        if (result.Count > maximum ||
            result.Any(x => x.Value <= 0 || x.Key <= 0))
            throw new InvalidOperationException("Customer changes are invalid.");
        return result;
    }

    private static Dictionary<long, long> ToVoucherAssignments(
        IReadOnlyDictionary<string, long> assignments, IReadOnlyList<PromoIssuedCode> codes)
    {
        var voucherByCode = codes.ToDictionary(x => x.Code, x => x.VoucherNo, StringComparer.Ordinal);
        return assignments.Where(x => voucherByCode.ContainsKey(x.Key))
            .ToDictionary(x => voucherByCode[x.Key], x => x.Value);
    }

    private static Dictionary<string, long> ToCodeAssignments(
        IReadOnlyDictionary<long, long> assignments, IReadOnlyList<PromoIssuedCode> codes)
    {
        var codeByVoucher = codes.ToDictionary(x => x.VoucherNo, x => x.Code);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (!codeByVoucher.TryGetValue(assignment.Key, out string? code))
                throw new InvalidOperationException("A customer code is no longer in this campaign. Reload the page.");
            result.Add(code, assignment.Value);
        }
        return result;
    }
    [HttpGet]
    public async Task<IActionResult> Issue(int id, CancellationToken token)
    {
        var group = await audience.ResolveGroupAsync(token);
        var promo = await promos.GetAsync(id, group, token);
        if (promo is null) return NotFound();
        return View(new PromoIssueViewModel { PromoId = id, Promo = promo,
            NotificationEnvironment = await audience.GetConfiguredPushEnvironmentAsync(token),
            CampaignImageUrl = await content.GetImageUrlAsync(id, group, token) });
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(7_000_000)]
    public async Task<IActionResult> Issue(PromoIssueViewModel model, CancellationToken token)
    {
        var group = await audience.ResolveGroupAsync(token);
        model.NotificationEnvironment = await audience.GetConfiguredPushEnvironmentAsync(token);
        ModelState.Remove(nameof(model.NotificationEnvironment));
        model.Promo = await promos.GetAsync(model.PromoId, group, token);
        if (model.Promo is null) return NotFound();
        if (model.DiscountType is not ("Amount" or "Percentage"))
            ModelState.AddModelError(nameof(model.DiscountType), "Choose an amount or percentage.");
        if (model.DiscountType == "Percentage" &&
            (model.Value < 1 || model.Value > 100 || decimal.Truncate(model.Value) != model.Value))
            ModelState.AddModelError(nameof(model.Value), "Percentage must be a whole number from 1 to 100.");
        if (model.DiscountType == "Amount" && decimal.Round(model.Value, 2) != model.Value)
            ModelState.AddModelError(nameof(model.Value), "Fixed amount supports two decimal places.");
        if (model.SendNotification && model.NotificationEnvironment is not ("Development" or "Production"))
            ModelState.AddModelError(nameof(model.NotificationEnvironment), "Choose Development or Production.");
        if (model.NotificationImageUpload is { Length: > 5 * 1024 * 1024 })
            ModelState.AddModelError(nameof(model.NotificationImageUpload), "Notification image must be no larger than 5 MB.");
        if (!IsValidNotificationImageUrl(model.NotificationImageHttpsUrl))
            ModelState.AddModelError(nameof(model.NotificationImageHttpsUrl), "Use a public HTTPS image URL ending in .jpg, .jpeg, .png or .webp.");
        if (model.NotificationImageUpload is { Length: > 0 } && !string.IsNullOrWhiteSpace(model.NotificationImageHttpsUrl))
            ModelState.AddModelError(nameof(model.NotificationImageHttpsUrl), "Choose either an uploaded image or an HTTPS image URL.");
        model.CampaignImageUrl = await content.GetImageUrlAsync(model.PromoId, group, token);
        model.Customer = model.CustomerId > 0
            ? await promos.GetCustomerAsync(model.CustomerId, group, token) : null;
        if (model.Customer is null)
            ModelState.AddModelError(nameof(model.CustomerId), "Choose an active customer in this loyalty group.");
        if (!ModelState.IsValid) return View(model);
        try
        {
            string code = await promos.IssueAsync(model, group, token);
            TempData["Success"] = "Customer PromoCode issued. Print or share the barcode with the customer.";
            if (model.SendNotification)
            {
                try
                {
                    var issued = await promos.GetCodeAsync(model.PromoId, code, group, token)
                        ?? throw new InvalidOperationException("The issued code could not be loaded for notification.");
                    string? notificationImageUrl = model.NotificationImageUpload is { Length: > 0 }
                        ? await images.SaveAsync(model.NotificationImageUpload, Request, token)
                        : model.CampaignImageUrl;
                    if (!string.IsNullOrWhiteSpace(model.NotificationImageHttpsUrl))
                        notificationImageUrl = model.NotificationImageHttpsUrl.Trim();
                    await promoNotifications.QueueAsync(model.Promo, issued, group,
                        model.NotificationEnvironment, token,
                        notificationImageUrl);
                    TempData["Success"] = "Customer PromoCode issued and app notification queued.";
                }
                catch (Exception)
                {
                    TempData["Error"] = "The PromoCode was issued, but its notification could not be queued. Use Send app notification on the code page to try again.";
                }
            }
            return RedirectToAction(nameof(Print), new
            {
                id = model.PromoId,
                voucherNo = long.Parse(code[5..], System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, UserMessage(ex));
            return View(model);
        }
    }

    [HttpGet]
    public async Task<IActionResult> SearchCustomers(string? q, CancellationToken token)
    {
        q = q?.Trim() ?? "";
        if (q.Length is < 2 or > 100) return Json(Array.Empty<PromoCustomer>());
        var group = await audience.ResolveGroupAsync(token);
        return Json(await promos.SearchCustomersAsync(q, group, token));
    }

    [HttpGet]
    public async Task<IActionResult> Print(int id, long voucherNo, CancellationToken token)
    {
        if (voucherNo <= 0) return NotFound();
        var group = await audience.ResolveGroupAsync(token);
        var promo = await promos.GetAsync(id, group, token);
        var issued = promo is null ? null : (await promos.GetCodesAsync(id, group, token))
            .SingleOrDefault(x => x.VoucherNo == voucherNo);
        if (promo is null || issued is null) return NotFound();
        var writer = new BarcodeWriterSvg
        {
            Format = BarcodeFormat.CODE_128,
            Options = new EncodingOptions { Width = 620, Height = 140, Margin = 12, PureBarcode = true }
        };
        string svg = writer.Write(issued.Code).Content;
        return View(new PromoPrintViewModel
        {
            Promo = promo,
            Code = issued,
            BarcodeSvg = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)),
            CampaignImageUrl = await content.GetImageUrlAsync(id, group, token)
        });
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(7_000_000)]
    public async Task<IActionResult> SendNotification(int id, long voucherNo,
        IFormFile? notificationImageUpload,
        string? notificationImageHttpsUrl, CancellationToken token)
    {
        if (voucherNo <= 0) return NotFound();
        var environment = await audience.GetConfiguredPushEnvironmentAsync(token);
        var group = await audience.ResolveGroupAsync(token);
        var promo = await promos.GetAsync(id, group, token);
        var issued = promo is null ? null : (await promos.GetCodesAsync(id, group, token))
            .SingleOrDefault(x => x.VoucherNo == voucherNo);
        if (promo is null || issued is null) return NotFound();
        try
        {
            if (!IsValidNotificationImageUrl(notificationImageHttpsUrl))
                throw new InvalidOperationException("Use a public HTTPS image URL ending in .jpg, .jpeg, .png or .webp.");
            if (notificationImageUpload is { Length: > 0 } && !string.IsNullOrWhiteSpace(notificationImageHttpsUrl))
                throw new InvalidOperationException("Choose either an uploaded image or an HTTPS image URL.");
            string? imageUrl = notificationImageUpload is { Length: > 0 }
                ? await images.SaveAsync(notificationImageUpload, Request, token)
                : await content.GetImageUrlAsync(id, group, token);
            if (!string.IsNullOrWhiteSpace(notificationImageHttpsUrl))
                imageUrl = notificationImageHttpsUrl.Trim();
            await promoNotifications.QueueAsync(promo, issued, group, environment, token,
                imageUrl);
            TempData["Success"] = "App notification queued for this customer.";
        }
        catch (Exception)
        {
            TempData["Error"] = "The app notification could not be queued. Check the selected environment and notification settings.";
        }
        return RedirectToAction(nameof(Print), new { id, voucherNo });
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(7_000_000)]
    public async Task<IActionResult> SendSelectedNotifications(int id, string? selectedVoucherNosJson,
        string? notificationTitle, string? notificationMessage,
        IFormFile? notificationImageUpload, string? notificationImageHttpsUrl,
        CancellationToken token)
    {
        try
        {
            var environment = await audience.GetConfiguredPushEnvironmentAsync(token);
            List<long> selectedVoucherNos = string.IsNullOrWhiteSpace(selectedVoucherNosJson)
                ? [] : JsonSerializer.Deserialize<List<long>>(selectedVoucherNosJson) ?? [];
            if (selectedVoucherNos.Count is < 1 or > 20_000
                || selectedVoucherNos.Any(x => x <= 0)
                || selectedVoucherNos.Distinct().Count() != selectedVoucherNos.Count)
                throw new InvalidOperationException("Select at least one active, unused customer code.");
            if (environment is not ("Development" or "Production"))
                throw new InvalidOperationException("Choose Development or Production.");
            if (!PromoNotificationService.IsValidTemplate(notificationTitle, 150)
                || !PromoNotificationService.IsValidTemplate(notificationMessage, 500))
                throw new InvalidOperationException("Check the notification title and message tokens and length.");
            if (!IsValidNotificationImageUrl(notificationImageHttpsUrl))
                throw new InvalidOperationException("Use a public HTTPS image URL ending in .jpg, .jpeg, .png or .webp.");
            if (notificationImageUpload is { Length: > 0 }
                && !string.IsNullOrWhiteSpace(notificationImageHttpsUrl))
                throw new InvalidOperationException("Choose either an uploaded image or an HTTPS image URL.");

            var group = await audience.ResolveGroupAsync(token);
            var promo = await promos.GetAsync(id, group, token);
            if (promo is null) return NotFound();
            var requested = selectedVoucherNos.ToHashSet();
            var issued = (await promos.GetCodesAsync(id, group, token))
                .Where(x => requested.Contains(x.VoucherNo)).ToArray();
            if (issued.Length != requested.Count || issued.Any(x => x.Status != "Issued"))
                throw new InvalidOperationException("The selected codes must belong to this campaign and be active and unused.");

            string? imageUrl = notificationImageUpload is { Length: > 0 }
                ? await images.SaveAsync(notificationImageUpload, Request, token)
                : await content.GetImageUrlAsync(id, group, token);
            if (!string.IsNullOrWhiteSpace(notificationImageHttpsUrl))
                imageUrl = notificationImageHttpsUrl.Trim();
            await promoNotifications.QueueCampaignAsync(promo, issued, group, environment,
                notificationTitle!, notificationMessage!, token, imageUrl);
            TempData["Success"] = $"App notifications queued for {issued.Length} customer(s).";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex is InvalidOperationException or ArgumentException
                ? ex.Message : "The app notifications could not be queued. Check the notification settings and try again.";
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private static bool IsValidNotificationImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return value.Length <= 1000
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && new[] { ".jpg", ".jpeg", ".png", ".webp" }
                .Contains(Path.GetExtension(uri.AbsolutePath), StringComparer.OrdinalIgnoreCase);
    }

    private static string RemoveCodeToken(string text) => text
        .Replace("PromoCode {code}", "promo", StringComparison.OrdinalIgnoreCase)
        .Replace("{code}", "", StringComparison.OrdinalIgnoreCase)
        .Replace("  ", " ");

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(int id, long voucherNo, CancellationToken token)
    {
        try
        {
            var group = await audience.ResolveGroupAsync(token);
            var issued = (await promos.GetCodesAsync(id, group, token))
                .SingleOrDefault(x => x.VoucherNo == voucherNo);
            if (issued is null) return NotFound();
            await promos.RevokeAsync(id, issued.Code, group, token);
            TempData["Success"] = "The PromoCode was revoked and can no longer be scanned.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = UserMessage(ex);
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    private static string UserMessage(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        SqlException sql when sql.Number is >= 51420 and <= 51499 => sql.Message,
        _ => "The PromoCode could not be saved. Check the ZWCORE database and try again."
    };
}

