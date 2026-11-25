using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MyQabayel.Admin.Models;

namespace MyQabayel.Admin.Services;

public sealed class CustomerPromoService(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("ErpDb")
        ?? throw new InvalidOperationException("ConnectionStrings:ErpDb is not configured.");

    private static void RequireCompany(LoyaltyGroupSnapshot group)
    {
        if (group.CCode != 1)
            throw new InvalidOperationException("Customer PromoCode issuance is configured for company code 1 only.");
    }

    public async Task<IReadOnlyList<PromoDepartment>> GetActiveDepartmentsAsync(CancellationToken token)
    {
        const string sql = """
            SELECT Id, Name, Code FROM dbo.NDepartmentMaster
            WHERE IsActive = 1 ORDER BY Name, Id;
            """;
        var result = new List<PromoDepartment>();
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2)));
        return result;
    }

    public async Task<IReadOnlyList<PromoMasterSummary>> ListAsync(LoyaltyGroupSnapshot group,
        CancellationToken token)
    {
        RequireCompany(group);
        const string sql = """
            SELECT P.Id, P.Name, P.Caption, P.Body, P.Code, P.Validity,
              (SELECT COUNT(*) FROM dbo.NPrivilagePromoCodeDepartment PD
                 JOIN dbo.NDepartmentMaster DEP ON DEP.Id=PD.DepartmentId AND DEP.IsActive=1
                WHERE PD.PromoCodeId=P.Id AND PD.IsActive=1) AS DepartmentCount,
              (SELECT COUNT(*) FROM dbo.NPrivilagePromoCodeDetails PC
                WHERE PC.PromoCodeId=P.Id) AS IssuedCount
            FROM dbo.NPrivilagePromoCode P
            WHERE P.CCode=@CCode AND P.LoyaltyGroupId=@GroupId
            ORDER BY P.Id DESC;
            """;
        var result = new List<PromoMasterSummary>();
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        AddGroup(command, group);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(ReadMaster(reader));
        return result;
    }

    public async Task<PromoMasterSummary?> GetAsync(int id, LoyaltyGroupSnapshot group,
        CancellationToken token)
    {
        RequireCompany(group);
        const string sql = """
            SELECT P.Id, P.Name, P.Caption, P.Body, P.Code, P.Validity,
              (SELECT COUNT(*) FROM dbo.NPrivilagePromoCodeDepartment PD
                 JOIN dbo.NDepartmentMaster DEP ON DEP.Id=PD.DepartmentId AND DEP.IsActive=1
                WHERE PD.PromoCodeId=P.Id AND PD.IsActive=1),
              (SELECT COUNT(*) FROM dbo.NPrivilagePromoCodeDetails PC
                WHERE PC.PromoCodeId=P.Id)
            FROM dbo.NPrivilagePromoCode P
            WHERE P.Id=@Id AND P.CCode=@CCode AND P.LoyaltyGroupId=@GroupId;
            """;
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        AddGroup(command, group);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? ReadMaster(reader) : null;
    }

    public async Task<IReadOnlyList<PromoDepartment>> GetDepartmentsAsync(int promoId,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = """
            SELECT D.Id, D.Name, D.Code
            FROM dbo.NPrivilagePromoCodeDepartment PD
            JOIN dbo.NDepartmentMaster D ON D.Id=PD.DepartmentId
            JOIN dbo.NPrivilagePromoCode P ON P.Id=PD.PromoCodeId
            WHERE P.Id=@PromoId AND P.CCode=@CCode AND P.LoyaltyGroupId=@GroupId
              AND PD.IsActive=1
            ORDER BY D.Name, D.Id;
            """;
        var result = new List<PromoDepartment>();
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@PromoId", SqlDbType.Int).Value = promoId;
        AddGroup(command, group);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2)));
        return result;
    }

    public async Task<int> CreateAsync(PromoCreateViewModel model, LoyaltyGroupSnapshot group,
        CancellationToken token)
    {
        RequireCompany(group);
        var ids = model.DepartmentIds.Distinct().ToArray();
        if (ids.Length == 0 || ids.Any(id => id <= 0))
            throw new InvalidOperationException("Select at least one active department.");
        var validity = model.ValidityDate.Date.AddDays(1).AddMilliseconds(-3);
        if (validity < new DateTime(1753, 1, 1))
            throw new InvalidOperationException("Choose a valid end date.");
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, token);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var idParameters = new List<string>();
            for (int i = 0; i < ids.Length; i++)
            {
                string name = $"@Department{i}";
                idParameters.Add(name);
                command.Parameters.Add(name, SqlDbType.Int).Value = ids[i];
            }
            command.CommandText = $"""
                SELECT COUNT(*) FROM dbo.NDepartmentMaster WITH (UPDLOCK, HOLDLOCK)
                WHERE IsActive=1 AND Id IN ({string.Join(',', idParameters)});
                """;
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != ids.Length)
                throw new InvalidOperationException("One or more selected departments are no longer active.");

            command.Parameters.Clear();
            command.Parameters.Add("@Name", SqlDbType.VarChar, 200).Value = model.Name.Trim();
            command.Parameters.Add("@Caption", SqlDbType.VarChar, 500).Value = model.Caption.Trim();
            command.Parameters.Add("@Body", SqlDbType.VarChar, 300).Value =
                string.IsNullOrWhiteSpace(model.Body) ? DBNull.Value : model.Body.Trim();
            command.Parameters.Add("@Code", SqlDbType.VarChar, 230).Value = model.CampaignCode.Trim();
            command.Parameters.Add("@Validity", SqlDbType.DateTime).Value = validity;
            AddGroup(command, group);
            command.CommandText = """
                IF @Validity <= GETDATE()
                    THROW 51430, 'Validity must end in the future.', 1;
                IF EXISTS (SELECT 1 FROM dbo.NPrivilagePromoCode WITH (UPDLOCK, HOLDLOCK)
                           WHERE Code=@Code)
                    THROW 51431, 'The campaign code already exists.', 1;
                DECLARE @Id int;
                SELECT @Id=ISNULL(MAX(Id),0)+1
                FROM dbo.NPrivilagePromoCode WITH (TABLOCKX, HOLDLOCK);
                INSERT dbo.NPrivilagePromoCode
                    (Id, Name, Caption, Body, Validity, Code, CCode, LoyaltyGroupId)
                VALUES (@Id, @Name, @Caption, @Body, @Validity, @Code, @CCode, @GroupId);
                SELECT @Id;
                """;
            int promoId = Convert.ToInt32(await command.ExecuteScalarAsync(token));
            command.Parameters.Clear();
            command.Parameters.Add("@PromoId", SqlDbType.Int).Value = promoId;
            command.CommandText = """
                INSERT dbo.NPrivilagePromoCodeDepartment(PromoCodeId, DepartmentId, IsActive)
                VALUES(@PromoId, @DepartmentId, 1);
                """;
            var departmentParameter = command.Parameters.Add("@DepartmentId", SqlDbType.Int);
            foreach (int departmentId in ids)
            {
                departmentParameter.Value = departmentId;
                await command.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
            return promoId;
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    public async Task<PromoEditResult> EditAsync(PromoEditViewModel model,
        IReadOnlyDictionary<string, long> customerChanges,
        IReadOnlyDictionary<string, long> originalAssignments,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        RequireCompany(group);
        var validity = model.ValidityDate.Date.AddDays(1).AddMilliseconds(-3);
        if (validity <= DateTime.Now || validity > new DateTime(9999, 12, 31))
            throw new InvalidOperationException("Choose a future last valid day.");
        bool changeDiscount = model.DiscountValue.HasValue;
        if (changeDiscount && model.DiscountType is not ("Amount" or "Percentage"))
            throw new InvalidOperationException("Choose a fixed amount or percentage.");
        if (changeDiscount && model.DiscountType == "Amount" &&
            (model.DiscountValue is <= 0 or > 99999999.99m ||
             decimal.Round(model.DiscountValue!.Value, 2) != model.DiscountValue))
            throw new InvalidOperationException("Enter a positive fixed amount with at most two decimals.");
        if (changeDiscount && model.DiscountType == "Percentage" &&
            (model.DiscountValue is < 1 or > 100 ||
             decimal.Truncate(model.DiscountValue!.Value) != model.DiscountValue))
            throw new InvalidOperationException("Enter a whole-number percentage from 1 to 100.");

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, token);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 120;
            command.Parameters.Add("@PromoId", SqlDbType.Int).Value = model.Id;
            command.Parameters.Add("@Name", SqlDbType.VarChar, 200).Value = model.Name.Trim();
            command.Parameters.Add("@Caption", SqlDbType.VarChar, 500).Value = model.Caption.Trim();
            command.Parameters.Add("@Body", SqlDbType.VarChar, 300).Value =
                string.IsNullOrWhiteSpace(model.Body) ? DBNull.Value : model.Body.Trim();
            command.Parameters.Add("@Validity", SqlDbType.DateTime).Value = validity;
            command.Parameters.Add("@ChangeDiscount", SqlDbType.Bit).Value = changeDiscount;
            command.Parameters.Add("@Amount", SqlDbType.Money).Value =
                changeDiscount && model.DiscountType == "Amount" ? model.DiscountValue!.Value : 0m;
            command.Parameters.Add("@Percentage", SqlDbType.Int).Value =
                changeDiscount && model.DiscountType == "Percentage" ? (int)model.DiscountValue!.Value : 0;
            AddGroup(command, group);
            command.CommandText = """
                IF NOT EXISTS (SELECT 1 FROM dbo.NPrivilagePromoCode WITH (UPDLOCK,HOLDLOCK)
                    WHERE Id=@PromoId AND CCode=@CCode AND LoyaltyGroupId=@GroupId)
                    THROW 51450, 'This campaign is unavailable in the configured loyalty group.', 1;
                IF @Validity<=GETDATE()
                    THROW 51451, 'Choose a future last valid day.', 1;
                DECLARE @Issued int;
                SELECT @Issued=COUNT(*) FROM dbo.NPrivilagePromoCodeDetails WITH (UPDLOCK,HOLDLOCK)
                WHERE PromoCodeId=@PromoId;
                UPDATE dbo.NPrivilagePromoCode
                SET Name=@Name, Caption=@Caption, Body=@Body, Validity=@Validity
                WHERE Id=@PromoId AND CCode=@CCode AND LoyaltyGroupId=@GroupId;
                UPDATE G
                SET G.ExpiryDate=@Validity,
                    G.VouchAmt=CASE WHEN @ChangeDiscount=1 THEN @Amount ELSE G.VouchAmt END,
                    G.VouchPerct=CASE WHEN @ChangeDiscount=1 THEN @Percentage ELSE G.VouchPerct END,
                    G.ModifiedDate=GETDATE()
                FROM dbo.NGiftVoucherMaster G
                JOIN dbo.NPrivilagePromoCodeDetails D
                  ON D.PromoCode='GxPrv'+CONVERT(varchar(30),G.GVNo)
                WHERE D.PromoCodeId=@PromoId AND G.CCode=@CCode
                  AND G.PrivilegeVoucher=2 AND D.Used=0 AND G.Used=0;
                DECLARE @Updated int=@@ROWCOUNT;
                SELECT @Updated,
                    (SELECT COUNT(*) FROM dbo.NPrivilagePromoCodeDetails D
                     JOIN dbo.NGiftVoucherMaster G
                       ON D.PromoCode='GxPrv'+CONVERT(varchar(30),G.GVNo)
                     WHERE D.PromoCodeId=@PromoId AND G.CCode=@CCode
                       AND G.PrivilegeVoucher=2 AND (D.Used=1 OR (G.Used=1 AND D.Used<>2))),
                    (SELECT COUNT(*) FROM dbo.NPrivilagePromoCodeDetails
                     WHERE PromoCodeId=@PromoId AND Used=2), @Issued;
                """;
            int updated, redeemed, revoked, issued;
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                if (!await reader.ReadAsync(token))
                    throw new InvalidOperationException("The campaign update returned no result.");
                updated = reader.GetInt32(0);
                redeemed = reader.GetInt32(1);
                revoked = reader.GetInt32(2);
                issued = reader.GetInt32(3);
            }
            if (model.CanEditDepartments && issued > 0)
                throw new InvalidOperationException("Customer codes were issued while you edited this campaign. Reload it; department rules are now locked.");

            command.Parameters.Clear();
            command.Parameters.Add("@PromoId", SqlDbType.Int).Value = model.Id;
            command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
            command.CommandText = """
                SELECT D.PromoCode,D.CustId,D.Used,G.Used
                FROM dbo.NPrivilagePromoCodeDetails D WITH (UPDLOCK,HOLDLOCK)
                JOIN dbo.NGiftVoucherMaster G WITH (UPDLOCK,HOLDLOCK)
                  ON D.PromoCode='GxPrv'+CONVERT(varchar(30),G.GVNo)
                WHERE D.PromoCodeId=@PromoId AND G.CCode=@CCode AND G.PrivilegeVoucher=2;
                """;
            var current = new List<(string Code, long CustomerId, int DetailUsed, bool VoucherUsed)>();
            await using (var reader = await command.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                    current.Add((reader.GetString(0), reader.GetInt64(1),
                        reader.GetInt32(2), reader.GetBoolean(3)));
            if (current.Count != issued)
                throw new InvalidOperationException("An issued code is missing its ERP voucher. No changes were saved.");
            var currentAssignments = current.Where(x => x.DetailUsed != 2)
                .ToDictionary(x => x.Code, x => x.CustomerId, StringComparer.Ordinal);
            if (currentAssignments.Count != originalAssignments.Count ||
                currentAssignments.Any(x => !originalAssignments.TryGetValue(x.Key, out long id) || id != x.Value))
                throw new InvalidOperationException("The campaign customer list changed. Reload Edit and review the customers again.");
            if (customerChanges.Count > 50 || customerChanges.Values.Any(x => x <= 0) ||
                customerChanges.Keys.Any(x => !currentAssignments.ContainsKey(x)))
                throw new InvalidOperationException("Choose up to 50 valid unused codes to reassign.");

            var mutable = current.ToDictionary(x => x.Code, StringComparer.Ordinal);
            var effective = new Dictionary<string, long>(currentAssignments, StringComparer.Ordinal);
            int reassigned = 0, skippedUsed = 0;
            foreach (var change in customerChanges)
            {
                var code = mutable[change.Key];
                if (change.Value == code.CustomerId) continue;
                if (code.DetailUsed != 0 || code.VoucherUsed)
                {
                    skippedUsed++;
                    continue;
                }
                effective[change.Key] = change.Value;
            }
            if (customerChanges.Count > 0 && effective.Values.Distinct().Count() != effective.Count)
                throw new InvalidOperationException("A customer cannot hold two active codes in this campaign.");

            long[] targetIds = customerChanges.Where(x => x.Value != mutable[x.Key].CustomerId
                    && effective[x.Key] == x.Value)
                .Select(x => x.Value).Distinct().ToArray();
            if (targetIds.Length > 0)
            {
                command.Parameters.Clear();
                command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
                command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
                command.Parameters.Add("@Targets", SqlDbType.NVarChar, -1).Value =
                    JsonSerializer.Serialize(targetIds);
                command.Parameters.Add("@Expected", SqlDbType.Int).Value = targetIds.Length;
                command.CommandText = """
                    SELECT COUNT(*) FROM dbo.NPrivilegeCustomer C WITH (HOLDLOCK)
                    JOIN OPENJSON(@Targets) T ON C.Id=TRY_CONVERT(bigint,T.[value])
                    WHERE T.[type]=2 AND C.Ccode=@CCode AND C.LoyaltyGroupId=@GroupId AND C.Active=1;
                    """;
                if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != targetIds.Length)
                    throw new InvalidOperationException("One or more replacement customers are not active in this loyalty group.");
            }
            foreach (var change in customerChanges)
            {
                var code = mutable[change.Key];
                if (change.Value == code.CustomerId || code.DetailUsed != 0 || code.VoucherUsed) continue;
                command.Parameters.Clear();
                command.Parameters.Add("@PromoId", SqlDbType.Int).Value = model.Id;
                command.Parameters.Add("@Code", SqlDbType.VarChar, 500).Value = change.Key;
                command.Parameters.Add("@OldCustomerId", SqlDbType.BigInt).Value = code.CustomerId;
                command.Parameters.Add("@NewCustomerId", SqlDbType.BigInt).Value = change.Value;
                command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
                command.CommandText = """
                    UPDATE D SET CustId=@NewCustomerId
                    FROM dbo.NPrivilagePromoCodeDetails D
                    JOIN dbo.NGiftVoucherMaster G
                      ON D.PromoCode='GxPrv'+CONVERT(varchar(30),G.GVNo)
                    WHERE D.PromoCodeId=@PromoId AND D.PromoCode=@Code
                      AND D.CustId=@OldCustomerId AND D.Used=0
                      AND G.CCode=@CCode AND G.PrivilegeVoucher=2 AND G.Used=0;
                    SELECT @@ROWCOUNT;
                    """;
                if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 1)
                    throw new InvalidOperationException("A customer code changed during this edit. Reload and review it.");
                reassigned++;
            }            if (issued == 0)
            {
                int[] ids = model.DepartmentIds.Distinct().ToArray();
                if (ids.Length == 0 || ids.Any(id => id <= 0))
                    throw new InvalidOperationException("Select at least one active department.");
                command.Parameters.Clear();
                command.Parameters.Add("@PromoId", SqlDbType.Int).Value = model.Id;
                command.Parameters.Add("@DepartmentJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(ids);
                command.Parameters.Add("@Expected", SqlDbType.Int).Value = ids.Length;
                command.CommandText = """
                    CREATE TABLE #Departments(Id int NOT NULL PRIMARY KEY);
                    INSERT #Departments(Id)
                    SELECT TRY_CONVERT(int,[value]) FROM OPENJSON(@DepartmentJson)
                    WHERE [type]=2 AND TRY_CONVERT(int,[value])>0;
                    IF (SELECT COUNT(*) FROM #Departments)<>@Expected OR
                       (SELECT COUNT(*) FROM dbo.NDepartmentMaster M WITH (HOLDLOCK)
                        JOIN #Departments D ON D.Id=M.Id WHERE M.IsActive=1)<>@Expected
                        THROW 51452, 'One or more selected departments are no longer active.', 1;
                    UPDATE dbo.NPrivilagePromoCodeDepartment SET IsActive=0
                    WHERE PromoCodeId=@PromoId;
                    UPDATE PD SET IsActive=1
                    FROM dbo.NPrivilagePromoCodeDepartment PD
                    JOIN #Departments D ON D.Id=PD.DepartmentId
                    WHERE PD.PromoCodeId=@PromoId;
                    INSERT dbo.NPrivilagePromoCodeDepartment(PromoCodeId,DepartmentId,IsActive)
                    SELECT @PromoId,D.Id,1 FROM #Departments D
                    WHERE NOT EXISTS (SELECT 1 FROM dbo.NPrivilagePromoCodeDepartment PD
                                      WHERE PD.PromoCodeId=@PromoId AND PD.DepartmentId=D.Id);
                    """;
                await command.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
            return new PromoEditResult(updated, redeemed, revoked, reassigned, skippedUsed);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    public async Task<IReadOnlyList<PromoCustomer>> SearchCustomersAsync(string query,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        if (query.Trim().Length < 2) return [];
        string escaped = query.Trim().Replace("~", "~~").Replace("%", "~%")
            .Replace("_", "~_").Replace("[", "~[");
        const string sql = """
            SELECT TOP(20) Id, Name, MobileNo FROM dbo.NPrivilegeCustomer
            WHERE Ccode=@CCode AND LoyaltyGroupId=@GroupId AND Active=1
              AND (CONVERT(nvarchar(30),Id)=@Exact
                OR Name LIKE @Pattern ESCAPE N'~'
                OR CONVERT(nvarchar(30),MobileNo) LIKE @Pattern ESCAPE N'~')
            ORDER BY Name,Id;
            """;
        var result = new List<PromoCustomer>();
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        AddGroup(command, group);
        command.Parameters.Add("@Exact", SqlDbType.NVarChar, 30).Value = query.Trim();
        command.Parameters.Add("@Pattern", SqlDbType.NVarChar, 220).Value = $"%{escaped}%";
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : Convert.ToString(reader.GetValue(2)) ?? ""));
        return result;
    }

    public async Task<PromoCustomer?> GetCustomerAsync(long id, LoyaltyGroupSnapshot group,
        CancellationToken token)
    {
        const string sql = """
            SELECT Name, MobileNo FROM dbo.NPrivilegeCustomer
            WHERE Id=@CustomerId AND Ccode=@CCode AND LoyaltyGroupId=@GroupId AND Active=1;
            """;
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@CustomerId", SqlDbType.BigInt).Value = id;
        AddGroup(command, group);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new(id, reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1)) ?? "")
            : null;
    }

    public async Task<string> IssueAsync(PromoIssueViewModel model, LoyaltyGroupSnapshot group,
        CancellationToken token)
    {
        RequireCompany(group);
        bool fixedAmount = model.DiscountType == "Amount";
        if (!fixedAmount && model.DiscountType != "Percentage")
            throw new InvalidOperationException("Choose Amount or Percentage.");
        if (fixedAmount && (model.Value <= 0 || decimal.Round(model.Value, 2) != model.Value))
            throw new InvalidOperationException("Enter a positive fixed amount with at most two decimals.");
        if (!fixedAmount && (model.Value < 1 || model.Value > 100 || decimal.Truncate(model.Value) != model.Value))
            throw new InvalidOperationException("Enter a whole-number percentage from 1 to 100.");
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, token);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.Add("@PromoId", SqlDbType.Int).Value = model.PromoId;
            command.Parameters.Add("@CustomerId", SqlDbType.BigInt).Value = model.CustomerId;
            command.Parameters.Add("@Amount", SqlDbType.Money).Value = fixedAmount ? model.Value : 0m;
            command.Parameters.Add("@Percentage", SqlDbType.Int).Value = fixedAmount ? 0 : (int)model.Value;
            AddGroup(command, group);
            command.CommandText = """
                DECLARE @Validity datetime;
                SELECT @Validity=P.Validity
                FROM dbo.NPrivilagePromoCode P WITH (UPDLOCK,HOLDLOCK)
                WHERE P.Id=@PromoId AND P.CCode=@CCode AND P.LoyaltyGroupId=@GroupId;
                IF @Validity IS NULL OR @Validity<GETDATE()
                    THROW 51432, 'The promotion is unavailable or expired.', 1;
                IF NOT EXISTS
                (SELECT 1 FROM dbo.NPrivilagePromoCodeDepartment PD
                 JOIN dbo.NDepartmentMaster DEP ON DEP.Id=PD.DepartmentId AND DEP.IsActive=1
                 WHERE PD.PromoCodeId=@PromoId AND PD.IsActive=1)
                    THROW 51433, 'At least one active department is required.', 1;
                IF NOT EXISTS
                (SELECT 1 FROM dbo.NPrivilegeCustomer C WITH (UPDLOCK,HOLDLOCK)
                 WHERE C.Id=@CustomerId AND C.Ccode=@CCode
                   AND C.LoyaltyGroupId=@GroupId AND C.Active=1)
                    THROW 51434, 'Choose an active customer in this loyalty group.', 1;

                DECLARE @VoucherNo bigint;
                SELECT @VoucherNo =
                    CASE WHEN ISNULL(MAX(GVNo),0)<900000000000000
                         THEN 900000000000001 ELSE MAX(GVNo)+1 END
                FROM dbo.NGiftVoucherMaster WITH (TABLOCKX,HOLDLOCK);
                WHILE EXISTS (SELECT 1 FROM dbo.NPrivilagePromoCodeDetails
                              WHERE PromoCode='GxPrv'+CONVERT(varchar(30),@VoucherNo))
                   OR EXISTS (SELECT 1 FROM dbo.NPrivilegeVoucher
                              WHERE VoucherNo='GxPrv'+CONVERT(nvarchar(30),@VoucherNo))
                    SET @VoucherNo=@VoucherNo+1;

                INSERT dbo.NGiftVoucherMaster
                    (GVNo, InvNoIssued, ExpiryDate, SalesAmt, VouchAmt, Used,
                     CCode, InvNoUsed, VouchPerct, PrivilegeVoucher, BranchID, IsSynced)
                VALUES (@VoucherNo, NULL, @Validity, 0, @Amount, 0,
                        @CCode, NULL, @Percentage, 2, NULL, 1);
                INSERT dbo.NPrivilagePromoCodeDetails
                    (PromoCodeId, PromoCode, CustId, Used, InvNo, UsedAmt)
                VALUES (@PromoId, 'GxPrv'+CONVERT(varchar(30),@VoucherNo),
                        @CustomerId, 0, NULL, 0);
                SELECT 'GxPrv'+CONVERT(varchar(30),@VoucherNo);
                """;
            string code = Convert.ToString(await command.ExecuteScalarAsync(token))
                ?? throw new InvalidOperationException("PromoCode issuance did not return a code.");
            await transaction.CommitAsync(token);
            return code;
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    public async Task<int> IssueAudienceAsync(int promoId, IReadOnlyList<long> customerIds,
        string discountType, decimal value, LoyaltyGroupSnapshot group, CancellationToken token)
    {
        RequireCompany(group);
        if (customerIds.Count is < 1 or > 20_000 || customerIds.Distinct().Count() != customerIds.Count
            || customerIds.Any(id => id <= 0))
            throw new InvalidOperationException("The audience must contain 1 to 20,000 different active customers.");
        bool fixedAmount = discountType == "Amount";
        if (!fixedAmount && discountType != "Percentage")
            throw new InvalidOperationException("Choose a fixed amount or percentage.");
        if (fixedAmount && (value <= 0 || value > 99999999.99m || decimal.Round(value, 2) != value))
            throw new InvalidOperationException("Enter a positive fixed amount with at most two decimals.");
        if (!fixedAmount && (value < 1 || value > 100 || decimal.Truncate(value) != value))
            throw new InvalidOperationException("Enter a whole-number percentage from 1 to 100.");

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, token);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 120;
            command.Parameters.Add("@PromoId", SqlDbType.Int).Value = promoId;
            command.Parameters.Add("@CustomerJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(customerIds);
            command.Parameters.Add("@Expected", SqlDbType.Int).Value = customerIds.Count;
            command.Parameters.Add("@Amount", SqlDbType.Money).Value = fixedAmount ? value : 0m;
            command.Parameters.Add("@Percentage", SqlDbType.Int).Value = fixedAmount ? 0 : (int)value;
            AddGroup(command, group);
            command.CommandText = """
                CREATE TABLE #Recipients(CustomerId bigint NOT NULL PRIMARY KEY, VoucherNo bigint NULL);
                INSERT #Recipients(CustomerId)
                SELECT TRY_CONVERT(bigint,[value]) FROM OPENJSON(@CustomerJson)
                WHERE [type]=2 AND TRY_CONVERT(bigint,[value])>0;
                IF (SELECT COUNT(*) FROM #Recipients)<>@Expected
                    THROW 51440, 'The selected customer list is invalid.', 1;
                IF (SELECT COUNT_BIG(*) FROM dbo.NPrivilegeCustomer C WITH (HOLDLOCK)
                    JOIN #Recipients R ON R.CustomerId=C.Id
                    WHERE C.Ccode=@CCode AND C.LoyaltyGroupId=@GroupId AND C.Active=1)<>@Expected
                    THROW 51441, 'One or more customers are no longer active in this loyalty group.', 1;
                DECLARE @Validity datetime;
                SELECT @Validity=P.Validity FROM dbo.NPrivilagePromoCode P WITH (UPDLOCK,HOLDLOCK)
                WHERE P.Id=@PromoId AND P.CCode=@CCode AND P.LoyaltyGroupId=@GroupId;
                IF @Validity IS NULL OR @Validity<GETDATE()
                    THROW 51442, 'The campaign is unavailable or expired.', 1;
                IF NOT EXISTS
                (SELECT 1 FROM dbo.NPrivilagePromoCodeDepartment PD
                 JOIN dbo.NDepartmentMaster DEP ON DEP.Id=PD.DepartmentId AND DEP.IsActive=1
                 WHERE PD.PromoCodeId=@PromoId AND PD.IsActive=1)
                    THROW 51443, 'At least one active department is required.', 1;

                DECLARE @Start bigint, @Offset bigint=0;
                SELECT @Start=CASE WHEN ISNULL(MAX(GVNo),0)<900000000000000
                                   THEN 900000000000001 ELSE MAX(GVNo)+1 END
                FROM dbo.NGiftVoucherMaster WITH (TABLOCKX,HOLDLOCK);
                WHILE 1=1
                BEGIN
                    ;WITH Numbered AS
                    (SELECT CustomerId, ROW_NUMBER() OVER (ORDER BY CustomerId) AS RowNo FROM #Recipients)
                    UPDATE R SET VoucherNo=@Start+@Offset+N.RowNo-1
                    FROM #Recipients R JOIN Numbered N ON N.CustomerId=R.CustomerId;
                    IF NOT EXISTS
                    (SELECT 1 FROM #Recipients R JOIN dbo.NPrivilagePromoCodeDetails D
                     ON D.PromoCode='GxPrv'+CONVERT(varchar(30),R.VoucherNo))
                    AND NOT EXISTS
                    (SELECT 1 FROM #Recipients R JOIN dbo.NPrivilegeVoucher V
                     ON V.VoucherNo='GxPrv'+CONVERT(nvarchar(30),R.VoucherNo))
                        BREAK;
                    SET @Offset=@Offset+@Expected;
                END;
                INSERT dbo.NGiftVoucherMaster
                    (GVNo,InvNoIssued,ExpiryDate,SalesAmt,VouchAmt,Used,CCode,
                     InvNoUsed,VouchPerct,PrivilegeVoucher,BranchID,IsSynced)
                SELECT VoucherNo,NULL,@Validity,0,@Amount,0,@CCode,NULL,@Percentage,2,NULL,1
                FROM #Recipients;
                INSERT dbo.NPrivilagePromoCodeDetails
                    (PromoCodeId,PromoCode,CustId,Used,InvNo,UsedAmt)
                SELECT @PromoId,'GxPrv'+CONVERT(varchar(30),VoucherNo),CustomerId,0,NULL,0
                FROM #Recipients;
                SELECT COUNT(*) FROM #Recipients;
                """;
            int count = Convert.ToInt32(await command.ExecuteScalarAsync(token));
            await transaction.CommitAsync(token);
            return count;
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    public async Task<IReadOnlyList<PromoIssuedCode>> GetCodesAsync(int promoId,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        const string sql = """
            SELECT D.PromoCode, G.GVNo, D.CustId, C.Name, C.MobileNo,
                   ISNULL(G.VouchAmt,0), ISNULL(G.VouchPerct,0), G.ExpiryDate,
                   D.Used, ISNULL(G.Used,0), D.InvNo, D.UsedAmt
            FROM dbo.NPrivilagePromoCodeDetails D
            JOIN dbo.NPrivilagePromoCode P ON P.Id=D.PromoCodeId
            LEFT JOIN dbo.NPrivilegeCustomer C ON C.Id=D.CustId
              AND C.Ccode=P.CCode AND C.LoyaltyGroupId=P.LoyaltyGroupId
            JOIN dbo.NGiftVoucherMaster G
              ON D.PromoCode='GxPrv'+CONVERT(varchar(30),G.GVNo)
             AND G.CCode=P.CCode AND G.PrivilegeVoucher=2
            WHERE P.Id=@PromoId AND P.CCode=@CCode AND P.LoyaltyGroupId=@GroupId
            ORDER BY G.GVNo DESC;
            """;
        var result = new List<PromoIssuedCode>();
        await using var connection = new SqlConnection(ConnectionString);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@PromoId", SqlDbType.Int).Value = promoId;
        AddGroup(command, group);
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(ReadCode(reader));
        return result;
    }

    public async Task<PromoIssuedCode?> GetCodeAsync(int promoId, string code,
        LoyaltyGroupSnapshot group, CancellationToken token)
    {
        var codes = await GetCodesAsync(promoId, group, token);
        return codes.SingleOrDefault(x => string.Equals(x.Code, code, StringComparison.Ordinal));
    }

    public async Task RevokeAsync(int promoId, string code, LoyaltyGroupSnapshot group,
        CancellationToken token)
    {
        RequireCompany(group);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, token);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.Add("@PromoId", SqlDbType.Int).Value = promoId;
            command.Parameters.Add("@Code", SqlDbType.VarChar, 500).Value = code;
            AddGroup(command, group);
            command.CommandText = """
                DECLARE @GVNo bigint=TRY_CONVERT(bigint,SUBSTRING(@Code,6,30));
                IF @GVNo IS NULL OR @Code<>'GxPrv'+CONVERT(varchar(30),@GVNo) OR NOT EXISTS
                (SELECT 1 FROM dbo.NGiftVoucherMaster WITH (UPDLOCK,HOLDLOCK)
                 WHERE GVNo=@GVNo AND CCode=@CCode AND PrivilegeVoucher=2 AND Used=0)
                    THROW 51435, 'Only an unused issued PromoCode can be revoked.', 1;
                IF NOT EXISTS
                (SELECT 1 FROM dbo.NPrivilagePromoCodeDetails D WITH (UPDLOCK,HOLDLOCK)
                 JOIN dbo.NPrivilagePromoCode P ON P.Id=D.PromoCodeId
                 WHERE P.Id=@PromoId AND P.CCode=@CCode AND P.LoyaltyGroupId=@GroupId
                   AND D.PromoCode=@Code AND D.Used=0)
                    THROW 51435, 'Only an unused issued PromoCode can be revoked.', 1;
                UPDATE dbo.NGiftVoucherMaster SET Used=1, ModifiedDate=GETDATE()
                WHERE GVNo=@GVNo AND CCode=@CCode AND PrivilegeVoucher=2 AND Used=0;
                IF @@ROWCOUNT<>1 THROW 51437, 'The voucher changed during revocation.', 1;
                UPDATE dbo.NPrivilagePromoCodeDetails SET Used=2
                WHERE PromoCode=@Code AND PromoCodeId=@PromoId AND Used=0;
                IF @@ROWCOUNT<>1 THROW 51436, 'The PromoCode changed during revocation.', 1;
                """;
            await command.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    private static void AddGroup(SqlCommand command, LoyaltyGroupSnapshot group)
    {
        command.Parameters.Add("@CCode", SqlDbType.Int).Value = group.CCode;
        command.Parameters.Add("@GroupId", SqlDbType.Int).Value = group.LoyaltyGroupId;
    }

    private static PromoMasterSummary ReadMaster(SqlDataReader reader) => new(
        reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
        reader.GetDateTime(5), reader.GetInt32(6), reader.GetInt32(7));

    private static PromoIssuedCode ReadCode(SqlDataReader reader) => new(
        reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
        reader.IsDBNull(3) ? "" : reader.GetString(3),
        reader.IsDBNull(4) ? null : Convert.ToString(reader.GetValue(4)),
        reader.GetDecimal(5), reader.GetInt32(6), reader.GetDateTime(7),
        reader.GetInt32(8), reader.GetBoolean(9),
        reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.GetDecimal(11));
}

