using QabayelMall.API.Data;
using QabayelMall.API.Models;
using QabayelMall.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace QabayelMall.API.Controllers;
public class CampaignsController(AdminDbContext db, IWebHostEnvironment environment,
    PublicAssetUrlService assetUrls, LoyaltyCustomerAudience audience) : Controller
{
    private static readonly string[] SupportedCampaignTypes = ["Flyer", "Offer", "Advertisement"];

    public async Task<IActionResult> Index(string? type, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(type) && !SupportedCampaignTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            return RedirectToAction(nameof(Index), new { type = "Flyer" });

        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var query = db.Campaigns.Include(x => x.Images).AsNoTracking()
            .Where(x => x.GroupCode == groupCode && SupportedCampaignTypes.Contains(x.CampaignType))
            .OrderByDescending(x => x.UpdatedAtUtc).AsQueryable();
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.CampaignType == type);
        ViewBag.Type = type; return View(await query.ToListAsync(token));
    }
    [HttpGet] public IActionResult Create(string type = "Flyer") =>
        View(new CampaignCreateViewModel { CampaignType = SupportedCampaignTypes.Contains(type, StringComparer.OrdinalIgnoreCase) ? type : "Flyer" });
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(52_500_000)]
    public async Task<IActionResult> Create(CampaignCreateViewModel model, CancellationToken token)
    {
        if (model.EndDate <= model.StartDate) ModelState.AddModelError(nameof(model.EndDate), "End date must be after the start date.");
        if (model.AudienceCondition == 3 && (!model.RecentDays.HasValue || model.RecentDays is < 1 or > 365))
            ModelState.AddModelError(nameof(model.RecentDays), "Enter the number of recent days (1–365) for recent customers.");
        var contentFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Flyer"] = "flyer",
            ["Offer"] = "offer",
            ["Advertisement"] = "advertisement"
        };
        if (!contentFolders.TryGetValue(model.CampaignType, out var contentFolder))
            ModelState.AddModelError(nameof(model.CampaignType), "Choose Flyer, Special Offer or Advertisement.");
        var allowed = new[] { "image/jpeg", "image/png", "image/webp" };
        if (model.Images.Count == 0 || model.Images.Count > 10) ModelState.AddModelError(nameof(model.Images), "Choose between 1 and 10 images.");
        if (model.Images.Any(x => !allowed.Contains(x.ContentType) || x.Length == 0 || x.Length > 5 * 1024 * 1024)) ModelState.AddModelError(nameof(model.Images), "Each image must be JPEG, PNG or WebP and no larger than 5 MB.");
        if (!ModelState.IsValid) return View(model);
        LoyaltyGroupSnapshot group;
        try { group = await audience.ResolveGroupAsync(token); }
        catch (Exception)
        {
            ModelState.AddModelError("", "Configure an active loyalty GroupCode in Settings before creating a campaign.");
            return View(model);
        }
        var uploads = Path.Combine(environment.WebRootPath, "uploads", contentFolder!); Directory.CreateDirectory(uploads);
        var urls = new List<string>();
        foreach (var image in model.Images) { var name=$"{Guid.NewGuid():N}{Path.GetExtension(image.FileName).ToLowerInvariant()}"; await using var stream=System.IO.File.Create(Path.Combine(uploads,name)); await image.CopyToAsync(stream,token); urls.Add(assetUrls.ToAbsolute($"/uploads/{contentFolder}/{name}", Request)); }
        var item = new Campaign { GroupCode=group.GroupCode,CampaignType=model.CampaignType,Title=model.Title,Header=model.Header,Body=model.Body,ImageUrl=urls[0],StartDateUtc=DateTime.SpecifyKind(model.StartDate,DateTimeKind.Utc),EndDateUtc=DateTime.SpecifyKind(model.EndDate.AddDays(1).AddTicks(-1),DateTimeKind.Utc),AudienceCondition=model.AudienceCondition,RecentDays=model.AudienceCondition==3?model.RecentDays:null,ErpBranchId=model.ErpBranchId,DisplaySeconds=model.CampaignType=="Advertisement"?model.DisplaySeconds:30,AllowSkip=model.CampaignType=="Advertisement"&&model.AllowSkip,Images=urls.Select((url,index)=>new CampaignImage{ImageUrl=url,SortOrder=index,IsPrimary=index==0}).ToList() };
        db.Campaigns.Add(item); await db.SaveChangesAsync(token);
        if (model.PublishNow)
            await PublishInternal(item, token);
        TempData["Success"] = model.PublishNow ? "Campaign saved for the loyalty app." : "Campaign saved as draft in MyQabayelAdminDB.";
        return RedirectToAction(nameof(Index), new { type=model.CampaignType });
    }
    [HttpPost, ValidateAntiForgeryToken] public async Task<IActionResult> Publish(long id, CancellationToken token) { var item=await db.Campaigns.FindAsync([id],token); if(item is null || item.GroupCode != await audience.GetConfiguredGroupCodeAsync(token))return NotFound(); var isRepublish=item.Status != "Draft"; await PublishInternal(item,token); TempData["Success"]=isRepublish?"Campaign published again for the loyalty app.":"Campaign published for the loyalty app."; return RedirectToAction(nameof(Index),new{type=item.CampaignType}); }
    [HttpPost, ValidateAntiForgeryToken] public async Task<IActionResult> Archive(long id, CancellationToken token) { var item=await db.Campaigns.FindAsync([id],token); if(item is null || item.GroupCode != await audience.GetConfiguredGroupCodeAsync(token))return NotFound(); item.Status="Archived";item.UpdatedAtUtc=DateTime.UtcNow;await db.SaveChangesAsync(token);TempData["Success"]="Campaign archived.";return RedirectToAction(nameof(Index)); }

    [HttpGet]
    public async Task<IActionResult> Details(long id, CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var item = await db.Campaigns.Include(x => x.Images).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.GroupCode == groupCode, token);
        return item is null || !SupportedCampaignTypes.Contains(item.CampaignType) ? NotFound() : View(item);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(long id, CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var item = await db.Campaigns.Include(x => x.Images).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.GroupCode == groupCode, token);
        if (item is null || !SupportedCampaignTypes.Contains(item.CampaignType)) return NotFound();
        return View(new CampaignEditViewModel
        {
            Id = item.Id, Title = item.Title, CampaignType = item.CampaignType, Header = item.Header, Body = item.Body,
            StartDate = item.StartDateUtc.Date, EndDate = item.EndDateUtc.Date, AudienceCondition = item.AudienceCondition,
            RecentDays = item.RecentDays, ErpBranchId = item.ErpBranchId, DisplaySeconds = item.DisplaySeconds,
            AllowSkip = item.AllowSkip, ExistingImages = item.Images.OrderBy(x => x.SortOrder).ToList()
        });
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(52_500_000)]
    public async Task<IActionResult> Edit(CampaignEditViewModel model, bool publishAfterSave, CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var item = await db.Campaigns.Include(x => x.Images).SingleOrDefaultAsync(x => x.Id == model.Id && x.GroupCode == groupCode, token);
        if (item is null || !SupportedCampaignTypes.Contains(item.CampaignType)) return NotFound();
        var newImages = model.NewImages ?? [];
        if (model.EndDate <= model.StartDate) ModelState.AddModelError(nameof(model.EndDate), "End date must be after the start date.");
        if (model.AudienceCondition == 3 && (!model.RecentDays.HasValue || model.RecentDays is < 1 or > 365))
            ModelState.AddModelError(nameof(model.RecentDays), "Enter the number of recent days (1–365) for recent customers.");
        if (newImages.Count > 10 || item.Images.Count + newImages.Count > 10)
            ModelState.AddModelError(nameof(model.NewImages), "A campaign can contain no more than 10 images.");
        var allowed = new[] { "image/jpeg", "image/png", "image/webp" };
        if (newImages.Any(x => !allowed.Contains(x.ContentType) || x.Length == 0 || x.Length > 5 * 1024 * 1024))
            ModelState.AddModelError(nameof(model.NewImages), "Each image must be JPEG, PNG or WebP and no larger than 5 MB.");
        if (!ModelState.IsValid)
        {
            model.CampaignType = item.CampaignType;
            model.ExistingImages = item.Images.OrderBy(x => x.SortOrder).ToList();
            return View(model);
        }

        var contentFolder = item.CampaignType.ToLowerInvariant();
        var uploads = Path.Combine(environment.WebRootPath, "uploads", contentFolder);
        Directory.CreateDirectory(uploads);
        var nextSortOrder = item.Images.Count == 0 ? 0 : item.Images.Max(x => x.SortOrder) + 1;
        foreach (var image in newImages)
        {
            var name = $"{Guid.NewGuid():N}{Path.GetExtension(image.FileName).ToLowerInvariant()}";
            await using var stream = System.IO.File.Create(Path.Combine(uploads, name));
            await image.CopyToAsync(stream, token);
            item.Images.Add(new CampaignImage { ImageUrl = assetUrls.ToAbsolute($"/uploads/{contentFolder}/{name}", Request), SortOrder = nextSortOrder++, IsPrimary = item.Images.Count == 0 });
        }

        item.Title = model.Title; item.Header = model.Header; item.Body = model.Body;
        item.StartDateUtc = DateTime.SpecifyKind(model.StartDate.Date, DateTimeKind.Utc);
        item.EndDateUtc = DateTime.SpecifyKind(model.EndDate.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc);
        item.AudienceCondition = model.AudienceCondition;
        item.RecentDays = model.AudienceCondition == 3 ? model.RecentDays : null;
        item.ErpBranchId = model.ErpBranchId;
        item.DisplaySeconds = item.CampaignType == "Advertisement" ? model.DisplaySeconds : 30;
        item.AllowSkip = item.CampaignType == "Advertisement" && model.AllowSkip;
        item.UpdatedAtUtc = DateTime.UtcNow;
        if (item.Images.Count > 0) item.ImageUrl = item.Images.OrderBy(x => x.SortOrder).First().ImageUrl;
        await db.SaveChangesAsync(token);
        if (publishAfterSave)
        {
            var isRepublish = item.Status != "Draft";
            await PublishInternal(item, token);
            TempData["Success"] = isRepublish
                ? "Campaign saved and published again for the loyalty app."
                : "Campaign saved and published for the loyalty app.";
        }
        else
        {
            TempData["Success"] = "Campaign updated successfully.";
        }
        return RedirectToAction(nameof(Details), new { id = item.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Delete(long id, CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var item = await db.Campaigns.Include(x => x.Images).AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.GroupCode == groupCode, token);
        return item is null || !SupportedCampaignTypes.Contains(item.CampaignType) ? NotFound() : View(item);
    }

    [HttpPost, ActionName("Delete"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(long id, CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var item = await db.Campaigns.Include(x => x.Images).SingleOrDefaultAsync(x => x.Id == id && x.GroupCode == groupCode, token);
        if (item is null || !SupportedCampaignTypes.Contains(item.CampaignType)) return NotFound();
        var type = item.CampaignType;
        db.Campaigns.Remove(item);
        await db.SaveChangesAsync(token);
        TempData["Success"] = "Campaign deleted from MyQabayelAdminDB.";
        return RedirectToAction(nameof(Index), new { type });
    }
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAdvertisementPlayback(long id, int displaySeconds, bool allowSkip, CancellationToken token)
    {
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var item = await db.Campaigns.SingleOrDefaultAsync(x => x.Id == id && x.GroupCode == groupCode && x.CampaignType == "Advertisement", token);
        if (item is null) return NotFound();
        if (displaySeconds is < 1 or > 300)
        {
            TempData["Error"] = "Advertisement display duration must be between 1 and 300 seconds.";
            return RedirectToAction(nameof(Index), new { type = "Advertisement" });
        }
        item.DisplaySeconds = displaySeconds;
        item.AllowSkip = allowSkip;
        item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(token);
        TempData["Success"] = "Advertisement playback settings updated.";
        return RedirectToAction(nameof(Index), new { type = "Advertisement" });
    }
    private async Task PublishInternal(Campaign item,CancellationToken token)
    {
        if (item.AudienceCondition == 3 && !item.RecentDays.HasValue) item.RecentDays = 30;
        item.Status = item.StartDateUtc > DateTime.UtcNow ? "Scheduled" : "Published";
        item.PublishStatus = "ReadyForApp";
        item.PublishedAtUtc = DateTime.UtcNow;
        item.LastPublishError = null;
        await db.SaveChangesAsync(token);
    }
}
