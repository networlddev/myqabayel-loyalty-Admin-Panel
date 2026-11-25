using QabayelMall.API.Data;
using QabayelMall.API.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace QabayelMall.API.Controllers;
public class DashboardController(AdminDbContext db, Services.CoreEdgeCustomerService coreEdge,
    Services.CustomerAppManagementService appManagement,
    Services.LoyaltyCustomerAudience audience, IConfiguration configuration) : Controller
{
    public async Task<IActionResult> Index(CancellationToken token)
    {
        try
        {
            var group = await audience.ResolveGroupAsync(token);
            var supportedTypes = new[] { "Flyer", "Offer", "Advertisement" };
            var query = db.Campaigns.AsNoTracking()
                .Where(x => x.GroupCode == group.GroupCode && supportedTypes.Contains(x.CampaignType));
            var pushEnvironment = await audience.GetConfiguredPushEnvironmentAsync(token);
            var notifications = db.LoyaltyNotifications.AsNoTracking()
                .Where(x => x.GroupCode == group.GroupCode && x.Environment == pushEnvironment);
            var credentialPath = configuration[$"PushNotifications:{pushEnvironment}:CredentialPath"];
            var pushConfigured = !string.IsNullOrWhiteSpace(credentialPath)
                && System.IO.File.Exists(credentialPath)
                && !string.IsNullOrWhiteSpace(configuration[$"PushNotifications:{pushEnvironment}:ExpectedProjectId"]);
            var todayUtc = DateTime.UtcNow.Date;
            var currentStart = todayUtc.AddDays(-6);
            var previousStart = currentStart.AddDays(-7);
            var activityDates = await query.Where(x => x.UpdatedAtUtc >= previousStart)
                .Select(x => x.UpdatedAtUtc).ToListAsync(token);
            var currentCount = activityDates.Count(x => x >= currentStart);
            var previousCount = activityDates.Count(x => x >= previousStart && x < currentStart);
            decimal? change = previousCount == 0
                ? (currentCount == 0 ? 0 : null)
                : Math.Round((currentCount - previousCount) * 100m / previousCount, 1);
            var daily = Enumerable.Range(0, 7).Select(offset =>
            {
                var day = currentStart.AddDays(offset);
                return new DashboardDailyActivity(day.ToString("ddd"), activityDates.Count(x => x.Date == day));
            }).ToList();

            CoreEdgeDashboardMetrics? coreEdgeMetrics = null;
            string? coreEdgeMessage = null;
            try { coreEdgeMetrics = await coreEdge.GetDashboardMetricsAsync(group, token); }
            catch (Exception ex) { coreEdgeMessage = $"CoreEdge live metrics are unavailable: {ex.Message}"; }

            Services.CustomerActivityCounts? customerActivity = null;
            string? customerActivityMessage = null;
            try { customerActivity = await appManagement.GetActivityCountsAsync(group, token); }
            catch (Exception) { customerActivityMessage = "App activity is unavailable until the customer app settings table is installed."; }

            CustomerPerformanceSeries? monthPerformance = null;
            CustomerPerformanceSeries? yearPerformance = null;
            string? customerPerformanceMessage = null;
            try
            {
                var today = DateTime.Today;
                var monthStart = today.AddDays(-29);
                var firstOfMonth = new DateTime(today.Year, today.Month, 1);
                var yearStart = firstOfMonth.AddMonths(-11);
                var registrationDays = await coreEdge.GetRegistrationActivityAsync(yearStart.AddMonths(-12), today.AddDays(1), group, token);
                var counts = registrationDays.ToDictionary(day => day.Date, day => day.Count);
                long Registrations(DateTime start, DateTime end) => counts.Where(day => day.Key >= start && day.Key < end).Sum(day => day.Value);
                decimal? Change(long current, long previous) => previous == 0
                    ? (current == 0 ? 0 : null)
                    : Math.Round((current - previous) * 100m / previous, 1);

                var monthTotal = Registrations(monthStart, today.AddDays(1));
                var previousMonthTotal = Registrations(monthStart.AddDays(-30), monthStart);
                var monthPoints = Enumerable.Range(0, 6).Select(index =>
                {
                    var start = monthStart.AddDays(index * 5);
                    return new CustomerPerformancePoint(start.ToString("dd MMM"), Registrations(start, start.AddDays(5)));
                }).ToList();
                monthPerformance = new CustomerPerformanceSeries(monthPoints, monthTotal, Change(monthTotal, previousMonthTotal));

                var yearTotal = Registrations(yearStart, today.AddDays(1));
                var previousYearTotal = Registrations(yearStart.AddMonths(-12), today.AddYears(-1).AddDays(1));
                var yearPoints = Enumerable.Range(0, 12).Select(index =>
                {
                    var start = yearStart.AddMonths(index);
                    return new CustomerPerformancePoint(start.ToString("MMM"), Registrations(start, start.AddMonths(1)));
                }).ToList();
                yearPerformance = new CustomerPerformanceSeries(yearPoints, yearTotal, Change(yearTotal, previousYearTotal));
            }
            catch (Exception) { customerPerformanceMessage = "Customer registration trends are unavailable right now."; }

            return View(new DashboardViewModel
            {
                TotalCampaigns = await query.CountAsync(token),
                ActiveCampaigns = await query.CountAsync(x => x.Status == "Published", token),
                DraftCampaigns = await query.CountAsync(x => x.Status == "Draft", token),
                ScheduledCampaigns = await query.CountAsync(x => x.Status == "Scheduled", token),
                TotalNotifications = await notifications.CountAsync(token),
                DraftNotifications = await notifications.CountAsync(x => x.Status == "Draft", token),
                QueuedNotifications = await notifications.CountAsync(x =>
                    (x.Status == "Published" || x.Status == "Scheduled")
                    && (x.DispatchStatus == "Pending" || x.DispatchStatus == "Retry"), token),
                GroupCode = group.GroupCode,
                PushEnvironment = pushEnvironment,
                PushConfigured = pushConfigured,
                LastSevenDaysActivity = currentCount,
                ActivityChangePercent = change,
                DailyActivity = daily,
                CoreEdgeMetrics = coreEdgeMetrics,
                CustomerActivity = customerActivity,
                CustomerActivityMessage = customerActivityMessage,
                CoreEdgeMessage = coreEdgeMessage,
                CustomerMonthPerformance = monthPerformance,
                CustomerYearPerformance = yearPerformance,
                CustomerPerformanceMessage = customerPerformanceMessage,
                Campaigns = await query.Include(x => x.Images).OrderByDescending(x => x.UpdatedAtUtc).Take(10).ToListAsync(token)
            });
        }
        catch (Exception ex) { return View(new DashboardViewModel { DatabaseMessage = $"Admin database setup required: {ex.Message}" }); }
    }
}
