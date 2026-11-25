using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MyQabayel.Admin.Models;

namespace MyQabayel.Admin.Services;

public sealed class PromoEditDraftService(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("AdminDb")
        ?? throw new InvalidOperationException("ConnectionStrings:AdminDb is not configured.");

    public async Task<HashSet<int>> ListCampaignIdsAsync(LoyaltyGroupSnapshot group,
        CancellationToken token)
    {
        const string sql = """
            SELECT CampaignId FROM dbo.AdminPromoEditDrafts
            WHERE CCode=@CCode AND LoyaltyGroupId=@GroupId;
            """;
        var ids = new HashSet<int>();
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    public async Task<(PromoEditDraft Draft, DateTime SavedAtUtc)?> GetAsync(int campaignId,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = """
            SELECT DraftJson, UpdatedAtUtc FROM dbo.AdminPromoEditDrafts
            WHERE CampaignId=@CampaignId AND CCode=@CCode AND LoyaltyGroupId=@GroupId;
            """;
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        AddKey(command, campaignId, group);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var draft = JsonSerializer.Deserialize<PromoEditDraft>(reader.GetString(0))
            ?? throw new InvalidOperationException("The saved promo draft is invalid.");
        return (draft, reader.GetDateTime(1));
    }

    public async Task SaveAsync(int campaignId, PromoEditDraft draft,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            UPDATE dbo.AdminPromoEditDrafts WITH (UPDLOCK, SERIALIZABLE)
               SET DraftJson=@DraftJson, UpdatedAtUtc=SYSUTCDATETIME()
             WHERE CampaignId=@CampaignId AND CCode=@CCode AND LoyaltyGroupId=@GroupId;
            IF @@ROWCOUNT=0
                INSERT dbo.AdminPromoEditDrafts(CampaignId,CCode,LoyaltyGroupId,DraftJson)
                VALUES(@CampaignId,@CCode,@GroupId,@DraftJson);
            COMMIT TRANSACTION;
            """;
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        AddKey(command, campaignId, group);
        command.Parameters.Add("@DraftJson", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(draft);
        await connection.OpenAsync(token);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task DeleteAsync(int campaignId, LoyaltyGroupSnapshot group,
        CancellationToken token)
    {
        const string sql = """
            DELETE dbo.AdminPromoEditDrafts
            WHERE CampaignId=@CampaignId AND CCode=@CCode AND LoyaltyGroupId=@GroupId;
            """;
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        AddKey(command, campaignId, group);
        await connection.OpenAsync(token);
        await command.ExecuteNonQueryAsync(token);
    }

    private static void AddKey(SqlCommand command, int campaignId, LoyaltyGroupSnapshot group)
    {
        command.Parameters.Add("@CampaignId", SqlDbType.Int).Value = campaignId;
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
    }
}
