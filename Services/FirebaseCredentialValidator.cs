using System.Text.Json;
using Google.Apis.Auth.OAuth2;

namespace MyQabayel.Admin.Services;

public static class FirebaseCredentialValidator
{
    public static string Validate(string json, string environment, IConfiguration configuration)
    {
        if (environment is not ("Development" or "Production"))
            throw new InvalidOperationException("Choose Development or Production.");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string? type = ReadString(root, "type");
        string? projectId = ReadString(root, "project_id");
        string? clientEmail = ReadString(root, "client_email");
        string? privateKey = ReadString(root, "private_key");
        if (type != "service_account")
            throw new FirebaseCredentialValidationException(
                root.TryGetProperty("project_info", out _) && root.TryGetProperty("client", out _)
                    ? "This is an Android google-services.json file. In Firebase Console, use Project settings → Service accounts → Generate new private key, then upload that downloaded JSON."
                    : "This JSON is not a Firebase Admin SDK service-account file.");
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(clientEmail)
            || string.IsNullOrWhiteSpace(privateKey))
            throw new FirebaseCredentialValidationException(
                "The service-account JSON is missing its project ID, client email, or private key.");

        string? expectedProjectId = configuration[$"PushNotifications:{environment}:ExpectedProjectId"];
        if (environment == "Development" && string.IsNullOrWhiteSpace(expectedProjectId))
            throw new FirebaseCredentialValidationException("The Development Firebase project ID is not configured on the server.");
        if (!string.IsNullOrWhiteSpace(expectedProjectId)
            && !string.Equals(projectId, expectedProjectId, StringComparison.Ordinal))
            throw new FirebaseCredentialValidationException(
                $"This service account is for a different Firebase project. Expected {expectedProjectId}.");
        try { _ = GoogleCredential.FromJson(json); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FirebaseCredentialValidationException(
                "Firebase could not read this private key. Generate a new service-account JSON in Firebase Console.");
        }
        return projectId;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

public sealed class FirebaseCredentialValidationException(string message) : Exception(message);
