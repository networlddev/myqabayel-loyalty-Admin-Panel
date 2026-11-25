using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MyQabayel.Admin.Data;
using MyQabayel.Admin.Models;
using MyQabayel.Admin.Services;

namespace MyQabayel.Admin.Controllers;

public sealed class NotificationSettingsController(
    IConfiguration configuration,
    IWebHostEnvironment hostingEnvironment,
    FirebasePushSender firebase,
    ILogger<NotificationSettingsController> logger,
    AdminDbContext db, LoyaltyCustomerAudience audience) : Controller
{
    private const long MaxCredentialBytes = 128 * 1024;
    private static readonly string[] Environments = ["Development", "Production"];

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        string groupCode = await audience.GetConfiguredGroupCodeAsync(cancellationToken);
        var iosClients = await db.FirebaseIosClientConfigurations.AsNoTracking()
            .Where(x => x.GroupCode == groupCode).ToListAsync(cancellationToken);
        return View(new NotificationSettingsViewModel
        {
            Environments = Environments.Select(GetStatus).ToList(),
            DevelopmentIosClient = iosClients.SingleOrDefault(x => x.Environment == "Development"),
            ProductionIosClient = iosClients.SingleOrDefault(x => x.Environment == "Production")
        });
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(256 * 1024)]
    public async Task<IActionResult> UploadIosClient(string? environment, IFormFile? iosPlist,
        CancellationToken cancellationToken)
    {
        environment ??= "Development";
        if (!Environments.Contains(environment, StringComparer.Ordinal)) return BadRequest();
        if (iosPlist is null || iosPlist.Length is < 1 or > MaxCredentialBytes
            || !string.Equals(Path.GetExtension(iosPlist.FileName), ".plist",
                StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = $"Choose the {environment} iOS GoogleService-Info.plist file, smaller than 128 KB.";
            return RedirectToAction(nameof(Index));
        }
        try
        {
            string groupCode = await audience.GetConfiguredGroupCodeAsync(cancellationToken);
            if (groupCode != "QABAYEL")
                throw new InvalidOperationException("This workspace only manages the MY QABAYEL loyalty group.");
            string plist;
            await using (var stream = iosPlist.OpenReadStream())
            using (var reader = new StreamReader(stream))
                plist = await reader.ReadToEndAsync(cancellationToken);
            var parsed = FirebaseIosPlistParser.Parse(plist, groupCode, environment, configuration);
            var existing = await db.FirebaseIosClientConfigurations.FindAsync(
                [groupCode, environment], cancellationToken);
            if (existing is null)
                db.FirebaseIosClientConfigurations.Add(parsed);
            else
            {
                existing.ProjectId = parsed.ProjectId;
                existing.AppId = parsed.AppId;
                existing.MessagingSenderId = parsed.MessagingSenderId;
                existing.ApiKey = parsed.ApiKey;
                existing.StorageBucket = parsed.StorageBucket;
                existing.IosBundleId = parsed.IosBundleId;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(cancellationToken);
            TempData["Success"] = $"{environment} iOS Firebase client configuration saved. The matching mobile API can now serve it.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unable to save {Environment} iOS Firebase client configuration", environment);
            TempData["Error"] = "The iOS client configuration could not be saved. Check the shared Admin database and try again.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(256 * 1024)]
    public async Task<IActionResult> Upload(string environment, IFormFile? credentialFile,
        CancellationToken cancellationToken)
    {
        string[] selectedEnvironments = environment == "Both" ? Environments
            : Environments.Contains(environment, StringComparer.Ordinal) ? [environment]
            : [];
        if (selectedEnvironments.Length == 0) return BadRequest();
        if (credentialFile is null || credentialFile.Length is < 1 or > MaxCredentialBytes
            || !string.Equals(Path.GetExtension(credentialFile.FileName), ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Choose a Firebase service-account JSON file smaller than 128 KB.";
            return RedirectToAction(nameof(Index));
        }

        string json;
        await using (var input = credentialFile.OpenReadStream())
        using (var reader = new StreamReader(input))
            json = await reader.ReadToEndAsync(cancellationToken);

        var targets = new List<CredentialTarget>();
        string projectId = "";
        try
        {
            foreach (string selectedEnvironment in selectedEnvironments)
            {
                string? configuredPath = configuration[$"PushNotifications:{selectedEnvironment}:CredentialPath"];
                if (string.IsNullOrWhiteSpace(configuredPath) || !Path.IsPathFullyQualified(configuredPath))
                    throw new InvalidOperationException("The Firebase credential path is not configured on the server.");
                string path = Path.GetFullPath(configuredPath);
                if (!string.Equals(Path.GetFileName(path), "service-account.json", StringComparison.OrdinalIgnoreCase)
                    || IsInsideWebRoot(path))
                    throw new InvalidOperationException("The Firebase credential path must be outside wwwroot.");

                projectId = FirebaseCredentialValidator.Validate(json, selectedEnvironment, configuration);
                targets.Add(new CredentialTarget(selectedEnvironment, path));
            }
            if (targets.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
                throw new InvalidOperationException("Each environment must have its own server file path.");
        }
        catch (FirebaseCredentialValidationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TempData["Error"] = "The JSON file or configured server path is invalid. Upload a Firebase Admin SDK service-account JSON file.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            foreach (var target in targets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
                await System.IO.File.WriteAllTextAsync(target.TemporaryPath, json, cancellationToken);
                target.HadExistingFile = System.IO.File.Exists(target.Path);
                if (target.HadExistingFile) System.IO.File.Copy(target.Path, target.BackupPath);
            }
            foreach (var target in targets)
            {
                System.IO.File.Move(target.TemporaryPath, target.Path, overwrite: true);
                target.Replaced = true;
            }
            foreach (var target in targets) firebase.Reload(target.Environment);
            if (targets.Any(target => !firebase.IsConfigured(target.Environment)))
                throw new InvalidOperationException("Firebase could not load the uploaded credential.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to configure Firebase for {Environment}", environment);
            bool rollbackFailed = false;
            foreach (var target in targets.Where(x => x.Replaced).Reverse())
            {
                try
                {
                    if (target.HadExistingFile) System.IO.File.Move(target.BackupPath, target.Path, overwrite: true);
                    else System.IO.File.Delete(target.Path);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    target.PreserveBackup = true;
                    rollbackFailed = true;
                    logger.LogError(restoreError, "Unable to restore previous Firebase {Environment} credential", target.Environment);
                }
            }
            foreach (var target in targets) firebase.Reload(target.Environment);
            if (ex is OperationCanceledException) throw;
            TempData["Error"] = rollbackFailed
                ? "Firebase setup failed and an earlier file could not be restored. A backup remains on the server."
                : "Firebase setup failed. The previous configuration was kept; check the file and server folder permissions.";
            return RedirectToAction(nameof(Index));
        }
        finally
        {
            foreach (var target in targets)
            {
                TryDelete(target.TemporaryPath);
                if (!target.PreserveBackup) TryDelete(target.BackupPath);
            }
        }

        TempData["Success"] = environment == "Both"
            ? $"Firebase service account configured for Development and Production ({projectId})."
            : $"{environment} Firebase service account configured for {projectId}.";
        return RedirectToAction(nameof(Index));
    }

    private sealed class CredentialTarget(string environment, string path)
    {
        public string Environment { get; } = environment;
        public string Path { get; } = path;
        public string TemporaryPath { get; } = $"{path}.{Guid.NewGuid():N}.tmp";
        public string BackupPath { get; } = $"{path}.{Guid.NewGuid():N}.bak";
        public bool HadExistingFile { get; set; }
        public bool Replaced { get; set; }
        public bool PreserveBackup { get; set; }
    }

    private FirebaseEnvironmentStatus GetStatus(string environment)
    {
        string path = configuration[$"PushNotifications:{environment}:CredentialPath"] ?? "Not configured";
        string projectId = configuration[$"PushNotifications:{environment}:ExpectedProjectId"] ?? "";
        if (!System.IO.File.Exists(path))
            return new(environment, path, projectId, "Missing", "Upload a Firebase Admin SDK service-account JSON file.");
        try
        {
            string detectedProject = FirebaseCredentialValidator.Validate(
                System.IO.File.ReadAllText(path), environment, configuration);
            return firebase.IsConfigured(environment)
                ? new(environment, path, detectedProject, "Configured", "Server credentials loaded. A real device token is still needed to test delivery.")
                : new(environment, path, detectedProject, "Invalid", "The sender could not load this file. Upload a valid service account.");
        }
        catch (Exception)
        {
            return new(environment, path, projectId, "Invalid", "The file is not valid for this Firebase project. Upload a matching service account.");
        }
    }

    private bool IsInsideWebRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(hostingEnvironment.WebRootPath)) return false;
        string webRoot = Path.GetFullPath(hostingEnvironment.WebRootPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (IOException ex) { logger.LogWarning(ex, "Unable to remove temporary Firebase credential file"); }
        catch (UnauthorizedAccessException ex) { logger.LogWarning(ex, "Unable to remove temporary Firebase credential file"); }
    }
}
