using QabayelMall.API.Services;
using Microsoft.AspNetCore.Mvc;
namespace QabayelMall.API.Controllers;
public class CustomersController(CoreEdgeCustomerService coreEdge, LoyaltyCustomerAudience audience,
    CustomerAppManagementService appManagement) : Controller
{
    public async Task<IActionResult> Index(string? search, CancellationToken token)
    {
        try
        {
            var group = await audience.ResolveGroupAsync(token);
            ViewBag.Source = $"CoreEdge ERP · {group.GroupCode}";
            ViewBag.Search = search;
            var customers = await coreEdge.GetCustomersAsync(group, search, token);
            var states = await appManagement.GetStatesAsync(group, token);
            return View(customers.Select(customer => states.TryGetValue(customer.Id, out var state)
                ? customer with { ShowFlyers = state.ShowFlyers, ShowOffers = state.ShowOffers,
                    LastSeenAtUtc = state.LastSeenAtUtc }
                : customer).ToList());
        }
        catch (Exception ex)
        {
            ViewBag.Error = $"Customer data is unavailable for the configured loyalty group: {ex.Message}";
            return View(Array.Empty<Models.CustomerSummary>());
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetActive(long id, bool active, CancellationToken token)
    {
        var group = await audience.ResolveGroupAsync(token);
        if (!await appManagement.SetAccountActiveAsync(group, id, active, token)) return NotFound();
        TempData["Success"] = active ? "Customer account reactivated." : "Customer account deactivated and sessions revoked.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetVisibility(long id, bool showFlyers, bool showOffers, CancellationToken token)
    {
        var group = await audience.ResolveGroupAsync(token);
        if (!await appManagement.SetVisibilityAsync(group, id, showFlyers, showOffers, token)) return NotFound();
        TempData["Success"] = "Customer content visibility updated.";
        return RedirectToAction(nameof(Index));
    }
}
