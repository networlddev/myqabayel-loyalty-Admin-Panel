using MyQabayel.Admin.Data;
using MyQabayel.Admin.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyQabayel.Admin.Services;

namespace MyQabayel.Admin.Controllers;

public class NotificationsController(AdminDbContext db, LoyaltyCustomerAudience audience,
    FirebasePushSender firebase, IConfiguration configuration,
    IWebHostEnvironment hostingEnvironment, PublicAssetUrlService assetUrls) : Controller
{
    public async Task<IActionResult> Index(CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var environment = await audience.GetConfiguredPushEnvironmentAsync(token);
        var notifications = await db.LoyaltyNotifications.AsNoTracking()
            .Where(x => x.GroupCode == groupCode && x.Environment == environment)
            .OrderByDescending(x => x.ScheduledAtUtc).ToListAsync(token);
        ViewData["DeliverySummaries"] = await db.NotificationDeliveries.AsNoTracking()
            .Where(x => x.Notification.GroupCode == groupCode && x.Notification.Environment == environment)
            .GroupBy(x => x.NotificationId)
            .Select(group => new NotificationDeliverySummary
            {
                NotificationId = group.Key,
                DeviceCount = group.Count(),
                AcceptedCount = group.Count(x => x.Status == "Sent"),
                FailedCount = group.Count(x => x.Status == "Failed"),
                WaitingCount = group.Count(x => x.Status == "Pending" || x.Status == "Retry"),
                SkippedCount = group.Count(x => x.Status == "Skipped")
            }).ToDictionaryAsync(x => x.NotificationId, token);
        await PopulateDeviceAvailabilityAsync(token);
        ViewData["PushEnvironment"] = environment;
        return View(notifications);
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var environment = await audience.GetConfiguredPushEnvironmentAsync(token);
        var notification = await db.LoyaltyNotifications.AsNoTracking()
            .Include(x => x.Deliveries).Include(x => x.Images)
            .SingleOrDefaultAsync(x => x.Id == id && x.GroupCode == groupCode && x.Environment == environment, token);
        if (notification is null) return NotFound();
        if (notification.AudienceType != "All")
            ViewData["RecipientCount"] = await db.NotificationRecipients.AsNoTracking()
                .CountAsync(x => x.NotificationId == id, token);
        await PopulateDeviceAvailabilityAsync(token);
        return View(notification);
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken token)
    {
        var model = new LoyaltyNotificationCreateViewModel
        {
            Environment = await audience.GetConfiguredPushEnvironmentAsync(token)
        };
        await PopulateFormAsync(model, token);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> SearchCustomers(string? query, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return Json(Array.Empty<NotificationCustomerSearchResult>());
        var group = await audience.ResolveGroupAsync(token);
        return Json(await audience.SearchActiveCustomersAsync(query, group, token));
    }

    [HttpGet]
    public async Task<IActionResult> PreviewAudience(string? audienceType, CancellationToken token)
    {
        int days = PurchaseAudienceDays(audienceType);
        if (days < 0) return BadRequest();
        var group = await audience.ResolveGroupAsync(token);
        var ids = await audience.GetCustomersWithoutRecentPurchaseAsync(days, group, token);
        return Json(new { customerCount = Math.Min(ids.Count, 20_000), tooMany = ids.Count > 20_000 });
    }

    private static int PurchaseAudienceDays(string? audienceType) => audienceType switch
    {
        "NeverPurchased" => 0,
        "NoPurchase30" => 30,
        "NoPurchase60" => 60,
        "NoPurchase90" => 90,
        _ => -1
    };

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(27_000_000)]
    public async Task<IActionResult> Create(LoyaltyNotificationCreateViewModel model, CancellationToken token)
    {
        // An unselected file input can bind as an empty IFormFile. It is not an image upload.
        model.Images = model.Images.Where(image => image.Length > 0).ToList();
        model.Environment = await audience.GetConfiguredPushEnvironmentAsync(token);
        ModelState.Remove(nameof(model.Environment));
        await PopulateFormAsync(model, token);
        if (model.Environment is not ("Development" or "Production"))
            ModelState.AddModelError(nameof(model.Environment), "Choose Development or Production.");
        if (model.AudienceType is not ("All" or "Selected" or "NeverPurchased" or "NoPurchase30" or "NoPurchase60" or "NoPurchase90"))
            ModelState.AddModelError(nameof(model.AudienceType), "Choose a valid customer audience.");
        if (model.ExpiresAt.HasValue && model.ExpiresAt <= model.ScheduledAt)
            ModelState.AddModelError(nameof(model.ExpiresAt), "Expiry must be after the scheduled date and time.");
        if ((!string.IsNullOrEmpty(model.Title) && CustomerNotificationText.HidePromoCodes(model.Title) != model.Title)
            || (!string.IsNullOrEmpty(model.Message) && CustomerNotificationText.HidePromoCodes(model.Message) != model.Message))
            ModelState.AddModelError(nameof(model.Message), "Do not include a private PromoCode in notification text. Direct customers to the barcode in the app.");
        string? imageUrl = model.ImageUrl?.Trim();
        if (!string.IsNullOrEmpty(imageUrl)
            && (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri)
                || imageUri.Scheme != Uri.UriSchemeHttps
                || !new[] { ".jpg", ".jpeg", ".png", ".webp" }
                    .Contains(Path.GetExtension(imageUri.AbsolutePath), StringComparer.OrdinalIgnoreCase)))
            ModelState.AddModelError(nameof(model.ImageUrl),
                "Use a public HTTPS image URL ending in .jpg, .jpeg, .png or .webp.");
        if (model.Images.Count + (string.IsNullOrWhiteSpace(imageUrl) ? 0 : 1) > 5)
            ModelState.AddModelError(nameof(model.Images), "Use no more than five images including the image URL.");
        var uploadExtensions = new List<string>();
        foreach (var image in model.Images)
        {
            if (image.Length is < 1 or > 5 * 1024 * 1024)
            {
                ModelState.AddModelError(nameof(model.Images), "Each image must be no larger than 5 MB.");
                break;
            }
            string? extension = await DetectImageExtensionAsync(image, token);
            if (extension is null)
            {
                ModelState.AddModelError(nameof(model.Images), "Upload only valid JPEG, PNG or WebP images.");
                break;
            }
            uploadExtensions.Add(extension);
        }

        long[] selectedIds = [];
        if (model.AudienceType == "Selected")
        {
            string[] parts = (model.SelectedCustomerIds ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 200 || parts.Any(x => !long.TryParse(x, out long id) || id <= 0))
                ModelState.AddModelError(nameof(model.SelectedCustomerIds), "Enter 1 to 200 valid customer IDs.");
            else selectedIds = parts.Select(long.Parse).Distinct().ToArray();
        }

        if (!ModelState.IsValid) return View(model);

        var scheduledUtc = model.ScheduledAt.ToUniversalTime();
        LoyaltyGroupSnapshot group;
        Campaign? linkedCampaign = null;
        try
        {
            group = await audience.ResolveGroupAsync(token);
            if (model.AudienceType == "Selected"
                && !await audience.AreActiveCustomersInGroupAsync(selectedIds, group, token))
            {
                ModelState.AddModelError(nameof(model.SelectedCustomerIds),
                    "One or more customer IDs are not active in this loyalty group.");
                return View(model);
            }
            int inactiveDays = PurchaseAudienceDays(model.AudienceType);
            if (inactiveDays >= 0)
            {
                selectedIds = (await audience.GetCustomersWithoutRecentPurchaseAsync(
                    inactiveDays, group, token)).ToArray();
                if (selectedIds.Length is 0 or > 20_000)
                {
                    ModelState.AddModelError(nameof(model.AudienceType), selectedIds.Length == 0
                        ? "No customers match this purchase-activity filter. Choose another audience."
                        : "This filter matches over 20,000 customers. Narrow the audience before sending.");
                    return View(model);
                }
            }
            if (model.LinkedCampaignId.HasValue)
            {
                linkedCampaign = await db.Campaigns.Include(x => x.Images).AsNoTracking().SingleOrDefaultAsync(x =>
                    x.Id == model.LinkedCampaignId.Value && x.GroupCode == group.GroupCode
                    && x.Status == "Published" && x.StartDateUtc <= scheduledUtc
                    && x.EndDateUtc >= scheduledUtc, token);
                if (linkedCampaign is null)
                {
                    ModelState.AddModelError(nameof(model.LinkedCampaignId),
                        "Choose a campaign in this loyalty group that is active when the notification is sent.");
                    return View(model);
                }
            }
        }
        catch (Exception)
        {
            ModelState.AddModelError("", "Unable to validate the configured loyalty group.");
            return View(model);
        }

        var uploadedPaths = new List<string>();
        try
        {
            var galleryUrls = new List<string>();
            if (!string.IsNullOrEmpty(imageUrl)) galleryUrls.Add(imageUrl);
            if (model.Images.Count > 0)
            {
                string uploadFolder = Path.Combine(hostingEnvironment.WebRootPath, "uploads", "notification");
                Directory.CreateDirectory(uploadFolder);
                for (int index = 0; index < model.Images.Count; index++)
                {
                    string name = $"{Guid.NewGuid():N}{uploadExtensions[index]}";
                    string path = Path.Combine(uploadFolder, name);
                    uploadedPaths.Add(path);
                    await using (var output = System.IO.File.Create(path))
                        await model.Images[index].CopyToAsync(output, token);
                    galleryUrls.Add(assetUrls.ToAbsolute($"/uploads/notification/{name}", Request));
                }
            }
            if (galleryUrls.Count == 0 && model.UseLinkedCampaignImage && linkedCampaign is not null)
            {
                galleryUrls.AddRange(linkedCampaign.Images.OrderBy(x => x.SortOrder)
                    .Select(x => x.ImageUrl).Take(5));
                if (galleryUrls.Count == 0 && !string.IsNullOrWhiteSpace(linkedCampaign.ImageUrl))
                    galleryUrls.Add(linkedCampaign.ImageUrl);
            }
            db.LoyaltyNotifications.Add(new LoyaltyNotification
            {
                Title = model.Title.Trim(),
                Message = model.Message.Trim(),
                ScheduledAtUtc = scheduledUtc,
                ExpiresAtUtc = model.ExpiresAt?.ToUniversalTime(),
                GroupCode = group.GroupCode,
                CCode = group.CCode,
                LoyaltyGroupId = group.LoyaltyGroupId,
                ImageUrl = galleryUrls.FirstOrDefault(),
                Images = galleryUrls.Select((url, index) => new LoyaltyNotificationImage
                {
                    ImageUrl = url, SortOrder = index, IsPrimary = index == 0
                }).ToList(),
                TargetType = linkedCampaign?.CampaignType ?? "Notifications",
                TargetId = linkedCampaign?.Id,
                Environment = model.Environment,
                AudienceType = model.AudienceType,
                Recipients = selectedIds.Select(id => new LoyaltyNotificationRecipient { CustomerId = id }).ToList(),
                Status = model.PublishNow
                    ? (scheduledUtc <= DateTime.UtcNow ? "Published" : "Scheduled")
                    : "Draft"
            });
            await db.SaveChangesAsync(token);
        }
        catch
        {
            foreach (string path in uploadedPaths)
            {
                try { System.IO.File.Delete(path); }
                catch (IOException) { /* Preserve the original failure. */ }
                catch (UnauthorizedAccessException) { /* Preserve the original failure. */ }
            }
            throw;
        }
        TempData["Success"] = model.PublishNow
            ? "Notification saved. Open delivery details to check Firebase push results."
            : "Notification saved as a draft in MyQabayelAdminDB.";
        return RedirectToAction(nameof(Index));
    }

    private static async Task<string?> DetectImageExtensionAsync(IFormFile image, CancellationToken token)
    {
        byte[] signature = new byte[12];
        await using var stream = image.OpenReadStream();
        int count = 0;
        while (count < signature.Length)
        {
            int read = await stream.ReadAsync(signature.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count >= 3 && signature[0] == 0xFF && signature[1] == 0xD8 && signature[2] == 0xFF)
            return ".jpg";
        if (count >= 8 && signature.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return ".png";
        if (count >= 12 && signature.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && signature.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return ".webp";
        return null;
    }

    private async Task PopulateFormAsync(LoyaltyNotificationCreateViewModel model,
        CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        ViewData["PushEnvironment"] = await audience.GetConfiguredPushEnvironmentAsync(token);
        ViewData["GroupCode"] = groupCode;
        ViewData["DevelopmentPushReady"] = firebase.IsConfigured("Development");
        ViewData["ProductionPushReady"] = firebase.IsConfigured("Production");
        ViewData["DevelopmentProjectId"] = configuration["PushNotifications:Development:ExpectedProjectId"];
        var campaigns = await db.Campaigns.Include(x => x.Images).AsNoTracking()
            .Where(x => x.GroupCode == groupCode && x.Status == "Published"
                && x.EndDateUtc >= DateTime.UtcNow)
            .OrderBy(x => x.CampaignType).ThenBy(x => x.Title)
            .ToListAsync(token);
        ViewData["CampaignPreviewImages"] = campaigns.ToDictionary(x => x.Id.ToString(), x =>
            x.Images.Count > 0
                ? x.Images.OrderBy(image => image.SortOrder).Take(5).Select(image => image.ImageUrl).ToArray()
                : new[] { x.ImageUrl });
        model.CampaignOptions = campaigns.Select(x => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem
        {
            Value = x.Id.ToString(), Text = $"{x.CampaignType}: {x.Title} (#{x.Id})"
        }).ToList();
        await PopulateDeviceAvailabilityAsync(token);
    }

    private async Task PopulateDeviceAvailabilityAsync(CancellationToken token)
    {
        try
        {
            var group = await audience.ResolveGroupAsync(token);
            ViewData["DeviceAvailability"] = await audience.GetActiveDeviceAvailabilityAsync(group, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            ViewData["DeviceAvailabilityUnavailable"] = true;
        }
    }

    [HttpGet]
    public async Task<IActionResult> Resend(long id, CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var environment = await audience.GetConfiguredPushEnvironmentAsync(token);
        var item = await db.LoyaltyNotifications.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.GroupCode == groupCode && x.Environment == environment, token);
        if (item is null) return NotFound();
        if (item.Status is not ("Published" or "Archived")) return RedirectToAction(nameof(Index));
        if (item.ExpiresAtUtc.HasValue && item.ExpiresAtUtc <= DateTime.UtcNow)
        {
            TempData["Error"] = "This notification has expired. Create a new notification with a later expiry.";
            return RedirectToAction(nameof(Index));
        }
        if (item.DispatchStatus == "Sending")
        {
            TempData["Error"] = "This notification is currently being sent. Try again shortly.";
            return RedirectToAction(nameof(Index));
        }

        var model = new LoyaltyNotificationResendViewModel { Id = id };
        await PopulateResendModelAsync(model, item, token);
        if (item.AudienceType is "All" or "Selected")
            model.AudienceType = item.AudienceType;
        if (item.AudienceType == "Selected")
        {
            var selectedIds = await db.NotificationRecipients.AsNoTracking()
                .Where(x => x.NotificationId == id)
                .OrderBy(x => x.CustomerId)
                .Select(x => x.CustomerId)
                .ToArrayAsync(token);
            model.SelectedCustomerIds = string.Join(", ", selectedIds);
        }
        return View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(long id, LoyaltyNotificationResendViewModel model,
        CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var environment = await audience.GetConfiguredPushEnvironmentAsync(token);
        var item = await db.LoyaltyNotifications
            .SingleOrDefaultAsync(x => x.Id == id && x.GroupCode == groupCode && x.Environment == environment, token);
        if (item is null) return NotFound();
        if (item.Status is not ("Published" or "Archived")) return RedirectToAction(nameof(Index));
        model.Id = id;
        await PopulateResendModelAsync(model, item, token);

        if (model.AudienceType is not ("All" or "Selected"))
            ModelState.AddModelError(nameof(model.AudienceType), "Choose all customers or specific customers.");

        long[] selectedIds = [];
        if (model.AudienceType == "Selected")
        {
            string[] parts = (model.SelectedCustomerIds ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 200 || parts.Any(x => !long.TryParse(x, out long value) || value <= 0))
                ModelState.AddModelError(nameof(model.SelectedCustomerIds), "Enter 1 to 200 valid customer IDs.");
            else
                selectedIds = parts.Select(long.Parse).Distinct().ToArray();
        }

        if (!ModelState.IsValid) return View(model);

        if (model.AudienceType == "Selected")
        {
            try
            {
                var group = await audience.ResolveGroupAsync(token);
                if (group.CCode != item.CCode || group.LoyaltyGroupId != item.LoyaltyGroupId
                    || !await audience.AreActiveCustomersInGroupAsync(selectedIds, group, token))
                {
                    ModelState.AddModelError(nameof(model.SelectedCustomerIds),
                        "One or more customer IDs are not active in this loyalty group.");
                    return View(model);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                ModelState.AddModelError("", "Unable to validate the selected customers right now.");
                return View(model);
            }
        }

        if (item.DispatchStatus == "Sending")
        {
            TempData["Error"] = "This notification is currently being sent. Try again shortly.";
            return RedirectToAction(nameof(Index));
        }
        var now = DateTime.UtcNow;
        if (item.ExpiresAtUtc.HasValue && item.ExpiresAtUtc <= now)
        {
            TempData["Error"] = "This notification has expired. Create a new notification with a later expiry.";
            return RedirectToAction(nameof(Index));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        int reserved = await db.LoyaltyNotifications
            .Where(x => x.Id == id && x.GroupCode == groupCode && x.Environment == environment
                && (x.Status == "Published" || x.Status == "Archived")
                && x.DispatchStatus != "Sending"
                && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.DispatchStatus, "Pending"), token);
        if (reserved != 1)
        {
            TempData["Error"] = "This notification changed while you were choosing customers. Please try again.";
            return RedirectToAction(nameof(Index));
        }
        await db.NotificationDeliveries.Where(x => x.NotificationId == id).ExecuteDeleteAsync(token);
        await db.NotificationRecipients.Where(x => x.NotificationId == id).ExecuteDeleteAsync(token);
        if (model.AudienceType == "Selected")
            db.NotificationRecipients.AddRange(selectedIds.Select(customerId =>
                new LoyaltyNotificationRecipient { NotificationId = id, CustomerId = customerId }));
        item.AudienceType = model.AudienceType;
        item.ScheduledAtUtc = now;
        item.Status = "Published";
        item.DispatchStatus = "Pending";
        item.PushAttemptCount = 0;
        item.LastPushAttemptAtUtc = null;
        item.PushCompletedAtUtc = null;
        item.UpdatedAtUtc = now;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);

        TempData["Success"] = model.AudienceType == "All"
            ? "Notification is live for all customers. Firebase push will retry shortly."
            : $"Notification is live for {selectedIds.Length} selected customer(s). Firebase push will retry shortly.";
        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateResendModelAsync(LoyaltyNotificationResendViewModel model,
        LoyaltyNotification item, CancellationToken token)
    {
        model.Title = item.Title;
        model.Message = item.Message;
        model.Environment = item.Environment;
        model.CurrentAudience = item.AudienceType switch
        {
            "All" => "All customers",
            "Selected" => "Selected customers",
            "NeverPurchased" => "Customers who have never purchased",
            "NoPurchase30" => "No recorded purchase in 30 days",
            "NoPurchase60" => "No recorded purchase in 60 days",
            "NoPurchase90" => "No recorded purchase in 90 days",
            _ => item.AudienceType
        };
        if (item.AudienceType != "All")
        {
            int count = await db.NotificationRecipients.AsNoTracking()
                .CountAsync(x => x.NotificationId == item.Id, token);
            model.CurrentAudience += $" ({count} customer(s))";
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(long id, CancellationToken token)
    {
        var item = await db.LoyaltyNotifications.FindAsync([id], token);
        if (item is null || item.GroupCode != await audience.GetConfiguredGroupCodeAsync(token)
            || item.Environment != await audience.GetConfiguredPushEnvironmentAsync(token)) return NotFound();
        if (item.Status is "Published" or "Archived")
            return RedirectToAction(nameof(Resend), new { id });
        if (item.DispatchStatus == "Sending")
        {
            TempData["Error"] = "This notification is currently being sent. Try again shortly.";
            return RedirectToAction(nameof(Index));
        }
        var now = DateTime.UtcNow;
        if (item.ExpiresAtUtc.HasValue && item.ExpiresAtUtc <= now)
        {
            TempData["Error"] = "This notification has expired and cannot go live. Create a new notification with a later expiry.";
            return RedirectToAction(nameof(Index));
        }
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.NotificationDeliveries.Where(x => x.NotificationId == id).ExecuteDeleteAsync(token);
        item.ScheduledAtUtc = now;
        item.Status = "Published";
        item.DispatchStatus = "Pending";
        item.PushAttemptCount = 0;
        item.LastPushAttemptAtUtc = null;
        item.PushCompletedAtUtc = null;
        item.UpdatedAtUtc = now;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        TempData["Success"] = "Notification is live in the app. Firebase push will start shortly.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(long id, CancellationToken token)
    {
        var item = await db.LoyaltyNotifications.FindAsync([id], token);
        if (item is null || item.GroupCode != await audience.GetConfiguredGroupCodeAsync(token)
            || item.Environment != await audience.GetConfiguredPushEnvironmentAsync(token)) return NotFound();
        item.Status = "Archived";
        item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        TempData["Success"] = "Notification archived.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(long id, CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var environment = await audience.GetConfiguredPushEnvironmentAsync(token);
        var deleted = await db.LoyaltyNotifications
            .Where(x => x.Id == id && x.GroupCode == groupCode && x.Environment == environment && x.DispatchStatus != "Sending")
            .ExecuteDeleteAsync(token);

        if (deleted == 0)
        {
            if (!await db.LoyaltyNotifications.AsNoTracking()
                    .AnyAsync(x => x.Id == id && x.GroupCode == groupCode && x.Environment == environment, token))
                return NotFound();

            TempData["Error"] = "This notification is being sent. Try deleting it when sending finishes.";
            return RedirectToAction(nameof(Index));
        }

        TempData["Success"] = "Notification permanently deleted from the app and Admin Panel.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSelected([FromForm] long[]? ids, CancellationToken token)
    {
        if (ids is null || ids.Length == 0 || ids.Any(id => id <= 0))
        {
            TempData["Error"] = "Select at least one notification to delete.";
            return RedirectToAction(nameof(Index));
        }

        var selectedIds = ids.Distinct().ToArray();
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var environment = await audience.GetConfiguredPushEnvironmentAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var deleted = await db.LoyaltyNotifications
            .Where(x => selectedIds.Contains(x.Id) && x.GroupCode == groupCode
                && x.Environment == environment && x.DispatchStatus != "Sending")
            .ExecuteDeleteAsync(token);

        if (deleted != selectedIds.Length)
        {
            await transaction.RollbackAsync(token);
            TempData["Error"] = "One or more selected notifications are missing, belong to another group or environment, or are being sent. Nothing was deleted.";
            return RedirectToAction(nameof(Index));
        }

        await transaction.CommitAsync(token);
        TempData["Success"] = $"{deleted} notification(s) permanently deleted from the app and Admin Panel.";
        return RedirectToAction(nameof(Index));
    }
}
