using System.IO.Compression;
using System.Text;
using PortableDroid.Core.Apk;
using PortableDroid.Core.Models;
using Xunit;

namespace PortableDroid.Tests;

/// <summary>
/// Builds real binary-XML APKs in memory and parses them back, so the manifest reader is
/// tested against the actual format rather than a mock.
/// </summary>
public class ApkInspectorTests : IDisposable
{
    private readonly string _dir;

    public ApkInspectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pd-apk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public void ReadsPackageNameVersionAndSdkLevels()
    {
        var apk = CreateApk("com.example.notes", "2.4.1", versionCode: 241, minSdk: 24, targetSdk: 33);
        var metadata = ApkInspector.Inspect(apk);

        Assert.Equal("com.example.notes", metadata.PackageName);
        Assert.Equal("2.4.1", metadata.VersionName);
        Assert.Equal(241, metadata.VersionCode);
        Assert.Equal(24, metadata.MinSdkVersion);
        Assert.Equal(33, metadata.TargetSdkVersion);
        Assert.True(metadata.FileSizeBytes > 0);
    }

    [Fact]
    public void DetectsAnArmOnlyApp()
    {
        var apk = CreateApk("com.example.armonly", abis: new[] { "arm64-v8a", "armeabi-v7a" });
        var metadata = ApkInspector.Inspect(apk);

        Assert.True(metadata.IsArmOnly);
        Assert.Equal(CompatibilityStatus.ArmOnly, ApkInspector.Classify(metadata));
    }

    [Fact]
    public void AnAppWithX86CodeIsNotArmOnly()
    {
        var apk = CreateApk("com.example.universal", abis: new[] { "arm64-v8a", "x86_64" });
        var metadata = ApkInspector.Inspect(apk);

        Assert.False(metadata.IsArmOnly);
        Assert.Equal(CompatibilityStatus.Compatible, ApkInspector.Classify(metadata));
    }

    [Fact]
    public void APureJavaAppIsNotArmOnly()
    {
        var metadata = ApkInspector.Inspect(CreateApk("com.example.pure"));
        Assert.Empty(metadata.NativeAbis);
        Assert.False(metadata.IsArmOnly);
    }

    [Fact]
    public void FlagsAnAppThatNeedsGoogleServices()
    {
        var apk = CreateApk("com.example.mapsy", usesGoogle: true);
        var metadata = ApkInspector.Inspect(apk);

        Assert.True(metadata.UsesGooglePlayServices);
        Assert.Equal(CompatibilityStatus.GoogleDependency, ApkInspector.Classify(metadata));
    }

    [Fact]
    public void FlagsAnAppThatNeedsANewerAndroid()
    {
        var metadata = ApkInspector.Inspect(CreateApk("com.example.modern", minSdk: 34));
        Assert.Equal(CompatibilityStatus.AndroidApiIssue, ApkInspector.Classify(metadata, runtimeApiLevel: 30));
    }

    [Fact]
    public void LooksLikeApk_RejectsNonApkFiles()
    {
        var notApk = Path.Combine(_dir, "photo.jpg");
        File.WriteAllText(notApk, "not an apk");
        Assert.False(ApkInspector.LooksLikeApk(notApk));

        var missing = Path.Combine(_dir, "nope.apk");
        Assert.False(ApkInspector.LooksLikeApk(missing));
    }

    [Fact]
    public void LooksLikeApk_RejectsAZipWithoutAManifest()
    {
        var fake = Path.Combine(_dir, "fake.apk");
        using (var zip = ZipFile.Open(fake, ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");

        Assert.False(ApkInspector.LooksLikeApk(fake));
    }

    [Fact]
    public void LooksLikeApk_AcceptsARealApk() =>
        Assert.True(ApkInspector.LooksLikeApk(CreateApk("com.example.ok")));

    [Fact]
    public void Inspect_ThrowsOnACorruptManifest()
    {
        var broken = Path.Combine(_dir, "broken.apk");
        using (var zip = ZipFile.Open(broken, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("AndroidManifest.xml");
            using var s = entry.Open();
            s.Write(new byte[] { 1, 2, 3 });
        }

        Assert.ThrowsAny<Exception>(() => ApkInspector.Inspect(broken));
    }

    [Fact]
    public void FallsBackToAReadableNameWhenTheLabelIsAResourceReference()
    {
        var metadata = ApkInspector.Inspect(CreateApk("com.example.calculator"));
        Assert.False(string.IsNullOrWhiteSpace(metadata.ApplicationLabel));
        Assert.DoesNotContain("@", metadata.ApplicationLabel);
    }

    // ---- test APK construction ---------------------------------------------------------

    private string CreateApk(
        string packageName,
        string versionName = "1.0",
        int versionCode = 1,
        int minSdk = 21,
        int targetSdk = 30,
        string[]? abis = null,
        bool usesGoogle = false)
    {
        var path = Path.Combine(_dir, packageName + ".apk");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var manifest = AxmlWriter.BuildManifest(packageName, versionName, versionCode, minSdk, targetSdk, usesGoogle);
        var entry = zip.CreateEntry("AndroidManifest.xml");
        using (var s = entry.Open()) s.Write(manifest);

        foreach (var abi in abis ?? Array.Empty<string>())
        {
            var lib = zip.CreateEntry($"lib/{abi}/libnative.so");
            using var ls = lib.Open();
            ls.Write(Encoding.UTF8.GetBytes("fake native library"));
        }

        var dex = zip.CreateEntry("classes.dex");
        using (var ds = dex.Open()) ds.Write(Encoding.UTF8.GetBytes("dex"));

        return path;
    }
}

/// <summary>Writes a minimal but format-correct AXML document for tests.</summary>
internal static class AxmlWriter
{
    private const uint AttrVersionCode = 0x0101021B;
    private const uint AttrVersionName = 0x0101021C;
    private const uint AttrMinSdk = 0x0101020C;
    private const uint AttrTargetSdk = 0x01010270;
    private const uint AttrName = 0x01010003;

    public static byte[] BuildManifest(
        string packageName, string versionName, int versionCode, int minSdk, int targetSdk, bool usesGoogle)
    {
        // String pool: indices matter because attribute names are looked up by index.
        var strings = new List<string>
        {
            "manifest",          // 0
            "package",           // 1
            "versionCode",       // 2
            "versionName",       // 3
            "uses-sdk",          // 4
            "minSdkVersion",     // 5
            "targetSdkVersion",  // 6
            "application",       // 7
            "uses-library",      // 8
            "name",              // 9
            packageName,         // 10
            versionName,         // 11
            "com.google.android.gms" // 12
        };

        // Resource map is parallel to the first N string-pool entries.
        var resourceIds = new uint[]
        {
            0, 0, AttrVersionCode, AttrVersionName, 0, AttrMinSdk, AttrTargetSdk, 0, 0, AttrName
        };

        var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            WriteStartElement(w, nameIndex: 0, new[]
            {
                Attr(nameIndex: 1, valueType: 0x03, data: 10, rawValue: 10),                 // package
                Attr(nameIndex: 2, valueType: 0x10, data: (uint)versionCode, rawValue: -1),  // versionCode
                Attr(nameIndex: 3, valueType: 0x03, data: 11, rawValue: 11)                  // versionName
            });

            WriteStartElement(w, nameIndex: 4, new[]
            {
                Attr(nameIndex: 5, valueType: 0x10, data: (uint)minSdk, rawValue: -1),
                Attr(nameIndex: 6, valueType: 0x10, data: (uint)targetSdk, rawValue: -1)
            });

            WriteStartElement(w, nameIndex: 7, Array.Empty<byte[]>());

            if (usesGoogle)
            {
                WriteStartElement(w, nameIndex: 8, new[]
                {
                    Attr(nameIndex: 9, valueType: 0x03, data: 12, rawValue: 12)
                });
            }
        }

        var pool = BuildStringPool(strings);
        var resMap = BuildResourceMap(resourceIds);
        var bodyBytes = body.ToArray();

        var total = 8 + pool.Length + resMap.Length + bodyBytes.Length;
        var output = new MemoryStream();
        using (var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0x0003);   // RES_XML_TYPE
            w.Write((ushort)8);        // header size
            w.Write((uint)total);
            w.Write(pool);
            w.Write(resMap);
            w.Write(bodyBytes);
        }
        return output.ToArray();
    }

    private static byte[] Attr(int nameIndex, byte valueType, uint data, int rawValue)
    {
        var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(-1);            // namespace
        w.Write(nameIndex);
        w.Write(rawValue);      // raw string value index
        w.Write((ushort)8);     // value size
        w.Write((byte)0);       // padding
        w.Write(valueType);
        w.Write(data);
        return ms.ToArray();
    }

    private static void WriteStartElement(BinaryWriter w, int nameIndex, IReadOnlyList<byte[]> attributes)
    {
        var attrBytes = attributes.SelectMany(a => a).ToArray();
        // chunk header (8) + line/comment (8) + element header (20) + attributes.
        // The element header is ns(4) name(4) attrStart(2) attrSize(2) count(2) id(2) class(2) style(2).
        var size = 8 + 8 + 20 + attrBytes.Length;

        w.Write((ushort)0x0102);      // RES_XML_START_ELEMENT_TYPE
        w.Write((ushort)16);          // header size
        w.Write((uint)size);
        w.Write(0);                   // line number
        w.Write(-1);                  // comment
        w.Write(-1);                  // namespace
        w.Write(nameIndex);
        w.Write((ushort)20);          // attribute start
        w.Write((ushort)20);          // attribute size
        w.Write((ushort)attributes.Count);
        w.Write((ushort)0);           // id index
        w.Write((ushort)0);           // class index
        w.Write((ushort)0);           // style index
        w.Write(attrBytes);
    }

    private static byte[] BuildStringPool(IReadOnlyList<string> strings)
    {
        var data = new MemoryStream();
        var offsets = new List<uint>();

        using (var w = new BinaryWriter(data, Encoding.Unicode, leaveOpen: true))
        {
            foreach (var s in strings)
            {
                offsets.Add((uint)data.Position);
                w.Write((ushort)s.Length);
                w.Write(Encoding.Unicode.GetBytes(s));
                w.Write((ushort)0); // null terminator
            }
        }

        var stringData = data.ToArray();
        var headerSize = 28;
        var offsetsSize = offsets.Count * 4;
        var total = headerSize + offsetsSize + stringData.Length;

        // Chunks must be 4-byte aligned.
        var padding = (4 - total % 4) % 4;
        total += padding;

        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0x0001);            // RES_STRING_POOL_TYPE
            w.Write((ushort)headerSize);
            w.Write((uint)total);
            w.Write(offsets.Count);
            w.Write(0);                          // style count
            w.Write(0u);                         // flags: UTF-16
            w.Write((uint)(headerSize + offsetsSize));
            w.Write(0u);                         // styles start
            foreach (var o in offsets) w.Write(o);
            w.Write(stringData);
            for (var i = 0; i < padding; i++) w.Write((byte)0);
        }
        return ms.ToArray();
    }

    private static byte[] BuildResourceMap(uint[] ids)
    {
        var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0x0180);          // RES_XML_RESOURCE_MAP_TYPE
        w.Write((ushort)8);
        w.Write((uint)(8 + ids.Length * 4));
        foreach (var id in ids) w.Write(id);
        return ms.ToArray();
    }
}
