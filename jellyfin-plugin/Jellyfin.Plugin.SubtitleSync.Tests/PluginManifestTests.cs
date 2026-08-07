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

    /// <summary>
    /// The GUID is the shared key between the server, the repository manifest and
    /// every installed copy. Changing it orphans existing installs.
    /// </summary>
    [Fact]
    public void PluginIdIsTheStableGeneratedGuid()
    {
        Assert.Equal(
            Guid.Parse("e981c765-b769-44a1-a4fe-805df5bb6d6b"),
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

        var pages = UninitializedPlugin().GetPages().ToList();

        Assert.Single(pages);
        Assert.Equal(ConfigPageResource, pages[0].EmbeddedResourcePath);
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
}
