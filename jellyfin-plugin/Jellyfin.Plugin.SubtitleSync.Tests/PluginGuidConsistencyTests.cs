using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests;

/// <summary>
/// Asserts that every hand-maintained copy of the plugin GUID agrees with
/// <see cref="Plugin.Id"/>.
/// </summary>
/// <remarks>
/// <para>
/// The GUID is the key the server matches an installed plugin against, so the
/// copies have to agree, but nothing makes them. The failure is silent and
/// splits by file: a wrong <c>configPage.html</c> means the config page loads
/// and then fails to read or save settings; a wrong <c>manifest.json</c> means
/// the plugin installs but is never offered an update, because the server sees
/// the installed copy and the catalogue entry as different plugins.
/// </para>
/// <para>
/// This became a real problem once: the GUID was edited in
/// <c>Plugin.cs</c> alone and the value was one character too long, which
/// threw from <c>Plugin.get_Id()</c> at runtime and took out
/// <c>GET /Plugins</c> entirely. A test is cheaper than rediscovering that.
/// </para>
/// <para>
/// Reading the repository's own files from a unit test is unusual, and the
/// alternative is templating four files from one source at build time. That is
/// the better design, but it means generating <c>configPage.html</c> and the
/// manifest, which is a lot of machinery for one constant.
/// </para>
/// </remarks>
public class PluginGuidConsistencyTests
{
    /// <summary>
    /// Files carrying a copy of the GUID, relative to the repository root, with
    /// what each one breaks if it drifts.
    /// </summary>
    private static readonly (string Path, string Breaks)[] Copies =
    [
        ("jellyfin-plugin/Jellyfin.Plugin.SubtitleSync/Configuration/configPage.html",
            "the config page cannot read or save its settings"),
        ("jellyfin-plugin/README.md",
            "the documented GUID is wrong"),
        (".github/workflows/release.yml",
            "released zips carry a meta.json the server will not match"),
        ("public/jellyfin/manifest.json",
            "the plugin installs but is never offered an update"),
    ];

    [Fact]
    public void EveryCopyOfTheGuidMatchesPluginId()
    {
        var expected = UninitializedPlugin().Id.ToString();
        var root = RepositoryRoot();
        var wrong = new List<string>();

        foreach (var (path, breaks) in Copies)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"{path} does not exist");

            if (!File.ReadAllText(full).Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                wrong.Add($"  {path} - if this is stale, {breaks}");
            }
        }

        Assert.True(
            wrong.Count == 0,
            $"Plugin.Id is {expected} but these files do not contain it:{Environment.NewLine}"
            + string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// Walks up from the test assembly until it finds the repository root.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            // package.json plus the plugin folder: distinctive enough that a
            // parent directory cannot be mistaken for the root.
            if (File.Exists(Path.Combine(dir.FullName, "package.json"))
                && Directory.Exists(Path.Combine(dir.FullName, "jellyfin-plugin")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"could not find the repository root above {AppContext.BaseDirectory}");
    }

    private static Plugin UninitializedPlugin()
        => (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
}
