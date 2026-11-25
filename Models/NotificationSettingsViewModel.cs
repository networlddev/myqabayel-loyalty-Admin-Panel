namespace MyQabayel.Admin.Models;

public sealed record FirebaseEnvironmentStatus(
    string Environment,
    string CredentialPath,
    string ProjectId,
    string Status,
    string Description);

public sealed class NotificationSettingsViewModel
{
    public List<FirebaseEnvironmentStatus> Environments { get; set; } = [];
    public FirebaseIosClientConfiguration? DevelopmentIosClient { get; set; }
    public FirebaseIosClientConfiguration? ProductionIosClient { get; set; }
}
