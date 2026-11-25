using QabayelMall.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QabayelMall.API.Services;

namespace QabayelMall.API.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/content")]
public class ContentApiController(AdminDbContext db, PublicAssetUrlService assetUrls,
    LoyaltyCustomerAudience audience) : ControllerBase
{
    private static readonly string[] SupportedContentTypes = ["Flyer", "Advertisement", "Offer"];

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? type, [FromQuery] int? branchId, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var query = db.Campaigns.Include(x => x.Images).AsNoTracking()
            .Where(x => x.GroupCode == groupCode && SupportedContentTypes.Contains(x.CampaignType)
                && (x.Status == "Published" || x.Status == "Scheduled") && x.StartDateUtc <= now && x.EndDateUtc >= now);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.CampaignType == type);
        if (branchId.HasValue) query = query.Where(x => x.ErpBranchId == null || x.ErpBranchId == branchId);
        var rows = await query.OrderByDescending(x => x.UpdatedAtUtc).ToListAsync(token);
        var result = rows.SelectMany(campaign =>
            (campaign.Images.Count > 0
                ? campaign.Images.OrderBy(image => image.SortOrder).Select(image => new { image.Id, image.ImageUrl, image.SortOrder, image.IsPrimary })
                : new[] { new { Id = 0L, campaign.ImageUrl, SortOrder = 0, IsPrimary = true } })
            .Select(image => new ContentImageResponse(
                image.Id,
                campaign.Id,
                campaign.CampaignType,
                campaign.Title,
                campaign.Header,
                campaign.Body,
                campaign.StartDateUtc,
                campaign.EndDateUtc,
                campaign.ErpBranchId,
                image.SortOrder,
                image.IsPrimary,
                assetUrls.ToAbsolute(image.ImageUrl, Request))))
            .ToList();
        return Ok(new { success = true, count = result.Count, data = result });
    }

    [HttpGet("campaigns")]
    public async Task<IActionResult> Campaigns([FromQuery] string? type, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var query = db.Campaigns.Include(x => x.Images).AsNoTracking().Where(x => x.GroupCode == groupCode && SupportedContentTypes.Contains(x.CampaignType) && (x.Status == "Published" || x.Status == "Scheduled") && x.StartDateUtc <= now && x.EndDateUtc >= now);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(x => x.CampaignType == type);
        var rows = await query.OrderByDescending(x => x.UpdatedAtUtc).ToListAsync(token);
        return Ok(new { success = true, count = rows.Count, data = rows.Select(x => new { id=x.Id,contentType=x.CampaignType,x.Title,x.Header,description=x.Body,fromDate=x.StartDateUtc,toDate=x.EndDateUtc,resultImage=x.Images.OrderBy(i=>i.SortOrder).Select(i=>assetUrls.ToAbsolute(i.ImageUrl, Request)).ToArray() }) });
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var groupCode = await audience.GetConfiguredGroupCodeAsync(token);
        var active = await db.Campaigns.Include(x => x.Images).AsNoTracking().Where(x => x.GroupCode == groupCode && SupportedContentTypes.Contains(x.CampaignType) && (x.Status == "Published" || x.Status == "Scheduled") && x.StartDateUtc <= now && x.EndDateUtc >= now).ToListAsync(token);
        var counts = active.GroupBy(x => x.CampaignType).ToDictionary(x => x.Key, x => new { records = x.Count(), images = x.Sum(c => c.Images.Count) });
        return Ok(SupportedContentTypes.Select(type => new { contentType = type, records = counts.TryGetValue(type,out var value)?value.records:0, activeImages = counts.TryGetValue(type,out value)?value.images:0 }));
    }
}

public record ContentImageResponse(long ImageId,long CampaignId,string ContentType,string Title,string? Header,string? Description,DateTime FromDate,DateTime ToDate,int? BranchId,int SortOrder,bool IsPrimary,string ResultImage);
