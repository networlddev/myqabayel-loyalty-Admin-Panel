namespace MyQabayel.Admin.Models;
public class DashboardViewModel
{
    public IReadOnlyList<Campaign> Campaigns { get; init; } = [];
    public int TotalCampaigns { get; init; }
    public int ActiveCampaigns { get; init; }
    public int DraftCampaigns { get; init; }
    public int ScheduledCampaigns { get; init; }
    public int TotalNotifications { get; init; }
    public int DraftNotifications { get; init; }
    public int QueuedNotifications { get; init; }
    public string GroupCode { get; init; } = "QABAYEL";
    public string PushEnvironment { get; init; } = "Development";
    public bool PushConfigured { get; init; }
    public int LastSevenDaysActivity { get; init; }
    public decimal? ActivityChangePercent { get; init; }
    public IReadOnlyList<DashboardDailyActivity> DailyActivity { get; init; } = [];
    public CoreEdgeDashboardMetrics? CoreEdgeMetrics { get; init; }
    public Services.CustomerActivityCounts? CustomerActivity { get; init; }
    public string? CustomerActivityMessage { get; init; }
    public string? CoreEdgeMessage { get; init; }
    public CustomerPerformanceSeries? CustomerMonthPerformance { get; init; }
    public CustomerPerformanceSeries? CustomerYearPerformance { get; init; }
    public string? CustomerPerformanceMessage { get; init; }
    public string? DatabaseMessage { get; init; }
}

public record DashboardDailyActivity(string Label, int Count);
public record CoreEdgeDashboardMetrics(long TotalCustomers, long EnabledCustomers, long LoyaltyInvoices, decimal LoyaltySales, decimal TotalPoints);
public record CustomerRegistrationDay(DateTime Date, long Count);
public record CustomerPerformancePoint(string Label, long Count);
public record CustomerPerformanceSeries(IReadOnlyList<CustomerPerformancePoint> Points, long Total, decimal? ChangePercent);
