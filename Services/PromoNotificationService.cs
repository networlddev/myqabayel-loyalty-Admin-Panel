using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MyQabayel.Admin.Data;
using MyQabayel.Admin.Models;

namespace MyQabayel.Admin.Services;

public sealed class PromoNotificationService(AdminDbContext db, IConfiguration configuration,
    LoyaltyCurrencyService currencies)
{
    public static bool IsValidTemplate(string? template, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > maxLength) return false;
        if (Regex.IsMatch(template, @"GxPrv\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        string sample = Render(template, new string('N', 80),
            "QAR 99,999,999.99 off", "30 Nov 2026");
        return sample.Length <= maxLength && !sample.Contains('{') && !sample.Contains('}');
    }

    private static string Render(string template, string name,
        string discount, string expiry) => Regex.Replace(template.Trim(),
            @"\{(name|discount|expiry)\}", match => match.Groups[1].Value.ToLowerInvariant() switch
            {
                "name" => name,
                "discount" => discount,
                _ => expiry
            }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task QueueCampaignAsync(PromoMasterSummary promo,
        IReadOnlyList<PromoIssuedCode> issuedCodes, LoyaltyGroupSnapshot group,
        string environment, string titleTemplate, string messageTemplate,
        CancellationToken token, string? imageUrl = null)
    {
        if (environment is not ("Development" or "Production"))
            throw new ArgumentException("Choose Development or Production.", nameof(environment));
        if (issuedCodes.Count is < 1 or > 20_000
            || issuedCodes.Any(x => x.VoucherNo <= 0)
            || issuedCodes.Select(x => x.VoucherNo).Distinct().Count() != issuedCodes.Count
            || issuedCodes.Any(x => x.Status != "Issued"))
            throw new InvalidOperationException("The notification audience is empty or too large.");
        if (!IsValidTemplate(titleTemplate, 150) || !IsValidTemplate(messageTemplate, 500))
            throw new InvalidOperationException("The notification title or message template is invalid.");

        string currencyCode = await currencies.GetCodeAsync(group.GroupCode, token);
        var messages = issuedCodes.Select(issued =>
        {
            string name = (issued.CustomerName ?? "").Trim();
            if (name.Length > 80) name = name[..80];
            if (name.Length == 0) name = "customer";
            string discount = issued.Amount > 0
                ? $"{LoyaltyCurrencyService.FormatAmount(issued.Amount, currencyCode)} off"
                : $"{issued.Percentage}% off";
            string expiry = issued.Expiry.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            return new
            {
                voucherNo = issued.VoucherNo,
                customerId = issued.CustomerId,
                title = Render(titleTemplate, name, discount, expiry),
                message = Render(messageTemplate, name, discount, expiry)
            };
        }).ToArray();
        if (messages.Any(x => x.title.Length > 150 || x.message.Length > 500))
            throw new InvalidOperationException("The notification is too long after inserting customer details.");

        string connectionString = configuration.GetConnectionString("AdminDb")
            ?? throw new InvalidOperationException("ConnectionStrings:AdminDb is not configured.");
        await using var connection = new SqlConnection(connectionString);
        await using var command = new SqlCommand("""
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            CREATE TABLE #Messages(VoucherNo BIGINT NOT NULL PRIMARY KEY, CustomerId BIGINT NOT NULL,
                Title NVARCHAR(150) NOT NULL, Message NVARCHAR(500) NOT NULL);
            INSERT #Messages(VoucherNo,CustomerId,Title,Message)
            SELECT VoucherNo,CustomerId,Title,Message FROM OPENJSON(@Messages)
            WITH (VoucherNo BIGINT '$.voucherNo', CustomerId BIGINT '$.customerId', Title NVARCHAR(150) '$.title',
                  Message NVARCHAR(500) '$.message');
            IF (SELECT COUNT(*) FROM #Messages) <> @Expected
                THROW 51470, 'The personalized notification audience is invalid.', 1;
            CREATE TABLE #Created(NotificationId BIGINT NOT NULL, CustomerId BIGINT NOT NULL);
            MERGE dbo.AdminAppNotifications AS Target
            USING #Messages AS Source ON 1=0
            WHEN NOT MATCHED THEN INSERT
                (Title,Message,ScheduledAtUtc,ExpiresAtUtc,GroupCode,CCode,LoyaltyGroupId,
                 ImageUrl,TargetType,Environment,AudienceType,DispatchStatus,Status)
            VALUES (Source.Title,Source.Message,@Now,@Expires,@GroupCode,@CCode,@GroupId,
                    @ImageUrl,N'Notifications',@Environment,N'Selected',N'Pending',N'Published')
            OUTPUT inserted.Id,Source.CustomerId INTO #Created(NotificationId,CustomerId);
            INSERT dbo.AdminAppNotificationRecipients(NotificationId,CustomerId)
            SELECT NotificationId,CustomerId FROM #Created;
            COMMIT TRANSACTION;
            """, connection) { CommandTimeout = 120 };
        command.Parameters.Add("@Messages", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(messages);
        command.Parameters.Add("@Expected", SqlDbType.Int).Value = messages.Length;
        command.Parameters.Add("@Now", SqlDbType.DateTime2).Value = DateTime.UtcNow;
        command.Parameters.Add("@Expires", SqlDbType.DateTime2).Value =
            DateTime.SpecifyKind(promo.Validity, DateTimeKind.Local).ToUniversalTime();
        command.Parameters.Add("@GroupCode", SqlDbType.NVarChar, 100).Value = group.GroupCode;
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        command.Parameters.Add("@ImageUrl", SqlDbType.NVarChar, 1000).Value = (object?)imageUrl ?? DBNull.Value;
        command.Parameters.Add("@Environment", SqlDbType.NVarChar, 20).Value = environment;
        await connection.OpenAsync(token);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task QueueAsync(PromoMasterSummary promo, PromoIssuedCode issued,
        LoyaltyGroupSnapshot group, string environment, CancellationToken token,
        string? imageUrl = null)
    {
        if (environment is not ("Development" or "Production"))
            throw new ArgumentException("Choose Development or Production.", nameof(environment));
        if (issued.Status != "Issued")
            throw new InvalidOperationException("Only an active, unused PromoCode can be sent.");

        string currencyCode = await currencies.GetCodeAsync(group.GroupCode, token);
        string discount = issued.Amount > 0
            ? $"{LoyaltyCurrencyService.FormatAmount(issued.Amount, currencyCode)} off"
            : $"{issued.Percentage}% off";
        string title = $"Your {promo.Name} PromoCode";
        var now = DateTime.UtcNow;
        db.LoyaltyNotifications.Add(new LoyaltyNotification
        {
            Title = title[..Math.Min(title.Length, 150)],
            Message = ComposeMessage(promo, discount, issued.Expiry),
            ScheduledAtUtc = now,
            ExpiresAtUtc = DateTime.SpecifyKind(issued.Expiry, DateTimeKind.Local).ToUniversalTime(),
            GroupCode = group.GroupCode,
            CCode = group.CCode,
            LoyaltyGroupId = group.LoyaltyGroupId,
            Environment = environment,
            ImageUrl = imageUrl,
            AudienceType = "Selected",
            Recipients = [new LoyaltyNotificationRecipient { CustomerId = issued.CustomerId }],
            Status = "Published",
            DispatchStatus = "Pending"
        });
        await db.SaveChangesAsync(token);
    }

    public async Task QueueAgainAsync(PromoMasterSummary promo,
        IReadOnlyList<long> customerIds, LoyaltyGroupSnapshot group,
        string environment, string? imageUrl, CancellationToken token)
    {
        if (environment is not ("Development" or "Production"))
            throw new ArgumentException("Choose Development or Production.", nameof(environment));
        if (customerIds.Count is < 1 or > 20_000)
            throw new InvalidOperationException("No active unused PromoCodes are available to notify.");
        var notification = new LoyaltyNotification
        {
            Title = $"Your {promo.Name} PromoCode"[..Math.Min($"Your {promo.Name} PromoCode".Length, 150)],
            Message = ComposeMessage(promo, null, promo.Validity),
            ScheduledAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.SpecifyKind(promo.Validity, DateTimeKind.Local).ToUniversalTime(),
            GroupCode = group.GroupCode,
            CCode = group.CCode,
            LoyaltyGroupId = group.LoyaltyGroupId,
            Environment = environment,
            ImageUrl = imageUrl,
            AudienceType = "Selected",
            Status = "Published",
            DispatchStatus = "Pending"
        };
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        db.LoyaltyNotifications.Add(notification);
        await db.SaveChangesAsync(token);
        var recipientJson = new SqlParameter("@RecipientJson", SqlDbType.NVarChar, -1)
        { Value = JsonSerializer.Serialize(customerIds) };
        var notificationId = new SqlParameter("@NotificationId", SqlDbType.BigInt)
        { Value = notification.Id };
        await db.Database.ExecuteSqlRawAsync("""
            INSERT dbo.AdminAppNotificationRecipients(NotificationId,CustomerId)
            SELECT @NotificationId, TRY_CONVERT(bigint,[value])
            FROM OPENJSON(@RecipientJson)
            WHERE [type]=2 AND TRY_CONVERT(bigint,[value])>0;
            """, [notificationId, recipientJson], token);
        await transaction.CommitAsync(token);
    }

    private static string ComposeMessage(PromoMasterSummary promo, string? discount,
        DateTime validity)
    {
        string caption = promo.Caption.Trim();
        if (caption.Length > 120) caption = caption[..120] + "…";
        string body = promo.Body?.Trim() ?? "";
        if (body.Length > 120) body = body[..120] + "…";
        string offer = discount is null ? "Your PromoCode and discount are ready"
            : $"Your {discount} PromoCode is ready";
        string message = $"{caption}. {offer}. Valid through {validity:dd MMM yyyy}.";
        if (body.Length > 0) message += $" {body}";
        message += " Open PromoCodes in the app for details.";
        return message.Length <= 500 ? message : message[..500];
    }
}
