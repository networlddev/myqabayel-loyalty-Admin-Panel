using System.Data;
using QabayelMall.API.Models;
using Microsoft.Data.SqlClient;

namespace QabayelMall.API.Services;

public class ErpPublishingService(IConfiguration configuration)
{
    public async Task<long> PublishAsync(Campaign campaign, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("ErpDb")
            ?? throw new InvalidOperationException("ERP connection is not configured.");
        var isFlyer = campaign.CampaignType == "Flyer";
        var isRepublish = campaign.ErpRecordId.HasValue;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var saveProcedure = isRepublish
            ? (isFlyer ? "Admin_Flyer_Update" : "Admin_Offer_Update")
            : (isFlyer ? "Admin_Flyer_Create" : "Admin_Offer_Create");
        await using var create = new SqlCommand(saveProcedure, connection)
        { CommandType = CommandType.StoredProcedure };
        if (isRepublish) create.Parameters.AddWithValue("@Id", campaign.ErpRecordId!.Value);
        create.Parameters.AddWithValue("@ImageUri", campaign.ImageUrl);
        create.Parameters.AddWithValue("@Header", (object?)campaign.Header ?? DBNull.Value);
        create.Parameters.AddWithValue("@Title", campaign.Title);
        create.Parameters.AddWithValue("@Body", (object?)campaign.Body ?? DBNull.Value);
        create.Parameters.AddWithValue("@Condition", campaign.AudienceCondition);
        create.Parameters.AddWithValue("@RecDays", (object?)campaign.RecentDays ?? DBNull.Value);
        create.Parameters.AddWithValue("@FromDate", campaign.StartDateUtc);
        create.Parameters.AddWithValue("@ToDate", campaign.EndDateUtc);
        create.Parameters.AddWithValue("@BranchId", (object?)campaign.ErpBranchId ?? DBNull.Value);
        create.Parameters.AddWithValue("@IsPointCustomer", campaign.AudienceCondition == 2 ? 1 : 0);
        await using var reader = await create.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("ERP did not return the saved record.");
        var id = Convert.ToInt64(reader[isFlyer ? "Id" : "id"]);
        await reader.CloseAsync();
        await using var publish = new SqlCommand(isFlyer ? "Admin_Flyer_SetActive" : "Admin_Offer_SetActive", connection)
        { CommandType = CommandType.StoredProcedure };
        publish.Parameters.AddWithValue("@Id", id);
        publish.Parameters.AddWithValue("@Active", true);
        await publish.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }
}
