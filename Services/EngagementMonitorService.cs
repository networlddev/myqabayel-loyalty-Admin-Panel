using System.Data;
using Microsoft.Data.SqlClient;

namespace QabayelMall.API.Services;

public sealed record EngagementContent(long Id, string ContentType, string Title, string Status,
    DateTime UpdatedAtUtc, long Reactions, long Comments);
public sealed record EngagementReaction(long Id, long CustomerId, string Reaction, DateTime UpdatedAtUtc);
public sealed record EngagementComment(long Id, long CustomerId, string CustomerName,
    string Text, bool IsVisible, DateTime CreatedAtUtc);

public sealed class EngagementIndexModel
{
    public string? ContentType { get; init; }
    public string? Search { get; init; }
    public long? CustomerId { get; init; }
    public int Page { get; init; }
    public bool HasMore { get; set; }
    public List<EngagementContent> Items { get; } = [];
}

public sealed class EngagementDetailsModel
{
    public required EngagementContent Content { get; set; }
    public Dictionary<string, long> ReactionCounts { get; } = new()
    {
        ["Like"] = 0, ["Love"] = 0, ["Haha"] = 0,
        ["Wow"] = 0, ["Sad"] = 0, ["Angry"] = 0
    };
    public int ReactionPage { get; init; }
    public int CommentPage { get; init; }
    public bool MoreReactions { get; set; }
    public bool MoreComments { get; set; }
    public List<EngagementReaction> Reactions { get; } = [];
    public List<EngagementComment> Comments { get; } = [];
}

public sealed class EngagementMonitorService(IConfiguration configuration)
{
    private const int PageSize = 50;
    private const string NotificationTable = "dbo.AdminAppNotifications";

    private SqlConnection Connect() => new(configuration.GetConnectionString("AdminDb")
        ?? throw new InvalidOperationException("ConnectionStrings:AdminDb is missing."));

    private static void AddScope(SqlCommand command, string groupCode, string? type = null,
        long? contentId = null)
    {
        command.Parameters.Add("@GroupCode", SqlDbType.NVarChar, 100).Value = groupCode;
        if (type is not null)
            command.Parameters.Add("@ContentType", SqlDbType.NVarChar, 20).Value = type;
        if (contentId.HasValue)
            command.Parameters.Add("@ContentId", SqlDbType.BigInt).Value = contentId.Value;
    }

    public static string? NormalizeType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "flyer" => "Flyer", "offer" => "Offer", "notification" => "Notification", _ => null
    };

    public async Task<EngagementIndexModel> ListAsync(string groupCode, string? type,
        string? search, long? customerId, int page, CancellationToken token)
    {
        page = Math.Clamp(page, 1, 10000);
        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim()[..Math.Min(search.Trim().Length, 100)];
        var model = new EngagementIndexModel
        {
            ContentType = type, Search = search, CustomerId = customerId, Page = page
        };
        var sql = $"""
            WITH Items AS
            (
                SELECT C.Id, C.CampaignType AS ContentType, C.Title, C.Status, C.UpdatedAtUtc
                FROM dbo.AdminPanelCampaigns C
                WHERE C.GroupCode = @GroupCode AND C.CampaignType IN (N'Flyer', N'Offer')
                UNION ALL
                SELECT N.Id, N'Notification', N.Title, N.Status, N.UpdatedAtUtc
                FROM {NotificationTable} N WHERE N.GroupCode = @GroupCode
            )
            SELECT I.Id, I.ContentType, I.Title, I.Status, I.UpdatedAtUtc,
                   R.Reactions, CM.Comments
            FROM Items I
            OUTER APPLY (SELECT COUNT_BIG(*) Reactions FROM dbo.AdminContentReactions R
                WHERE R.GroupCode = @GroupCode AND R.ContentType = I.ContentType
                  AND R.ContentId = I.Id) R
            OUTER APPLY (SELECT COUNT_BIG(*) Comments FROM dbo.AdminContentComments C
                WHERE C.GroupCode = @GroupCode AND C.ContentType = I.ContentType
                  AND C.ContentId = I.Id AND C.IsVisible = 1) CM
            WHERE (@Type IS NULL OR I.ContentType = @Type)
              AND (@Search IS NULL OR I.Title LIKE N'%' + @Search + N'%'
                   OR I.Id = TRY_CONVERT(BIGINT, @Search))
              AND (@CustomerId IS NULL OR EXISTS
                    (SELECT 1 FROM dbo.AdminContentReactions CR
                     WHERE CR.GroupCode = @GroupCode AND CR.ContentType = I.ContentType
                       AND CR.ContentId = I.Id AND CR.CustomerId = @CustomerId)
                   OR EXISTS
                    (SELECT 1 FROM dbo.AdminContentComments CC
                     WHERE CC.GroupCode = @GroupCode AND CC.ContentType = I.ContentType
                       AND CC.ContentId = I.Id AND CC.CustomerId = @CustomerId))
            ORDER BY I.UpdatedAtUtc DESC, I.Id DESC
            OFFSET @Offset ROWS FETCH NEXT 51 ROWS ONLY;
            """;
        await using var connection = Connect();
        await using var command = new SqlCommand(sql, connection);
        AddScope(command, groupCode);
        command.Parameters.Add("@Type", SqlDbType.NVarChar, 20).Value = (object?)type ?? DBNull.Value;
        command.Parameters.Add("@Search", SqlDbType.NVarChar, 100).Value = (object?)search ?? DBNull.Value;
        command.Parameters.Add("@CustomerId", SqlDbType.BigInt).Value = (object?)customerId ?? DBNull.Value;
        command.Parameters.Add("@Offset", SqlDbType.Int).Value = (page - 1) * PageSize;
        await connection.OpenAsync(token);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            model.Items.Add(new EngagementContent(reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetDateTime(4),
                reader.GetInt64(5), reader.GetInt64(6)));
        }
        model.HasMore = model.Items.Count > PageSize;
        if (model.HasMore) model.Items.RemoveAt(PageSize);
        return model;
    }

    public async Task<EngagementDetailsModel?> DetailsAsync(string groupCode, string type,
        long contentId, int reactionPage, int commentPage, CancellationToken token)
    {
        reactionPage = Math.Clamp(reactionPage, 1, 10000);
        commentPage = Math.Clamp(commentPage, 1, 10000);
        string contentSql = type == "Notification"
            ? $"SELECT Id, Title, Status, UpdatedAtUtc FROM {NotificationTable} WHERE GroupCode=@GroupCode AND Id=@ContentId"
            : "SELECT Id, Title, Status, UpdatedAtUtc FROM dbo.AdminPanelCampaigns WHERE GroupCode=@GroupCode AND CampaignType=@ContentType AND Id=@ContentId";
        await using var connection = Connect();
        await connection.OpenAsync(token);
        EngagementContent content;
        await using (var command = new SqlCommand(contentSql, connection))
        {
            AddScope(command, groupCode, type, contentId);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            content = new EngagementContent(reader.GetInt64(0), type, reader.GetString(1),
                reader.GetString(2), reader.GetDateTime(3), 0, 0);
        }
        var model = new EngagementDetailsModel
        {
            Content = content, ReactionPage = reactionPage, CommentPage = commentPage
        };
        const string countsSql = """
            SELECT Reaction, COUNT_BIG(*) FROM dbo.AdminContentReactions
            WHERE GroupCode=@GroupCode AND ContentType=@ContentType AND ContentId=@ContentId
            GROUP BY Reaction;
            SELECT COUNT_BIG(*) FROM dbo.AdminContentComments
            WHERE GroupCode=@GroupCode AND ContentType=@ContentType AND ContentId=@ContentId
              AND IsVisible=1;
            """;
        long reactionCount = 0, commentCount = 0;
        await using (var command = new SqlCommand(countsSql, connection))
        {
            AddScope(command, groupCode, type, contentId);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var count = reader.GetInt64(1);
                model.ReactionCounts[reader.GetString(0)] = count;
                reactionCount += count;
            }
            await reader.NextResultAsync(token);
            if (await reader.ReadAsync(token)) commentCount = reader.GetInt64(0);
        }
        model.Content = content with { Reactions = reactionCount, Comments = commentCount };
        const string reactionsSql = """
            SELECT Id, CustomerId, Reaction, UpdatedAtUtc FROM dbo.AdminContentReactions
            WHERE GroupCode=@GroupCode AND ContentType=@ContentType AND ContentId=@ContentId
            ORDER BY Id DESC OFFSET @Offset ROWS FETCH NEXT 51 ROWS ONLY;
            """;
        await using (var command = new SqlCommand(reactionsSql, connection))
        {
            AddScope(command, groupCode, type, contentId);
            command.Parameters.Add("@Offset", SqlDbType.Int).Value = (reactionPage - 1) * PageSize;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                model.Reactions.Add(new EngagementReaction(reader.GetInt64(0), reader.GetInt64(1),
                    reader.GetString(2), reader.GetDateTime(3)));
        }
        model.MoreReactions = model.Reactions.Count > PageSize;
        if (model.MoreReactions) model.Reactions.RemoveAt(PageSize);
        const string commentsSql = """
            SELECT Id, CustomerId, CustomerName, CommentText, IsVisible, CreatedAtUtc
            FROM dbo.AdminContentComments
            WHERE GroupCode=@GroupCode AND ContentType=@ContentType AND ContentId=@ContentId
            ORDER BY Id DESC OFFSET @Offset ROWS FETCH NEXT 51 ROWS ONLY;
            """;
        await using (var command = new SqlCommand(commentsSql, connection))
        {
            AddScope(command, groupCode, type, contentId);
            command.Parameters.Add("@Offset", SqlDbType.Int).Value = (commentPage - 1) * PageSize;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                model.Comments.Add(new EngagementComment(reader.GetInt64(0), reader.GetInt64(1),
                    reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetDateTime(5)));
        }
        model.MoreComments = model.Comments.Count > PageSize;
        if (model.MoreComments) model.Comments.RemoveAt(PageSize);
        return model;
    }

    public async Task<bool> SetCommentVisibilityAsync(string groupCode, string type,
        long contentId, long commentId, bool visible, CancellationToken token)
    {
        const string sql = """
            UPDATE dbo.AdminContentComments SET IsVisible=@Visible
            WHERE Id=@Id AND GroupCode=@GroupCode AND ContentType=@ContentType
              AND ContentId=@ContentId;
            """;
        await using var connection = Connect();
        await using var command = new SqlCommand(sql, connection);
        AddScope(command, groupCode, type, contentId);
        command.Parameters.Add("@Id", SqlDbType.BigInt).Value = commentId;
        command.Parameters.Add("@Visible", SqlDbType.Bit).Value = visible;
        await connection.OpenAsync(token);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task<bool> RemoveReactionAsync(string groupCode, string type,
        long contentId, long reactionId, CancellationToken token)
    {
        const string sql = """
            DELETE FROM dbo.AdminContentReactions
            WHERE Id=@Id AND GroupCode=@GroupCode AND ContentType=@ContentType
              AND ContentId=@ContentId;
            """;
        await using var connection = Connect();
        await using var command = new SqlCommand(sql, connection);
        AddScope(command, groupCode, type, contentId);
        command.Parameters.Add("@Id", SqlDbType.BigInt).Value = reactionId;
        await connection.OpenAsync(token);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }
}
