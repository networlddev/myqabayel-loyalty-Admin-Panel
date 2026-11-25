using System.Data;
using Microsoft.Data.SqlClient;

namespace QabayelMall.API.Services;

public sealed record CustomerAppState(bool ShowFlyers, bool ShowOffers, DateTime? LastSeenAtUtc);
public sealed record CustomerActivityCounts(int OnlineLastFiveMinutes, int ActiveLast24Hours);

public sealed class CustomerAppManagementService(IConfiguration configuration)
{
    private SqlConnection AdminConnection() => new(configuration.GetConnectionString("AdminDb")
        ?? throw new InvalidOperationException("AdminDb is not configured."));

    private SqlConnection ErpConnection() => new(configuration.GetConnectionString("ErpDb")
        ?? throw new InvalidOperationException("ErpDb is not configured."));

    private static void AddIdentity(SqlCommand command, LoyaltyGroupSnapshot group, long? customerId = null)
    {
        command.Parameters.Add("@GroupCode", SqlDbType.NVarChar, 100).Value = group.GroupCode;
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        if (customerId.HasValue)
            command.Parameters.Add("@CustomerId", SqlDbType.BigInt).Value = customerId.Value;
    }

    public async Task<Dictionary<long, CustomerAppState>> GetStatesAsync(LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = """
            SELECT CustomerId, ShowFlyers, ShowOffers, LastSeenAtUtc
            FROM dbo.AdminCustomerAppSettings
            WHERE GroupCode=@GroupCode AND CCode=@CCode AND LoyaltyGroupId=@GroupId;
            """;
        await using var connection = AdminConnection();
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, group);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        var states = new Dictionary<long, CustomerAppState>();
        while (await reader.ReadAsync(token))
            states.Add(reader.GetInt64(0), new CustomerAppState(reader.GetBoolean(1), reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3)));
        return states;
    }

    public async Task<CustomerActivityCounts> GetActivityCountsAsync(LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = """
            SELECT COUNT(CASE WHEN LastSeenAtUtc >= DATEADD(minute,-5,SYSUTCDATETIME()) THEN 1 END),
                   COUNT(CASE WHEN LastSeenAtUtc >= DATEADD(hour,-24,SYSUTCDATETIME()) THEN 1 END)
            FROM dbo.AdminCustomerAppSettings
            WHERE GroupCode=@GroupCode AND CCode=@CCode AND LoyaltyGroupId=@GroupId;
            """;
        await using var connection = AdminConnection();
        await using var command = new SqlCommand(sql, connection);
        AddIdentity(command, group);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        await reader.ReadAsync(token);
        return new CustomerActivityCounts(reader.GetInt32(0), reader.GetInt32(1));
    }

    public async Task<bool> SetVisibilityAsync(LoyaltyGroupSnapshot group, long customerId,
        bool showFlyers, bool showOffers, CancellationToken token)
    {
        if (customerId <= 0) return false;
        await using (var erp = ErpConnection())
        await using (var exists = new SqlCommand("""
            SELECT 1 FROM dbo.NPrivilegeCustomer
            WHERE Id=@CustomerId AND Ccode=@CCode AND LoyaltyGroupId=@GroupId;
            """, erp))
        {
            exists.Parameters.Add("@CustomerId", SqlDbType.BigInt).Value = customerId;
            exists.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
            exists.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
            await erp.OpenAsync(token);
            if (await exists.ExecuteScalarAsync(token) is null) return false;
        }
        const string sql = """
            UPDATE dbo.AdminCustomerAppSettings WITH (UPDLOCK, HOLDLOCK)
            SET ShowFlyers=@ShowFlyers, ShowOffers=@ShowOffers, UpdatedAtUtc=SYSUTCDATETIME()
            WHERE GroupCode=@GroupCode AND CCode=@CCode AND LoyaltyGroupId=@GroupId AND CustomerId=@CustomerId;
            IF @@ROWCOUNT=0
                INSERT dbo.AdminCustomerAppSettings
                    (GroupCode,CCode,LoyaltyGroupId,CustomerId,ShowFlyers,ShowOffers)
                VALUES (@GroupCode,@CCode,@GroupId,@CustomerId,@ShowFlyers,@ShowOffers);
            """;
        await using var connection = AdminConnection();
        await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using var command = new SqlCommand(sql, connection, transaction);
        AddIdentity(command, group, customerId);
        command.Parameters.Add("@ShowFlyers", SqlDbType.Bit).Value = showFlyers;
        command.Parameters.Add("@ShowOffers", SqlDbType.Bit).Value = showOffers;
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task<bool> SetAccountActiveAsync(LoyaltyGroupSnapshot group, long customerId,
        bool active, CancellationToken token)
    {
        if (customerId <= 0) return false;
        const string sql = """
            UPDATE dbo.NPrivilegeCustomer
            SET Active=@Active,
                token=CASE WHEN @Active=0 THEN CONVERT(nvarchar(200),NEWID()) ELSE token END
            WHERE Id=@CustomerId AND Ccode=@CCode AND LoyaltyGroupId=@GroupId;
            """;
        await using var connection = ErpConnection();
        await connection.OpenAsync(token);
        await using var transaction = connection.BeginTransaction();
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Active", SqlDbType.Bit).Value = active;
        command.Parameters.Add("@CustomerId", SqlDbType.BigInt).Value = customerId;
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        if (await command.ExecuteNonQueryAsync(token) != 1)
        {
            await transaction.RollbackAsync(token);
            return false;
        }

        if (!active)
        {
            const string revokeSql = """
                DELETE FROM dbo.NLoyaltyCustomerRefreshToken
                WHERE CustomerId=@CustomerId AND CCode=@CCode AND LoyaltyGroupId=@GroupId;
                UPDATE dbo.NLoyaltyCustomerDevice SET IsActive=0, UnregisteredAtUtc=SYSUTCDATETIME()
                WHERE UserId=@CustomerId AND CCode=@CCode AND LoyaltyGroupId=@GroupId AND IsActive=1;
                """;
            await using var revoke = new SqlCommand(revokeSql, connection, transaction);
            revoke.Parameters.Add("@CustomerId", SqlDbType.BigInt).Value = customerId;
            revoke.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
            revoke.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
            await revoke.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        if (!active)
        {
            await using var admin = AdminConnection();
            await using var clear = new SqlCommand("""
                UPDATE dbo.AdminCustomerAppSettings SET LastSeenAtUtc=NULL, UpdatedAtUtc=SYSUTCDATETIME()
                WHERE GroupCode=@GroupCode AND CCode=@CCode AND LoyaltyGroupId=@GroupId AND CustomerId=@CustomerId;
                """, admin);
            AddIdentity(clear, group, customerId);
            await admin.OpenAsync(token);
            await clear.ExecuteNonQueryAsync(token);
        }
        return true;
    }
}
