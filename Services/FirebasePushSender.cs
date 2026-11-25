using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace MyQabayel.Admin.Services;

public sealed class FirebasePushSender(IConfiguration configuration, ILogger<FirebasePushSender> logger) : IDisposable
{
    private readonly Dictionary<string, FirebaseApp> _apps = new(StringComparer.Ordinal);
    private readonly List<FirebaseApp> _retiredApps = [];
    private readonly object _sync = new();

    public bool IsConfigured(string environment)
    {
        string? path = configuration[$"PushNotifications:{environment}:CredentialPath"];
        if (environment is not ("Development" or "Production")
            || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try { _ = GetApp(environment); return true; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Firebase {Environment} credentials could not be loaded", environment);
            return false;
        }
    }

    public void Reload(string environment)
    {
        lock (_sync)
        {
            if (_apps.Remove(environment, out var app)) _retiredApps.Add(app);
        }
    }

    public async Task SendAsync(string environment, string token, long notificationId,
        string groupCode, string title, string body, string? imageUrl,
        string targetType, long? targetId, CancellationToken cancellationToken)
    {
        var messaging = FirebaseMessaging.GetMessaging(GetApp(environment));
        string screen = targetType switch
        {
            "Offer" => "offer",
            "Flyer" => "flyer",
            "Advertisement" => "advertisement",
            _ => "notifications"
        };
        var data = new Dictionary<string, string>
        {
            ["notificationId"] = notificationId.ToString(),
            ["screen"] = screen,
            ["groupCode"] = groupCode
        };
        if (targetId.HasValue) data["targetId"] = targetId.Value.ToString();
        if (!string.IsNullOrWhiteSpace(imageUrl)) data["imageUrl"] = imageUrl;
        var message = new Message
        {
            Token = token,
            Notification = new Notification
            {
                Title = title,
                Body = body,
                ImageUrl = imageUrl is not null && imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? imageUrl : null
            },
            Data = data
        };
        await messaging.SendAsync(message, cancellationToken);
    }

    private FirebaseApp GetApp(string environment)
    {
        lock (_sync)
        {
            if (_apps.TryGetValue(environment, out var app)) return app;
            string path = configuration[$"PushNotifications:{environment}:CredentialPath"]
                ?? throw new InvalidOperationException($"Firebase {environment} credential path is not configured.");
            if (!File.Exists(path)) throw new FileNotFoundException("Firebase credential file was not found.", path);
            string projectId = FirebaseCredentialValidator.Validate(
                File.ReadAllText(path), environment, configuration);
            app = FirebaseApp.Create(new AppOptions
            {
                Credential = GoogleCredential.FromFile(path),
                ProjectId = projectId
            }, $"MyQabayel-{environment}-{Guid.NewGuid():N}");
            _apps.Add(environment, app);
            logger.LogInformation("Firebase {Environment} sender initialized", environment);
            return app;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var app in _apps.Values) app.Delete();
            foreach (var app in _retiredApps) app.Delete();
            _apps.Clear();
            _retiredApps.Clear();
        }
    }
}
