using MyQabayel.Admin.Models;
using Microsoft.Data.SqlClient;
using System.Data;

namespace MyQabayel.Admin.Services;

public class CoreEdgeCustomerService(IConfiguration configuration)
{
    public async Task<IReadOnlyList<CustomerRegistrationDay>> GetRegistrationActivityAsync(DateTime start, DateTime end,
        LoyaltyGroupSnapshot group, CancellationToken token = default)
    {
        var connectionString = configuration.GetConnectionString("ErpDb")
            ?? throw new InvalidOperationException("CoreEdge ERP connection is not configured.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        const string sql = """
            SELECT CONVERT(date, customer.Entrydate) AS RegistrationDate,
                   COUNT_BIG(*) AS Registrations
            FROM dbo.NPrivilegeCustomer customer
            WHERE customer.Entrydate >= @start AND customer.Entrydate < @end
              AND customer.Ccode = @CCode AND customer.LoyaltyGroupId = @GroupId
            GROUP BY CONVERT(date, customer.Entrydate)
            ORDER BY RegistrationDate;
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 20 };
        command.Parameters.Add("@start", SqlDbType.DateTime2).Value = start.Date;
        command.Parameters.Add("@end", SqlDbType.DateTime2).Value = end.Date;
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        var days = new List<CustomerRegistrationDay>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            days.Add(new CustomerRegistrationDay(
                reader.GetDateTime(0).Date,
                reader.GetInt64(1)));
        }
        return days;
    }

    public async Task<CoreEdgeDashboardMetrics> GetDashboardMetricsAsync(LoyaltyGroupSnapshot group,
        CancellationToken token = default)
    {
        var connectionString = configuration.GetConnectionString("ErpDb")
            ?? throw new InvalidOperationException("CoreEdge ERP connection is not configured.");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        const string sql = """
            SELECT customerTotals.TotalCustomers, customerTotals.EnabledCustomers, invoiceTotals.LoyaltyInvoices,
                   invoiceTotals.LoyaltySales, customerTotals.TotalPoints
            FROM
            (
                SELECT COUNT_BIG(*) AS TotalCustomers,
                       COUNT_BIG(CASE WHEN customer.Active=1 THEN 1 END) AS EnabledCustomers,
                       ISNULL(SUM(
                           CONVERT(decimal(19,2),ISNULL(customer.Points,0))
                           + ISNULL(opening.OpeningPoints,0)
                           - ISNULL(opening.RedeemedPoints,0)),0) AS TotalPoints
                FROM dbo.NPrivilegeCustomer customer
                OUTER APPLY
                (
                    SELECT
                        SUM(CONVERT(decimal(19,2),ISNULL(o.OpeningPoints,0))) AS OpeningPoints,
                        SUM(CONVERT(decimal(19,2),ISNULL(o.RedeemPoints,0))) AS RedeemedPoints
                    FROM dbo.NPrivilegeOpening o
                    WHERE o.PrivilegeCustID=customer.Id AND o.Ccode=customer.Ccode
                ) opening
                WHERE customer.Ccode = @CCode AND customer.LoyaltyGroupId = @GroupId
            ) customerTotals
            CROSS JOIN
            (
                SELECT COUNT_BIG(*) AS LoyaltyInvoices,
                       ISNULL(SUM(CONVERT(decimal(19,2),sales.TotAmount)),0) AS LoyaltySales
                FROM dbo.NSalesMaster sales
                INNER JOIN dbo.NPrivilegeCustomer customer
                   ON customer.Id = sales.PrivilegeCustID AND customer.Ccode = sales.CCode
                WHERE ISNULL(sales.PrivilegePoint,0) - ISNULL(sales.RedeemPoints,0) <> 0
                  AND customer.Ccode = @CCode AND customer.LoyaltyGroupId = @GroupId
            ) invoiceTotals;
            """;
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 20 };
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("CoreEdge did not return dashboard metrics.");
        return new CoreEdgeDashboardMetrics(
            Convert.ToInt64(reader["TotalCustomers"]),
            Convert.ToInt64(reader["EnabledCustomers"]),
            Convert.ToInt64(reader["LoyaltyInvoices"]),
            Convert.ToDecimal(reader["LoyaltySales"]),
            Convert.ToDecimal(reader["TotalPoints"]));
    }

    public async Task<IReadOnlyList<CustomerSummary>> GetCustomersAsync(LoyaltyGroupSnapshot group, string? search,
        CancellationToken token = default)
    {
        var connectionString = configuration.GetConnectionString("ErpDb")
            ?? throw new InvalidOperationException("CoreEdge ERP connection is not configured.");
        var customers = new List<CustomerSummary>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        const string sql = """
            SELECT TOP (500)
                   customer.Id,customer.Name,customer.MobileNo,customer.Email,customer.CardNumber,
                   ISNULL(customer.Points,0) Points,
                   ISNULL(opening.OpeningPoints,0) OpeningPoints,
                   ISNULL(opening.RedeemedPoints,0) RedeemedOpeningPoints,
                   opening.OpeningDate,
                   customer.Entrydate RegisteredAt,
                   customer.Active
            FROM dbo.NPrivilegeCustomer customer
            OUTER APPLY
            (
                SELECT
                    SUM(CONVERT(decimal(19,2),ISNULL(o.OpeningPoints,0))) AS OpeningPoints,
                    SUM(CONVERT(decimal(19,2),ISNULL(o.RedeemPoints,0))) AS RedeemedPoints,
                    MIN(o.OpeningDate) AS OpeningDate
                FROM dbo.NPrivilegeOpening o
                WHERE o.PrivilegeCustID=customer.Id AND o.Ccode=customer.Ccode
            ) opening
            WHERE customer.Ccode = @CCode AND customer.LoyaltyGroupId = @GroupId
              AND (@Search IS NULL OR customer.Name LIKE '%' + @Search + '%'
                   OR CONVERT(nvarchar(30),customer.MobileNo) LIKE '%' + @Search + '%'
                   OR customer.Id = TRY_CONVERT(bigint,@Search))
            ORDER BY customer.Id DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
        command.Parameters.Add("@Search", SqlDbType.NVarChar, 100).Value =
            string.IsNullOrWhiteSpace(search) ? DBNull.Value : search.Trim()[..Math.Min(search.Trim().Length, 100)];
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            customers.Add(new CustomerSummary(
                Convert.ToInt64(reader["Id"]),
                Convert.ToString(reader["Name"]) ?? string.Empty,
                reader["MobileNo"] is DBNull ? null : Convert.ToString(reader["MobileNo"]),
                reader["Email"] is DBNull ? null : Convert.ToString(reader["Email"]),
                reader["CardNumber"] is DBNull ? null : Convert.ToString(reader["CardNumber"]),
                Convert.ToDecimal(reader["Points"]),
                Convert.ToDecimal(reader["OpeningPoints"]),
                Convert.ToDecimal(reader["RedeemedOpeningPoints"]),
                reader["OpeningDate"] is DBNull ? null : Convert.ToDateTime(reader["OpeningDate"]),
                reader["RegisteredAt"] is DBNull ? null : Convert.ToDateTime(reader["RegisteredAt"]),
                Convert.ToBoolean(reader["Active"])));
        }
        return customers;
    }
}
