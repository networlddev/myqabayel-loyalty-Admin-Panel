using System.Xml;
using System.Xml.Linq;
using QabayelMall.API.Models;

namespace QabayelMall.API.Services;

public static class FirebaseIosPlistParser
{
    public static FirebaseIosClientConfiguration Parse(string plist, string groupCode, string environment,
        IConfiguration configuration)
    {
        if (environment is not ("Development" or "Production"))
            throw new InvalidOperationException("Choose Development or Production for the iOS app.");
        string Expected(string key) => configuration[$"PushNotifications:{environment}:Ios:{key}"]?.Trim() is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Configure the MY QABAYEL {environment} iOS {key} before uploading a plist.");
        var expectedProjectId = Expected("ProjectId");
        var expectedAppId = Expected("AppId");
        var expectedSenderId = Expected("SenderId");
        var expectedStorageBucket = Expected("StorageBucket");
        var expectedBundleId = Expected("BundleId");
        try
        {
            using var text = new StringReader(plist);
            using var xml = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null
            });
            var document = XDocument.Load(xml);
            if (document.Root?.Name.LocalName != "plist")
                throw new InvalidOperationException("Upload a Firebase iOS GoogleService-Info.plist file.");
            var children = document.Root.Element("dict")?.Elements().ToArray();
            if (children is null || children.Length % 2 != 0)
                throw new InvalidOperationException("The Firebase iOS plist is missing its settings dictionary.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < children.Length; index += 2)
            {
                if (children[index].Name.LocalName != "key" || !keys.Add(children[index].Value))
                    throw new InvalidOperationException("The Firebase iOS plist has invalid or repeated settings.");
                if (children[index + 1].Name.LocalName == "string")
                    values.Add(children[index].Value, children[index + 1].Value);
            }
            string Get(string name) => values.TryGetValue(name, out var value) ? value.Trim() : "";
            var result = new FirebaseIosClientConfiguration
            {
                GroupCode = groupCode,
                Environment = environment,
                ProjectId = Get("PROJECT_ID"),
                AppId = Get("GOOGLE_APP_ID"),
                MessagingSenderId = Get("GCM_SENDER_ID"),
                ApiKey = Get("API_KEY"),
                StorageBucket = Get("STORAGE_BUCKET"),
                IosBundleId = Get("BUNDLE_ID")
            };
            if (result.ProjectId != expectedProjectId || result.AppId != expectedAppId
                || result.MessagingSenderId != expectedSenderId
                || result.StorageBucket != expectedStorageBucket
                || result.IosBundleId != expectedBundleId)
                throw new InvalidOperationException($"This iOS plist does not match the MY QABAYEL {environment} Firebase app ({expectedBundleId}).");
            if (result.ApiKey.Length is < 20 or > 200 || result.ApiKey.Any(char.IsWhiteSpace)
                || result.ApiKey.Contains('<') || result.ApiKey.Contains('>'))
                throw new InvalidOperationException("The iOS plist does not contain a valid API_KEY.");
            return result;
        }
        catch (XmlException)
        {
            throw new InvalidOperationException("Upload a valid Firebase iOS GoogleService-Info.plist file.");
        }
    }
}
