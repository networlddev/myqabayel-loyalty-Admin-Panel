using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MyQabayel.Admin.Data;

namespace MyQabayel.Admin.Services;

public sealed record LoyaltyGroupSnapshot(int CCode, int LoyaltyGroupId, string GroupCode);
public sealed record ActiveDeviceAvailability(string Environment, int CustomerCount, int DeviceCount);
public sealed record NotificationCustomerSearchResult(long Id, string Name, string? MobileNo);
public sealed record LoyaltyGroupCheckResult(string Status, string Message, LoyaltyGroupSnapshot? Group)
{
    public bool IsActive => Status == "Active";
}

public sealed class LoyaltyCustomerAudience(IConfiguration configuration, AdminDbContext adminDb)
{
    private string ConnectionString => configuration.GetConnectionString("ErpDb")
        ?? throw new InvalidOperationException("ConnectionStrings:ErpDb is not configured.");

    public async Task<string> GetConfiguredGroupCodeAsync(CancellationToken cancellationToken)
    {
        var dedicatedGroup = configuration["LoyaltyGroup:GroupCode"]?.Trim();
        if (configuration.GetValue<bool>("LoyaltyGroup:LockGroupCode")
            && !string.IsNullOrWhiteSpace(dedicatedGroup))
            return dedicatedGroup;
        string? groupCode = await adminDb.AdminPanelSettings.AsNoTracking()
            .Where(x => x.Id == 1).Select(x => x.GroupCode).SingleOrDefaultAsync(cancellationToken);
        groupCode ??= configuration["LoyaltyGroup:GroupCode"]?.Trim();
        if (string.IsNullOrWhiteSpace(groupCode))
            throw new InvalidOperationException("The loyalty application GroupCode is not configured.");
        return groupCode;
    }

    public async Task<string> GetConfiguredPushEnvironmentAsync(CancellationToken cancellationToken)
    {
        string? environment = await adminDb.AdminPanelSettings.AsNoTracking()
            .Where(x => x.Id == 1).Select(x => x.PushEnvironment)
            .SingleOrDefaultAsync(cancellationToken);
        return environment is "Development" or "Production" ? environment : "Development";
    }

    public async Task<LoyaltyGroupSnapshot> ResolveGroupAsync(CancellationToken cancellationToken) =>
        await ResolveGroupAsync(await GetConfiguredGroupCodeAsync(cancellationToken), cancellationToken);

    public async Task<LoyaltyGroupSnapshot> ResolveGroupAsync(string groupCode, CancellationToken cancellationToken)
    {
        var result = await CheckGroupCodeAsync(groupCode, cancellationToken);
        if (!result.IsActive || result.Group is null)
            throw new InvalidOperationException(result.Message);
        return result.Group;
    }

    public async Task<LoyaltyGroupCheckResult> CheckGroupCodeAsync(string? groupCode,
        CancellationToken cancellationToken)
    {
        groupCode = groupCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(groupCode) || groupCode.Length > 100
            || groupCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            return new("Invalid", "Use 1 to 100 letters, numbers, or underscores.", null);

        const string sql = """
            SELECT TOP (2) CCode, LoyaltyGroupId, GroupCode, IsActive
            FROM dbo.NHoLoyaltyGroup
            WHERE GroupCode = @GroupCode
            ORDER BY IsActive DESC, LoyaltyGroupId;
            """;
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@GroupCode", SqlDbType.NVarChar, 100).Value = groupCode;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new("NotFound", "This GroupCode was not found in the configured ERP database.", null);
        var group = new LoyaltyGroupSnapshot(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2));
        if (!reader.GetBoolean(3))
            return new("Inactive", "This loyalty group exists but is inactive in the configured ERP database.", group);
        if (await reader.ReadAsync(cancellationToken) && reader.GetBoolean(3))
            return new("Ambiguous", "More than one active loyalty group uses this GroupCode.", null);
        return new("Active", "This loyalty group is active.", group);
    }

    public async Task<bool> AreActiveCustomersInGroupAsync(
        IReadOnlyCollection<long> customerIds, LoyaltyGroupSnapshot group,
        CancellationToken cancellationToken)
    {
        if (customerIds.Count is < 1 or > 200) return false;
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = connection.CreateCommand();
        var parameters = new List<string>();
        int index = 0;
        foreach (long id in customerIds)
        {
            string parameter = $"@Customer{index++}";
            parameters.Add(parameter);
            command.Parameters.Add(parameter, SqlDbType.BigInt).Value = id;
        }
        command.CommandText = $"""
            SELECT COUNT_BIG(*) FROM dbo.NPrivilegeCustomer
            WHERE Ccode = @CCode AND LoyaltyGroupId = @GroupId AND Active = 1
              AND Id IN ({string.Join(",", parameters)});
            """;
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        await connection.OpenAsync(cancellationToken);
        long count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return count == customerIds.Count;
    }

    public async Task<IReadOnlyList<NotificationCustomerSearchResult>> SearchActiveCustomersAsync(
        string query, LoyaltyGroupSnapshot group, CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length < 2 || query.Length > 100) return [];
        const string sql = """
            SELECT TOP (20) C.Id, C.Name, C.MobileNo
            FROM dbo.NPrivilegeCustomer C
            WHERE C.Ccode = @CCode AND C.LoyaltyGroupId = @GroupId AND C.Active = 1
              AND (CONVERT(NVARCHAR(30), C.Id) = @ExactId
                   OR C.Name LIKE @Pattern ESCAPE N'~'
                   OR CONVERT(NVARCHAR(50), C.MobileNo) LIKE @Pattern ESCAPE N'~')
            ORDER BY C.Name, C.Id;
            """;
        string escaped = query.Replace("~", "~~").Replace("%", "~%")
            .Replace("_", "~_").Replace("[", "~[");
        var results = new List<NotificationCustomerSearchResult>();
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        command.Parameters.Add("@ExactId", SqlDbType.NVarChar, 30).Value = query;
        command.Parameters.Add("@Pattern", SqlDbType.NVarChar, 220).Value = $"%{escaped}%";
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new NotificationCustomerSearchResult(
                reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2))));
        return results;
    }

    public async Task<IReadOnlyList<long>> GetCustomersWithoutRecentPurchaseAsync(
        int days, LoyaltyGroupSnapshot group, CancellationToken cancellationToken)
    {
        if (days is not (0 or 30 or 60 or 90))
            throw new ArgumentOutOfRangeException(nameof(days));
        // CoreEdge has purchase dates, not a customer visit log. Exclude newly registered
        // customers so a "we miss you" audience does not include someone who just joined.
        const string sql = """
            SELECT TOP (20001) C.Id
            FROM dbo.NPrivilegeCustomer C
            WHERE C.Ccode = @CCode AND C.LoyaltyGroupId = @GroupId AND C.Active = 1
              AND (@Days = 0 OR C.Entrydate < DATEADD(DAY, -@Days, GETDATE()))
              AND NOT EXISTS
              (
                  SELECT 1 FROM dbo.NSalesMaster S
                  WHERE S.CCode = C.Ccode AND S.PrivilegeCustID = C.Id
                    AND (@Days = 0 OR S.InvDate >= DATEADD(DAY, -@Days, GETDATE()))
              )
            ORDER BY C.Id;
            """;
        var results = new List<long>();
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        command.Parameters.Add("@Days", SqlDbType.Int).Value = days;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(reader.GetInt64(0));
        return results;
    }

    public async Task<IReadOnlyList<long>> GetActiveCustomerIdsAsync(
        LoyaltyGroupSnapshot group, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (20001) Id FROM dbo.NPrivilegeCustomer
            WHERE Ccode=@CCode AND LoyaltyGroupId=@GroupId AND Active=1
            ORDER BY Id;
            """;
        return await LoadAudienceIdsAsync(sql, group, null, cancellationToken);
    }

    public async Task<IReadOnlyList<long>> GetCustomersWithRecentPurchaseAsync(
        int days, LoyaltyGroupSnapshot group, CancellationToken cancellationToken)
    {
        if (days is not (30 or 60 or 90))
            throw new ArgumentOutOfRangeException(nameof(days));
        const string sql = """
            SELECT TOP (20001) C.Id
            FROM dbo.NPrivilegeCustomer C
            WHERE C.Ccode=@CCode AND C.LoyaltyGroupId=@GroupId AND C.Active=1
              AND EXISTS
              (SELECT 1 FROM dbo.NSalesMaster S
               WHERE S.CCode=C.Ccode AND S.PrivilegeCustID=C.Id
                 AND S.InvDate>=DATEADD(DAY,-@Days,GETDATE()))
            ORDER BY C.Id;
            """;
        return await LoadAudienceIdsAsync(sql, group, days, cancellationToken);
    }

    private async Task<IReadOnlyList<long>> LoadAudienceIdsAsync(string sql,
        LoyaltyGroupSnapshot group, int? days, CancellationToken cancellationToken)
    {
        var results = new List<long>();
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        if (days.HasValue) command.Parameters.Add("@Days", SqlDbType.Int).Value = days.Value;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(reader.GetInt64(0));
        return results;
    }

    public async Task<IReadOnlyDictionary<string, ActiveDeviceAvailability>> GetActiveDeviceAvailabilityAsync(
        LoyaltyGroupSnapshot group, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT D.Environment, COUNT(DISTINCT D.UserId) AS CustomerCount,
                   COUNT(*) AS DeviceCount
            FROM dbo.NLoyaltyCustomerDevice D
            INNER JOIN dbo.NPrivilegeCustomer C
                ON C.Id = D.UserId AND C.Ccode = D.CCode
               AND C.LoyaltyGroupId = D.LoyaltyGroupId AND C.Active = 1
            WHERE D.CCode = @CCode AND D.LoyaltyGroupId = @GroupId
              AND D.IsActive = 1 AND D.Environment IN (N'Development', N'Production')
            GROUP BY D.Environment;
            """;
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, ActiveDeviceAvailability>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            string environment = reader.GetString(0);
            result[environment] = new ActiveDeviceAvailability(environment, reader.GetInt32(1), reader.GetInt32(2));
        }
        return result;
    }
}
