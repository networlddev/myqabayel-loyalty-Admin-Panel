using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MyQabayel.Admin.Models;

namespace MyQabayel.Admin.Services;

public sealed record PromoCreateDraft(
    string Name, string Caption, string? Body, string CampaignCode,
    DateTime ValidityDate, int[] DepartmentIds, string AudienceType,
    string? SelectedCustomerIds, string DiscountType, decimal? DiscountValue,
    bool SendNotification, string NotificationEnvironment, string? ImageUrl,
    string? NotificationImageUrl = null,
    string? NotificationTitle = null, string? NotificationMessage = null);

public sealed class PromoCampaignContentService(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("AdminDb")
        ?? throw new InvalidOperationException("ConnectionStrings:AdminDb is not configured.");

    public async Task<IReadOnlyList<PromoCreateDraftSummary>> ListDraftsAsync(
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = "SELECT Id,DraftJson,UpdatedAtUtc FROM dbo.AdminPromoCreateDrafts WHERE CCode=@CCode AND LoyaltyGroupId=@GroupId ORDER BY UpdatedAtUtc DESC";
        var result = new List<PromoCreateDraftSummary>();
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        AddGroup(command, group);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var draft = JsonSerializer.Deserialize<PromoCreateDraft>(reader.GetString(1));
            if (draft is not null) result.Add(new(reader.GetInt32(0), draft.Name,
                draft.CampaignCode, reader.GetDateTime(2)));
        }
        return result;
    }

    public async Task<PromoCreateDraft?> GetDraftAsync(int draftId,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = "SELECT DraftJson FROM dbo.AdminPromoCreateDrafts WHERE Id=@Id AND CCode=@CCode AND LoyaltyGroupId=@GroupId";
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = draftId;
        AddGroup(command, group);
        await connection.OpenAsync(token);
        var json = await command.ExecuteScalarAsync(token) as string;
        return json is null ? null : JsonSerializer.Deserialize<PromoCreateDraft>(json);
    }

    public async Task<int> SaveDraftAsync(int? draftId, PromoCreateDraft draft,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = """
            SET XACT_ABORT ON;
            IF @Id IS NULL
            BEGIN
                INSERT dbo.AdminPromoCreateDrafts(CCode,LoyaltyGroupId,DraftJson)
                VALUES(@CCode,@GroupId,@DraftJson);
                SELECT CONVERT(int,SCOPE_IDENTITY());
            END
            ELSE
            BEGIN
                UPDATE dbo.AdminPromoCreateDrafts SET DraftJson=@DraftJson,UpdatedAtUtc=SYSUTCDATETIME()
                WHERE Id=@Id AND CCode=@CCode AND LoyaltyGroupId=@GroupId;
                IF @@ROWCOUNT=0 THROW 51461, 'The promo draft was not found in this loyalty group.', 1;
                SELECT @Id;
            END
            """;
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = draftId is > 0 ? draftId : DBNull.Value;
        command.Parameters.Add("@DraftJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(draft);
        AddGroup(command, group);
        await connection.OpenAsync(token);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    public async Task DeleteDraftAsync(int draftId, LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = "DELETE dbo.AdminPromoCreateDrafts WHERE Id=@Id AND CCode=@CCode AND LoyaltyGroupId=@GroupId";
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = draftId;
        AddGroup(command, group);
        await connection.OpenAsync(token);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<string?> GetImageUrlAsync(int campaignId,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = "SELECT ImageUrl FROM dbo.AdminPromoCampaignContent WHERE CampaignId=@Id AND CCode=@CCode AND LoyaltyGroupId=@GroupId";
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = campaignId;
        AddGroup(command, group);
        await connection.OpenAsync(token);
        return await command.ExecuteScalarAsync(token) as string;
    }

    public async Task SaveImageUrlAsync(int campaignId, string? imageUrl,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            UPDATE dbo.AdminPromoCampaignContent WITH (UPDLOCK,SERIALIZABLE)
            SET ImageUrl=@ImageUrl,UpdatedAtUtc=SYSUTCDATETIME()
            WHERE CampaignId=@Id AND CCode=@CCode AND LoyaltyGroupId=@GroupId;
            IF @@ROWCOUNT=0
                INSERT dbo.AdminPromoCampaignContent(CampaignId,CCode,LoyaltyGroupId,ImageUrl)
                VALUES(@Id,@CCode,@GroupId,@ImageUrl);
            COMMIT TRANSACTION;
            """;
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = campaignId;
        command.Parameters.Add("@ImageUrl", SqlDbType.NVarChar, 1000).Value =
            (object?)imageUrl ?? DBNull.Value;
        AddGroup(command, group);
        await connection.OpenAsync(token);
        await command.ExecuteNonQueryAsync(token);
    }

    private static void AddGroup(SqlCommand command, LoyaltyGroupSnapshot group)
    {
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
    }
}
