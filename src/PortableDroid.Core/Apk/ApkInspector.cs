using System.IO.Compression;
using PortableDroid.Core.Models;

namespace PortableDroid.Core.Apk;

/// <summary>
/// Reads package metadata straight out of the APK zip, with no external tooling.
/// Used to warn about ARM-only apps and Google dependencies BEFORE attempting an install.
/// </summary>
public static class ApkInspector
{
    // Well-known android: attribute resource IDs (stable across all platform versions).
    private const uint AttrVersionCode = 0x0101021B;
    private const uint AttrVersionName = 0x0101021C;
    private const uint AttrMinSdkVersion = 0x0101020C;
    private const uint AttrTargetSdkVersion = 0x01010270;
    private const uint AttrLabel = 0x01010001;
    private const uint AttrName = 0x01010003;

    private static readonly string[] GoogleMarkers =
    {
        "com.google.android.gms",
        "com.google.android.c2dm",
        "com.google.firebase"
    };

    public static bool LooksLikeApk(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (!path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.GetEntry("AndroidManifest.xml") is not null;
        }
        catch
        {
            return false;
        }
    }

    public static ApkMetadata Inspect(string apkPath)
    {
        if (!File.Exists(apkPath))
            throw new FileNotFoundException("APK not found.", apkPath);

        using var zip = ZipFile.OpenRead(apkPath);

        var manifestEntry = zip.GetEntry("AndroidManifest.xml")
            ?? throw new InvalidDataException("The archive does not contain AndroidManifest.xml.");

        byte[] manifestBytes;
        using (var stream = manifestEntry.Open())
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            manifestBytes = buffer.ToArray();
        }

        var elements = BinaryXmlReader.Parse(manifestBytes);

        var manifest = elements.FirstOrDefault(e => e.Name == "manifest");
        var usesSdk = elements.FirstOrDefault(e => e.Name == "uses-sdk");
        var application = elements.FirstOrDefault(e => e.Name == "application");

        var packageName = manifest is null
            ? ""
            : manifest.Attributes.FirstOrDefault(a => a.Name == "package" && a.ResourceId == 0)?.Value ?? "";

        var versionName = FindAttr(manifest, AttrVersionName, "versionName") ?? "";
        var versionCodeText = FindAttr(manifest, AttrVersionCode, "versionCode") ?? "0";
        _ = long.TryParse(versionCodeText, out var versionCode);

        _ = int.TryParse(FindAttr(usesSdk, AttrMinSdkVersion, "minSdkVersion") ?? "0", out var minSdk);
        _ = int.TryParse(FindAttr(usesSdk, AttrTargetSdkVersion, "targetSdkVersion") ?? "0", out var targetSdk);

        var label = FindAttr(application, AttrLabel, "label") ?? "";
        if (string.IsNullOrWhiteSpace(label) || label.StartsWith('@'))
            label = DeriveNameFromPackage(packageName, apkPath);

        var abis = zip.Entries
            .Select(e => e.FullName)
            .Where(n => n.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Split('/'))
            .Where(parts => parts.Length >= 3)
            .Select(parts => parts[1])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var usesGoogle = elements.Any(e =>
            (e.Name == "uses-library" || e.Name == "meta-data" || e.Name == "service" ||
             e.Name == "receiver" || e.Name == "uses-permission" || e.Name == "activity") &&
            e.Attributes.Any(a =>
                (a.ResourceId == AttrName || a.Name == "name") &&
                GoogleMarkers.Any(m => a.Value.Contains(m, StringComparison.OrdinalIgnoreCase))));

        return new ApkMetadata
        {
            PackageName = packageName,
            ApplicationLabel = label,
            VersionName = versionName,
            VersionCode = versionCode,
            MinSdkVersion = minSdk,
            TargetSdkVersion = targetSdk,
            NativeAbis = abis,
            UsesGooglePlayServices = usesGoogle,
            FileSizeBytes = new FileInfo(apkPath).Length
        };
    }

    /// <summary>Maps inspected metadata onto the compatibility taxonomy of spec §26.</summary>
    public static CompatibilityStatus Classify(ApkMetadata metadata, int runtimeApiLevel = 0)
    {
        if (metadata.IsArmOnly) return CompatibilityStatus.ArmOnly;
        if (runtimeApiLevel > 0 && metadata.MinSdkVersion > runtimeApiLevel)
            return CompatibilityStatus.AndroidApiIssue;
        if (metadata.UsesGooglePlayServices) return CompatibilityStatus.GoogleDependency;
        return CompatibilityStatus.Compatible;
    }

    private static string? FindAttr(BinaryXmlReader.XmlElement? element, uint resourceId, string fallbackName)
    {
        if (element is null) return null;

        var byId = element.Attributes.FirstOrDefault(a => a.ResourceId == resourceId);
        if (byId is not null) return byId.Value;

        return element.Attributes.FirstOrDefault(a =>
            string.Equals(a.Name, fallbackName, StringComparison.Ordinal))?.Value;
    }

    private static string DeriveNameFromPackage(string packageName, string apkPath)
    {
        if (!string.IsNullOrWhiteSpace(packageName))
        {
            var last = packageName.Split('.').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last))
                return char.ToUpperInvariant(last[0]) + last[1..];
        }
        return Path.GetFileNameWithoutExtension(apkPath);
    }
}
