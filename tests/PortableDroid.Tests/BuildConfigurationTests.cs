using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace PortableDroid.Tests;

/// <summary>
/// Regression tests for the startup crash where the main window never appeared and
/// logs/crash.log filled up with:
///
/// <code>
/// System.InvalidOperationException: Cannot find non-neutral culture related to 'en-us'
///    at System.Windows.Markup.XmlLanguage.GetSpecificCulture()
///    at System.Windows.Data.BindingExpressionBase.GetCulture()
///    ...
///    at System.Windows.Window.Show()
///    at PortableDroid.App.App.OnStartup(StartupEventArgs e)
/// </code>
///
/// Root cause: Directory.Build.props had <c>&lt;InvariantGlobalization&gt;true&lt;/InvariantGlobalization&gt;</c>.
/// WPF calls <c>XmlLanguage.GetSpecificCulture()</c> for every data binding, and under invariant
/// globalization .NET cannot map 'en-us' to a non-neutral culture, so every binding threw and the
/// window died before it rendered. The app logic was fine; only the build setting was wrong.
///
/// These tests are deliberately cheap and have no dependency on WPF so they run on the plain
/// xunit host. Together they guard the setting itself and the runtime behaviour it controls.
/// </summary>
public class BuildConfigurationTests
{
    /// <summary>
    /// Guards the build setting directly. If somebody flips InvariantGlobalization back to true
    /// (for example to shave a few MB off the self-contained publish), this fails with a clear
    /// message instead of shipping an exe that crashes for every user.
    /// </summary>
    [Fact]
    public void InvariantGlobalizationIsNotEnabled()
    {
        var repoRoot = FindRepositoryRoot();
        var propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), $"Directory.Build.props not found at '{propsPath}'.");

        var doc = XDocument.Load(propsPath);

        // Directory.Build.props has no XML namespace, but be tolerant of one being added later.
        var values = doc.Descendants()
            .Where(e => e.Name.LocalName == "InvariantGlobalization")
            .Select(e => e.Value.Trim())
            .ToList();

        foreach (var value in values)
        {
            Assert.False(
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
                "Directory.Build.props sets <InvariantGlobalization>true</InvariantGlobalization>. " +
                "That breaks WPF data binding (XmlLanguage.GetSpecificCulture cannot resolve 'en-us') " +
                "and crashes the UI on startup. It must be false or absent.");
        }
    }

    /// <summary>
    /// The exact runtime operation WPF performs for each binding: resolve a *specific* (non-neutral)
    /// culture. Under invariant globalization "en-US" is silently replaced by the invariant culture
    /// and CreateSpecificCulture("en") cannot produce anything WPF accepts.
    /// </summary>
    [Fact]
    public void SpecificCulturesResolveAsWpfRequires()
    {
        var enUs = CultureInfo.GetCultureInfo("en-US");
        Assert.False(enUs.IsNeutralCulture, "'en-US' must resolve to a specific (non-neutral) culture.");
        Assert.NotEqual(CultureInfo.InvariantCulture, enUs);

        // XmlLanguage.GetSpecificCulture() ends up here for neutral xml:lang values such as "en".
        var specificFromNeutral = CultureInfo.CreateSpecificCulture("en");
        Assert.False(specificFromNeutral.IsNeutralCulture);
        Assert.NotEqual(CultureInfo.InvariantCulture, specificFromNeutral);
        Assert.False(string.IsNullOrEmpty(specificFromNeutral.Name));
    }

    /// <summary>
    /// Under invariant mode all cultures silently collapse to the invariant culture: depending on
    /// the PredefinedCulturesOnly switch a lookup either throws CultureNotFoundException or hands
    /// back a culture that carries the requested name but nothing except invariant data behind it
    /// (ISO language "iv", no real formatting rules). A naive "GetCultureInfo does not throw" test
    /// could therefore pass while the app is still broken. Asserting the round-tripped name *and*
    /// a piece of real culture data catches the regression in every configuration.
    /// </summary>
    [Fact]
    public void CultureNamesRoundTripInsteadOfCollapsingToInvariant()
    {
        var frFr = CultureInfo.GetCultureInfo("fr-FR");
        Assert.Equal("fr-FR", frFr.Name);
        Assert.NotEqual(CultureInfo.InvariantCulture, frFr);
        Assert.Equal("fr", frFr.TwoLetterISOLanguageName); // "iv" when backed by invariant data
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PortableDroid.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
