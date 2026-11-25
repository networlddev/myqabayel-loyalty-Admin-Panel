using Microsoft.AspNetCore.Mvc;
using MyQabayel.Admin.Services;

namespace MyQabayel.Admin.Controllers;

public sealed class EngagementController(EngagementMonitorService monitor,
    LoyaltyCustomerAudience groups) : Controller
{
    public async Task<IActionResult> Index(string? type, string? search, long? customerId,
        int page = 1, CancellationToken token = default)
    {
        var normalized = EngagementMonitorService.NormalizeType(type);
        if (!string.IsNullOrWhiteSpace(type) && normalized is null) return BadRequest();
        var groupCode = await groups.GetConfiguredGroupCodeAsync(token);
        return View(await monitor.ListAsync(groupCode, normalized, search,
            customerId is > 0 ? customerId : null, page, token));
    }

    public async Task<IActionResult> Details(string type, long id,
        int reactionPage = 1, int commentPage = 1, CancellationToken token = default)
    {
        var normalized = EngagementMonitorService.NormalizeType(type);
        if (normalized is null || id <= 0) return NotFound();
        var groupCode = await groups.GetConfiguredGroupCodeAsync(token);
        var model = await monitor.DetailsAsync(groupCode, normalized, id,
            reactionPage, commentPage, token);
        return model is null ? NotFound() : View(model);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetCommentVisibility(string type, long id,
        long commentId, bool visible, CancellationToken token)
    {
        var normalized = EngagementMonitorService.NormalizeType(type);
        if (normalized is null || id <= 0 || commentId <= 0) return BadRequest();
        var groupCode = await groups.GetConfiguredGroupCodeAsync(token);
        if (!await monitor.SetCommentVisibilityAsync(groupCode, normalized, id,
                commentId, visible, token)) return NotFound();
        TempData["Success"] = visible ? "Comment restored in the app." : "Comment hidden from the app.";
        return RedirectToAction(nameof(Details), new { type = normalized, id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveReaction(string type, long id,
        long reactionId, CancellationToken token)
    {
        var normalized = EngagementMonitorService.NormalizeType(type);
        if (normalized is null || id <= 0 || reactionId <= 0) return BadRequest();
        var groupCode = await groups.GetConfiguredGroupCodeAsync(token);
        if (!await monitor.RemoveReactionAsync(groupCode, normalized, id,
                reactionId, token)) return NotFound();
        TempData["Success"] = "Reaction removed.";
        return RedirectToAction(nameof(Details), new { type = normalized, id });
    }
}
