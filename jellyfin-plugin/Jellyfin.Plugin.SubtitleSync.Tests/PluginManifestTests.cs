using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.SubtitleSync.Configuration;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests;

/// <summary>
/// Guards the parts of the plugin shell that are contracts rather than
/// behaviour: the identity the server keys on, and the resource it loads.
/// </summary>
public class PluginManifestTests
{
    private const string ConfigPageResource =
        "Jellyfin.Plugin.SubtitleSync.Configuration.configPage.html";

    private const string BundleResource =
        "Jellyfin.Plugin.SubtitleSync.Web.subtitleSync.js";

    private const string SyncPageResource =
        "Jellyfin.Plugin.SubtitleSync.Configuration.syncPage.html";

    private const string PageBundleResource =
        "Jellyfin.Plugin.SubtitleSync.Web.subtitleSyncPage.js";

    /// <summary>
    /// The GUID is the shared key between the server, the repository manifest and
    /// every installed copy. Changing it orphans existing installs.
    /// </summary>
    [Fact]
    public void PluginIdIsTheStableGeneratedGuid()
    {
        Assert.Equal(
            Guid.Parse("96d55013-3cf0-465e-9036-7fb73dd47f71"),
            UninitializedPlugin().Id);
    }

    /// <summary>
    /// The display name appears in Dashboard &gt; Plugins and is the key the
    /// Playwright smoke tests and the config-page route look it up by.
    /// </summary>
    [Fact]
    public void PluginNameIsSubtitleSync()
    {
        Assert.Equal("Subtitle Sync", UninitializedPlugin().Name);
    }

    /// <summary>
    /// <c>GetPages</c> hands the server a manifest resource name as a plain
    /// string, so a renamed file or moved folder fails only at runtime unless the
    /// two are asserted against each other.
    /// </summary>
    [Fact]
    public void ConfigPageIsEmbeddedUnderTheNameGetPagesAdvertises()
    {
        Assert.Contains(
            ConfigPageResource,
            typeof(Plugin).Assembly.GetManifestResourceNames());

        var page = Assert.Single(
            UninitializedPlugin().GetPages(),
            p => p.Name == "Subtitle Sync");

        Assert.Equal(ConfigPageResource, page.EmbeddedResourcePath);
    }

    /// <summary>
    /// The browser bundle of <c>lib/</c> is served through the same
    /// <c>GetPages</c> mechanism as the config page, under a <c>.js</c> name so
    /// the server infers a JavaScript content type. If the csproj's LogicalName
    /// and this path ever disagree the page 404s at runtime and nowhere else.
    /// </summary>
    [Fact]
    public void WebBundleIsEmbeddedUnderTheNameGetPagesAdvertises()
    {
        Assert.Contains(
            BundleResource,
            typeof(Plugin).Assembly.GetManifestResourceNames());

        var page = Assert.Single(
            UninitializedPlugin().GetPages(),
            p => p.Name == Plugin.BundlePageName);

        Assert.Equal(BundleResource, page.EmbeddedResourcePath);
        Assert.EndsWith(".js", page.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sync page itself (#12). Reached at
    /// <c>/web/#/configurationpage?name=SubtitleSyncPage</c>, so the registered
    /// name is part of every link to it - the Dashboard button, the injected
    /// Subtitles-menu item (#13) and the Playwright specs.
    /// </summary>
    [Fact]
    public void SyncPageIsEmbeddedUnderTheNameGetPagesAdvertises()
    {
        Assert.Contains(
            SyncPageResource,
            typeof(Plugin).Assembly.GetManifestResourceNames());

        var page = Assert.Single(
            UninitializedPlugin().GetPages(),
            p => p.Name == Plugin.SyncPageName);

        Assert.Equal(SyncPageResource, page.EmbeddedResourcePath);
    }

    /// <summary>
    /// The sync page's UI bundle, served under a <c>.js</c> name for the same
    /// content-type reason as the shared bundle.
    /// </summary>
    [Fact]
    public void PageBundleIsEmbeddedUnderTheNameGetPagesAdvertises()
    {
        Assert.Contains(
            PageBundleResource,
            typeof(Plugin).Assembly.GetManifestResourceNames());

        var page = Assert.Single(
            UninitializedPlugin().GetPages(),
            p => p.Name == Plugin.PageBundlePageName);

        Assert.Equal(PageBundleResource, page.EmbeddedResourcePath);
        Assert.EndsWith(".js", page.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The four registered pages, and no fifth one nobody remembered to embed.
    /// </summary>
    /// <remarks>
    /// Every name here is a URL somewhere: two are <c>?name=</c> values in
    /// links, two are script sources the sync page's bootstrap fetches. A
    /// rename is therefore a breaking change to a link, not an internal detail.
    /// </remarks>
    [Fact]
    public void GetPagesRegistersExactlyTheFourKnownPages()
    {
        var names = UninitializedPlugin().GetPages().Select(p => p.Name).ToArray();

        Assert.Equal(
            new[] { "Subtitle Sync", "SubtitleSyncPage", "subtitleSync.js", "subtitleSyncPage.js" },
            names.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The bootstrap in the sync page loads the two script resources by name.
    /// Those strings live in HTML, where no compiler checks them.
    /// </summary>
    [Fact]
    public void SyncPageLoadsTheScriptNamesGetPagesRegisters()
    {
        var html = ReadResource(SyncPageResource);

        Assert.Contains("loadScript('" + Plugin.BundlePageName + "')", html, StringComparison.Ordinal);
        Assert.Contains("loadScript('" + Plugin.PageBundlePageName + "')", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Dashboard route is the primary way in, so the configuration page has
    /// to link to the sync page by the name it is actually registered under.
    /// </summary>
    [Fact]
    public void ConfigPageLinksToTheSyncPage()
    {
        Assert.Contains(
            "#/configurationpage?name=" + Plugin.SyncPageName,
            ReadResource(ConfigPageResource),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The client runs a plugin page through <c>translateHtml</c> before
    /// injecting it, which expands <c>${...}</c>. A JavaScript template literal
    /// in the markup is therefore silently mangled, and the failure shows up as
    /// a page that does nothing rather than as an error.
    /// </summary>
    [Fact]
    public void SyncPageMarkupContainsNoTemplateLiteralPlaceholders()
    {
        Assert.DoesNotContain("${", ReadResource(SyncPageResource), StringComparison.Ordinal);
    }

    /// <summary>
    /// A bundle that failed to build but still embedded would be a zero-byte
    /// resource and a page that loads but does nothing. It carries a minified
    /// copy of lib/ plus ~27 KB of base64 libfvad, so it cannot be small.
    /// </summary>
    [Fact]
    public void WebBundleIsNotTrivial()
    {
        using var stream = typeof(Plugin).Assembly
            .GetManifestResourceStream(BundleResource);

        Assert.NotNull(stream);
        Assert.True(
            stream!.Length > 40_000,
            $"embedded bundle is only {stream.Length} bytes");
    }

    /// <summary>
    /// Overwriting a user's subtitle file is destructive and has no undo, so it
    /// has to stay opt-in.
    /// </summary>
    [Fact]
    public void OverwriteOriginalDefaultsToFalse()
    {
        Assert.False(new PluginConfiguration().OverwriteOriginal);
    }

    /// <summary>
    /// Builds a <see cref="Plugin"/> without running its constructor.
    /// </summary>
    /// <remarks>
    /// The real constructor needs the server's DI container. Every member touched
    /// here is constant, so an uninitialized instance is enough and keeps this
    /// project free of a mocking framework.
    /// </remarks>
    private static Plugin UninitializedPlugin()
        => (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

    /// <summary>
    /// Reads an embedded resource as text.
    /// </summary>
    /// <param name="name">The manifest resource name.</param>
    /// <returns>Its contents.</returns>
    private static string ReadResource(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
