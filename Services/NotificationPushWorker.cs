using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using QabayelMall.API.Data;
using QabayelMall.API.Models;
using FirebaseAdmin.Messaging;

namespace QabayelMall.API.Services;

public sealed class NotificationPushWorker(
    IServiceScopeFactory scopeFactory,
    FirebasePushSender firebase,
    IConfiguration configuration,
    ILogger<NotificationPushWorker> logger) : BackgroundService
{
    private sealed record Device(long DeviceId, long CustomerId, string FcmToken, string? CustomerName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessDueAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Notification push worker failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdminDbContext>();
        var audience = scope.ServiceProvider.GetRequiredService<LoyaltyCustomerAudience>();
        var groupCode = await audience.GetConfiguredGroupCodeAsync(cancellationToken);
        var environment = await audience.GetConfiguredPushEnvironmentAsync(cancellationToken);
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dbo.AdminAppNotifications SET DispatchStatus = N'Retry'
            WHERE DispatchStatus = N'Sending'
              AND LastPushAttemptAtUtc < {now.AddMinutes(-5)};
            """, cancellationToken);

        var due = await db.LoyaltyNotifications.AsNoTracking()
            .Where(x => x.GroupCode == groupCode && x.Environment == environment
                && (x.Status == "Published" || x.Status == "Scheduled")
                && (x.DispatchStatus == "Pending" || x.DispatchStatus == "Retry")
                && x.ScheduledAtUtc <= now
                && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now)
                && x.PushAttemptCount < 5
                && (x.LastPushAttemptAtUtc == null || x.LastPushAttemptAtUtc < now.AddMinutes(-1)))
            .OrderBy(x => x.ScheduledAtUtc).Take(20).ToListAsync(cancellationToken);

        foreach (var candidate in due)
        {
            if (!firebase.IsConfigured(candidate.Environment)) continue;
            int claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE dbo.AdminAppNotifications
                   SET DispatchStatus = N'Sending', PushAttemptCount = PushAttemptCount + 1,
                       LastPushAttemptAtUtc = {now}, UpdatedAtUtc = {now}
                 WHERE Id = {candidate.Id} AND GroupCode = {groupCode}
                   AND Environment = {environment}
                   AND EXISTS (SELECT 1 FROM dbo.AdminPanelSettings
                               WHERE Id = 1 AND PushEnvironment = {environment})
                   AND DispatchStatus IN (N'Pending', N'Retry')
                   AND Status IN (N'Published', N'Scheduled')
                   AND ScheduledAtUtc <= {now};
                """, cancellationToken);
            if (claimed != 1) continue;
            try
            {
                var notification = await db.LoyaltyNotifications.AsNoTracking()
                    .SingleAsync(x => x.Id == candidate.Id, cancellationToken);
                await DeliverAsync(db, notification, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Push dispatch failed for notification {NotificationId}", candidate.Id);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE dbo.AdminAppNotifications SET DispatchStatus = N'Retry'
                    WHERE Id = {candidate.Id} AND DispatchStatus = N'Sending';
                    """, cancellationToken);
            }
        }
    }

    private async Task DeliverAsync(AdminDbContext db, LoyaltyNotification notification,
        CancellationToken cancellationToken)
    {
        HashSet<long>? selected = notification.AudienceType != "All"
            ? (await db.NotificationRecipients.AsNoTracking()
                .Where(x => x.NotificationId == notification.Id)
                .Select(x => x.CustomerId).ToListAsync(cancellationToken)).ToHashSet()
            : null;
        var devices = await LoadDevicesAsync(notification, selected, cancellationToken);
        if (notification.TargetType is "Flyer" or "Offer")
        {
            var blocked = await LoadBlockedCustomersAsync(notification, cancellationToken);
            devices.RemoveAll(device => blocked.Contains(device.CustomerId));
        }
        var byId = devices.ToDictionary(x => x.DeviceId);
        var existing = await db.NotificationDeliveries
            .Where(x => x.NotificationId == notification.Id).ToListAsync(cancellationToken);
        var existingIds = existing.Select(x => x.DeviceId).ToHashSet();
        foreach (var device in devices.Where(x => !existingIds.Contains(x.DeviceId)))
            db.NotificationDeliveries.Add(new LoyaltyNotificationDelivery
            {
                NotificationId = notification.Id, DeviceId = device.DeviceId
            });
        await db.SaveChangesAsync(cancellationToken);
        var deliveries = await db.NotificationDeliveries
            .Where(x => x.NotificationId == notification.Id).ToListAsync(cancellationToken);

        foreach (var delivery in deliveries.Where(x => x.Status is "Pending" or "Retry"))
        {
            if (!byId.TryGetValue(delivery.DeviceId, out var device))
            {
                delivery.Status = "Skipped";
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }
            try
            {
                await firebase.SendAsync(notification.Environment, device.FcmToken,
                    notification.Id, notification.GroupCode,
                    CustomerNotificationText.ForCustomer(notification.Title, device.CustomerName),
                    CustomerNotificationText.ForCustomer(notification.Message, device.CustomerName),
                    notification.ImageUrl,
                    notification.TargetType, notification.TargetId, cancellationToken);
                delivery.Status = "Sent";
                delivery.SentAtUtc = DateTime.UtcNow;
                delivery.LastError = null;
            }
            catch (FirebaseMessagingException ex) when (ex.MessagingErrorCode == MessagingErrorCode.Unregistered)
            {
                delivery.AttemptCount++;
                delivery.Status = "Failed";
                delivery.LastError = "FCM token is no longer registered.";
                await DeactivateInvalidTokenAsync(device, notification, cancellationToken);
                logger.LogInformation("Deactivated unregistered FCM device {DeviceId}", device.DeviceId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                delivery.AttemptCount++;
                delivery.Status = delivery.AttemptCount >= 3 ? "Failed" : "Retry";
                delivery.LastError = ex.Message[..Math.Min(ex.Message.Length, 500)];
                logger.LogWarning(ex, "FCM delivery failed for notification {NotificationId}, device {DeviceId}",
                    notification.Id, device.DeviceId);
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        string finalStatus = deliveries.Count == 0 ? "NoDevices"
            : deliveries.Any(x => x.Status == "Retry") ? "Retry"
            : deliveries.Any(x => x.Status == "Failed") ? "Failed" : "Sent";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE dbo.AdminAppNotifications
               SET DispatchStatus = {finalStatus},
                   PushCompletedAtUtc = {(finalStatus == "Retry" ? (DateTime?)null : DateTime.UtcNow)},
                   UpdatedAtUtc = {DateTime.UtcNow}
             WHERE Id = {notification.Id} AND DispatchStatus = N'Sending';
            """, cancellationToken);
    }

    private async Task<HashSet<long>> LoadBlockedCustomersAsync(LoyaltyNotification notification,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CustomerId FROM dbo.AdminCustomerAppSettings
            WHERE GroupCode=@GroupCode AND CCode=@CCode AND LoyaltyGroupId=@GroupId
              AND ((@TargetType='Flyer' AND ShowFlyers=0)
                OR (@TargetType='Offer' AND ShowOffers=0));
            """;
        await using var connection = new SqlConnection(configuration.GetConnectionString("AdminDb")
            ?? throw new InvalidOperationException("AdminDb is not configured."));
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@GroupCode", SqlDbType.NVarChar, 100).Value = notification.GroupCode;
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = notification.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = notification.LoyaltyGroupId;
        command.Parameters.Add("@TargetType", SqlDbType.NVarChar, 40).Value = notification.TargetType;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var blocked = new HashSet<long>();
        while (await reader.ReadAsync(cancellationToken)) blocked.Add(reader.GetInt64(0));
        return blocked;
    }

    private async Task<List<Device>> LoadDevicesAsync(LoyaltyNotification notification,
        HashSet<long>? selected, CancellationToken cancellationToken)
    {
        string connectionString = configuration.GetConnectionString("ErpDb")
            ?? throw new InvalidOperationException("ConnectionStrings:ErpDb is not configured.");
        const string sql = """
            SELECT D.DeviceId, D.UserId, D.FcmToken, C.Name
            FROM dbo.NLoyaltyCustomerDevice D
            INNER JOIN dbo.NPrivilegeCustomer C
                ON C.Id = D.UserId AND C.Ccode = D.CCode
               AND C.LoyaltyGroupId = D.LoyaltyGroupId AND C.Active = 1
            WHERE D.CCode = @CCode AND D.LoyaltyGroupId = @GroupId
              AND D.Environment = @Environment AND D.IsActive = 1;
            """;
        var result = new List<Device>();
        await using var connection = new SqlConnection(connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = notification.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = notification.LoyaltyGroupId;
        command.Parameters.Add("@Environment", SqlDbType.NVarChar, 20).Value = notification.Environment;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long customerId = reader.GetInt64(1);
            if (selected is null || selected.Contains(customerId))
                result.Add(new Device(reader.GetInt64(0), customerId, reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return result;
    }

    private async Task DeactivateInvalidTokenAsync(Device device, LoyaltyNotification notification,
        CancellationToken cancellationToken)
    {
        string connectionString = configuration.GetConnectionString("ErpDb")
            ?? throw new InvalidOperationException("ConnectionStrings:ErpDb is not configured.");
        const string sql = """
            UPDATE dbo.NLoyaltyCustomerDevice
               SET IsActive = 0, UnregisteredAtUtc = SYSUTCDATETIME(),
                   UpdatedAtUtc = SYSUTCDATETIME()
             WHERE DeviceId = @DeviceId AND UserId = @CustomerId
               AND CCode = @CCode AND LoyaltyGroupId = @GroupId
               AND Environment = @Environment AND FcmToken = @FcmToken;
            """;
        await using var connection = new SqlConnection(connectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@DeviceId", SqlDbType.BigInt).Value = device.DeviceId;
        command.Parameters.Add("@CustomerId", SqlDbType.BigInt).Value = device.CustomerId;
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = notification.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = notification.LoyaltyGroupId;
        command.Parameters.Add("@Environment", SqlDbType.NVarChar, 20).Value = notification.Environment;
        command.Parameters.Add("@FcmToken", SqlDbType.NVarChar, 2048).Value = device.FcmToken;
        await connection.OpenAsync(cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
