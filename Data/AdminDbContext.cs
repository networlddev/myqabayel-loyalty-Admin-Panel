using MyQabayel.Admin.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MyQabayel.Admin.Data;

public class AdminDbContext(DbContextOptions<AdminDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignImage> CampaignImages => Set<CampaignImage>();
    public DbSet<LoyaltyNotification> LoyaltyNotifications => Set<LoyaltyNotification>();
    public DbSet<LoyaltyNotificationRecipient> NotificationRecipients => Set<LoyaltyNotificationRecipient>();
    public DbSet<LoyaltyNotificationDelivery> NotificationDeliveries => Set<LoyaltyNotificationDelivery>();
    public DbSet<LoyaltyNotificationImage> NotificationImages => Set<LoyaltyNotificationImage>();
    public DbSet<AdminPanelSetting> AdminPanelSettings => Set<AdminPanelSetting>();
    public DbSet<LoyaltyCurrencySetting> LoyaltyCurrencies => Set<LoyaltyCurrencySetting>();
    public DbSet<FirebaseIosClientConfiguration> FirebaseIosClientConfigurations => Set<FirebaseIosClientConfiguration>();
    public DbSet<AppContentLinks> AppContentLinks => Set<AppContentLinks>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Campaign>(entity =>
        {
            entity.ToTable("AdminPanelCampaigns");
            entity.HasIndex(x => new { x.GroupCode, x.CampaignType, x.Status });
            entity.Property(x => x.Id).UseIdentityColumn();
        });
        builder.Entity<AdminPanelSetting>(entity =>
        {
            entity.ToTable("AdminPanelSettings");
            entity.HasKey(x => x.Id);
        });
        builder.Entity<LoyaltyCurrencySetting>(entity =>
        {
            entity.ToTable("AdminLoyaltyCurrencies");
            entity.HasKey(x => x.GroupCode);
        });
        builder.Entity<FirebaseIosClientConfiguration>(entity =>
        {
            entity.ToTable("AdminFirebaseIosClientConfigurations");
            entity.HasKey(x => new { x.GroupCode, x.Environment });
        });
        builder.Entity<AppContentLinks>(entity =>
        {
            entity.ToTable("AdminAppContentLinks");
            entity.HasKey(x => x.GroupCode);
        });
        builder.Entity<CampaignImage>(entity =>
        {
            entity.ToTable("AdminPanelCampaignImages");
            entity.HasOne(x => x.Campaign).WithMany(x => x.Images).HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.CampaignId, x.SortOrder });
        });
        builder.Entity<LoyaltyNotification>(entity =>
        {
            entity.ToTable("AdminAppNotifications");
            entity.Property(x => x.Id).UseIdentityColumn();
            entity.HasIndex(x => new { x.Status, x.ScheduledAtUtc });
        });
        builder.Entity<LoyaltyNotificationRecipient>(entity =>
        {
            entity.ToTable("AdminAppNotificationRecipients");
            entity.HasKey(x => new { x.NotificationId, x.CustomerId });
            entity.HasOne(x => x.Notification).WithMany(x => x.Recipients)
                .HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<LoyaltyNotificationDelivery>(entity =>
        {
            entity.ToTable("AdminAppNotificationDeliveries");
            entity.HasKey(x => new { x.NotificationId, x.DeviceId });
            entity.HasOne(x => x.Notification).WithMany(x => x.Deliveries)
                .HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<LoyaltyNotificationImage>(entity =>
        {
            entity.ToTable("AdminAppNotificationImages");
            entity.Property(x => x.Id).UseIdentityColumn();
            entity.HasOne(x => x.Notification).WithMany(x => x.Images)
                .HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.NotificationId, x.SortOrder }).IsUnique();
        });
    }
}
