# Jellyfin 10.11 plugin API - verified against primary sources

Research for issue #2 (Stage 0 of epic #18).

**Method.** Every claim below was read from source at tag `v10.11.11` of `github.com/jellyfin/jellyfin`
and `github.com/jellyfin/jellyfin-web`, from the live main branch of 10.11-targeting plugins, from the
NuGet flat-container index, or from the live production plugin manifests. Nothing is from memory.
Where a claim could not be verified it is flagged under **Unconfirmed**.

**Version landscape as of this research.** The 10.11 line runs `v10.11.0` .. `v10.11.11` (no `v10.11.1`;
the sequence skips it). The release branch is `release-10.11.z`. `master` is now `v12.0-rc4`, so
anything read from a GitHub default branch or a code search without an explicit `?ref=` is 12.0, not
10.11. This bit us once during research and will bite anyone who repeats it.

---

## Executive summary of the surprises

1. **The `dependencies` array in a repository manifest is inert in 10.11.** `VersionInfo` has no such
   property. There is no dependency resolution for plugins. See section 13.
2. **`targetAbi` is a minimum, not an exact match.** The installer keeps versions where
   `targetAbi <= serverVersion`. See section 13.
3. **There is no supported extension point for adding UI to the item detail page in 10.11**, and there
   is no server-driven client-plugin loader either. Confirmed by reading `pluginManager.js`. See
   section 12. The epic's assumption holds.
4. **File Transformation works by Harmony-patching `Jellyfin.Server.Startup.Configure`** and
   re-implementing that entire method verbatim. That is far more invasive than "intercepts static
   files", and it is why the plugin is pinned per Jellyfin patch release. See section 11.
5. **The item detail page in 10.11 is still the legacy `src/controllers/itemDetails/index.js`**, hosted
   as a legacy route inside the React "experimental" app shell. The Subtitles entry we want lives in
   `src/components/itemContextMenu.js`. See section 12.
6. **`config.json` in the web root is fetched at runtime with `cache: 'no-store'`** and carries a
   `menuLinks` array. That is a far more robust File Transformation target than a minified JS chunk.
   See section 12.
7. **jellyfin.org no longer has a plugin development documentation page.** `docs/general/contributing/`
   contains no plugin doc, and `https://jellyfin.org/docs/general/contributing/development/plugins/`
   returns 404. The plugin template README plus real plugin source are the only official references.
8. **The plugin template repo is not a valid 10.11 starting point.** See section 1.

---

## 1. Target framework and NuGet packages

**TFM: `net9.0`.**

Confirmed from the server itself at v10.11.11 -
[`Jellyfin.Server/Jellyfin.Server.csproj`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Jellyfin.Server/Jellyfin.Server.csproj)
has `<TargetFramework>net9.0</TargetFramework>`.

Corroborated by 10.11-targeting plugins: Bookshelf's `build.yaml` declares `targetAbi: "10.11.0.0"` /
`framework: "net9.0"`; OpenSubtitles declares `targetAbi: "10.11.8.0"` / `framework: "net9.0"`;
intro-skipper's `build.json` is `{"version": "10.11", "dotnet-version": "9.0.x"}`.

**Package versions.** No rename, no restructure of the two packages we care about.
`Jellyfin.Controller` and `Jellyfin.Model` both publish `10.11.11` as the latest 10.11 patch (verified
against `https://api.nuget.org/v3-flatcontainer/jellyfin.controller/index.json` and the equivalent for
`jellyfin.model`).

New in 10.11: `Jellyfin.Database.Implementations` is a brand-new package whose first published version
is `10.11.0-rc1`. Entity and enum types moved there (intro-skipper now imports
`using Jellyfin.Database.Implementations.Enums;`). `Jellyfin.Data` still exists at `10.11.11`. A plain
API/metadata plugin like ours does not need the new database package.

Real 10.11 csproj -
[intro-skipper/IntroSkipper.csproj](https://github.com/intro-skipper/intro-skipper/blob/master/IntroSkipper/IntroSkipper.csproj):

```xml
<PropertyGroup>
  <TargetFramework>net9.0</TargetFramework>
  <RootNamespace>IntroSkipper</RootNamespace>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <Nullable>enable</Nullable>
  <AnalysisMode>AllEnabledByDefault</AnalysisMode>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Jellyfin.Controller" Version="10.11.*-*" />
  <PackageReference Include="Jellyfin.Model" Version="10.11.*-*" />
  <PackageReference Include="StyleCop.Analyzers.Unstable" Version="1.2.0.556" PrivateAssets="All" />
</ItemGroup>
```

The File Transformation plugin pins explicitly instead
([csproj](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2.5.11.0/src/Jellyfin.Plugin.FileTransformation/Jellyfin.Plugin.FileTransformation.csproj)):

```xml
<TargetFramework Condition="$(JellyfinVersion.StartsWith('10.11'))">net9.0</TargetFramework>
<PackageReference Include="Jellyfin.Model" Version="$(JellyfinNugetVersion)" />
<PackageReference Include="Jellyfin.Controller" Version="$(JellyfinNugetVersion)" />
```

**Recommendation for us:** pin `Jellyfin.Controller` and `Jellyfin.Model` to an exact `10.11.11` for
reproducible builds, keep `<ExcludeAssets>runtime</ExcludeAssets>` on those references (this stops the
server assemblies being copied into the plugin output - the template still does this and it is still
correct), and set `targetAbi` to `10.11.0.0`.

**Do not start from the plugin template.** `jellyfin/jellyfin-plugin-template@master` is still on
`Jellyfin.Controller` **10.9.11**, and its `unstable` branch has already jumped to `net10.0` +
`Jellyfin.Controller 12.*-*`. There is no 10.11 branch. Copy
[`jellyfin-plugin-bookshelf`](https://github.com/jellyfin/jellyfin-plugin-bookshelf) or
[`jellyfin-plugin-opensubtitles`](https://github.com/jellyfin/jellyfin-plugin-opensubtitles) instead.

### Other project-setup notes for 10.11

- The server's root `Directory.Build.props` at v10.11.11 sets `<Nullable>enable</Nullable>` and
  `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` with
  `<WarningsNotAsErrors>NU1902;NU1903</WarningsNotAsErrors>`. Every 10.11 plugin csproj read sets
  `Nullable`, `TreatWarningsAsErrors`, `GenerateDocumentationFile` and `AnalysisMode`. With
  `GenerateDocumentationFile` plus warnings-as-errors you must XML-document every public member.
- `jellyfin.ruleset` is now optional and inconsistently used. Template and Bookshelf still set
  `<CodeAnalysisRuleSet>../jellyfin.ruleset</CodeAnalysisRuleSet>`; OpenSubtitles and intro-skipper
  have dropped it in favour of `.editorconfig` plus `AnalysisMode`.
- StyleCop package choice is not standardised across 10.11 plugins:
  `StyleCop.Analyzers 1.2.0-beta.556`, `StyleCop.Analyzers 1.1.118` and
  `StyleCop.Analyzers.Unstable 1.2.0.556` are all in use.
- 10.11-era plugin code uses C# 13: primary constructors on controllers, collection expressions
  (`return [ ... ]` from `GetPages`), file-scoped namespaces. `System.Threading.Lock` appears in
  `BasePluginOfT` itself.
- New first-party analyzer project `src/Jellyfin.CodeAnalysis` exists in the server repo, wired in via
  `Directory.Build.props` as an `OutputItemType="Analyzer"` project reference (Debug only). It is
  server-internal and not shipped to plugins.

---

## 2. `BasePlugin<TConfiguration>` and `IHasWebPages`

Namespaces, all verified at v10.11.11:

- `MediaBrowser.Common.Plugins.BasePlugin`, `MediaBrowser.Common.Plugins.BasePlugin<TConfigurationType>`
- `MediaBrowser.Model.Plugins.IHasWebPages`, `MediaBrowser.Model.Plugins.PluginPageInfo`,
  `MediaBrowser.Model.Plugins.BasePluginConfiguration`
- `MediaBrowser.Common.Configuration.IApplicationPaths`
- `MediaBrowser.Model.Serialization.IXmlSerializer`

The base constructor is unchanged in 10.11 - still `(IApplicationPaths, IXmlSerializer)`.
[`MediaBrowser.Common/Plugins/BasePluginOfT.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Common/Plugins/BasePluginOfT.cs):

```csharp
public abstract class BasePlugin<TConfigurationType> : BasePlugin, IHasPluginConfiguration
    where TConfigurationType : BasePluginConfiguration
{
    protected BasePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
    {
        ApplicationPaths = applicationPaths;
        XmlSerializer = xmlSerializer;
        ...
        var dataFolderPath = Path.Combine(ApplicationPaths.PluginsPath, Path.GetFileNameWithoutExtension(assemblyFilePath));
```

Your plugin's own constructor is resolved from the DI container, so you may inject additional services
and forward the first two to `base(...)`. intro-skipper's real 10.11 `Plugin.cs`
([source](https://github.com/intro-skipper/intro-skipper/blob/master/IntroSkipper/Plugin.cs)):

```csharp
public Plugin(
    IApplicationPaths applicationPaths,
    IXmlSerializer xmlSerializer,
    IServerConfigurationManager serverConfiguration,
    ILibraryManager libraryManager,
    IChapterManager chapterRepository,
    IPluginManager pluginManager,
    ILogger<Plugin> logger)
    : base(applicationPaths, xmlSerializer)
```

`Name` is `public abstract string Name { get; }` on `BasePlugin` - you must override it. `Id` is
`public virtual Guid Id { get; private set; }`; the normal pattern is an expression-bodied override.
Alternatively `BasePlugin<T>`'s constructor picks up an assembly-level `[assembly: Guid("...")]` via
`SetId`. Do not do both with different values.

### `IHasWebPages` and `PluginPageInfo`

[`MediaBrowser.Model/Plugins/IHasWebPages.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/Plugins/IHasWebPages.cs):

```csharp
namespace MediaBrowser.Model.Plugins
{
    public interface IHasWebPages
    {
        IEnumerable<PluginPageInfo> GetPages();
    }
}
```

[`MediaBrowser.Model/Plugins/PluginPageInfo.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/Plugins/PluginPageInfo.cs)
(six members, verbatim minus XML doc comments):

```csharp
namespace MediaBrowser.Model.Plugins
{
    public class PluginPageInfo
    {
        public string Name { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string EmbeddedResourcePath { get; set; } = string.Empty;
        public bool EnableInMainMenu { get; set; }
        public string? MenuSection { get; set; }
        public string? MenuIcon { get; set; }
    }
}
```

A complete real 10.11 implementation that returns both a config page and shows in the main menu -
[File Transformation `FileTransformationPlugin.cs`](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2.5.11.0/src/Jellyfin.Plugin.FileTransformation/FileTransformationPlugin.cs):

```csharp
public class FileTransformationPlugin : BasePlugin<PluginConfiguration>, IHasPluginConfiguration, IHasWebPages
{
    public static FileTransformationPlugin Instance { get; private set; } = null!;

    public override Guid Id => Guid.Parse("5e87cc92-571a-4d8d-8d98-d2d4147f9f90");

    public override string Name => "File Transformation";

    public FileTransformationPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer,
        IServiceProvider serviceProvider, IWebFileTransformationWriteService writeService)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        ...
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        string? prefix = GetType().Namespace;

        yield return new PluginPageInfo
        {
            Name = Name,
            EnableInMainMenu = true,
            EmbeddedResourcePath = $"{prefix}.Configuration.config.html"
        };
    }
}
```

`EmbeddedResourcePath` is the default MSBuild manifest resource name:
`<RootNamespace>.<folder path with dots>.<filename>`. It must be paired with an `EmbeddedResource`
item in the csproj:

```xml
<None Remove="Configuration\configPage.html" />
<EmbeddedResource Include="Configuration\configPage.html" />
```

OpenSubtitles ships two resources this way (`Web\opensubtitles.html`, `Web\opensubtitles.js`) - a `.js`
sibling registered as its own `PluginPageInfo` is how you serve scripts alongside a config page. **This
is the mechanism our embedded esbuild bundle (issue #10) will use.**

---

## 3. Registering API controllers

**Controllers are auto-discovered. No registration call is needed.** The server clears MVC's
application parts and then explicitly adds every loaded plugin assembly.
[`Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs):

```csharp
// Clear app parts to avoid other assemblies being picked up
.ConfigureApplicationPartManager(a => a.ApplicationParts.Clear())
.AddApplicationPart(typeof(StartupController).Assembly)
...
foreach (Assembly pluginAssembly in pluginAssemblies)
{
    mvcBuilder.AddApplicationPart(pluginAssembly);
}

return mvcBuilder.AddControllersAsServices();
```

Two consequences:

1. Subclass `ControllerBase`, add `[ApiController]` and a `[Route]`, and it works.
2. Because of `AddControllersAsServices()`, controllers are resolved from the DI container, so any
   constructor-injected plugin service **must** be registered in `IPluginServiceRegistrator` or
   resolution fails at request time.

**Route convention: a literal top-level segment. No `api/` prefix.** Real 10.11 example -
[intro-skipper `SegmentEditorController.cs`](https://github.com/intro-skipper/intro-skipper/blob/master/IntroSkipper/Controllers/SegmentEditorController.cs):

```csharp
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Route("MediaSegmentsApi")]
public class SegmentEditorController(MediaSegmentEditorService mediaSegmentEditorService) : ControllerBase
```

File Transformation uses the token form
([source](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2.5.11.0/src/Jellyfin.Plugin.FileTransformation/Controller/FileTransformationController.cs)):

```csharp
[Route("[controller]")]
public class FileTransformationController : ControllerBase
{
    [HttpPost("RegisterTransformation")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult RegisterTransformation([FromBody] TransformationRegistrationPayload payload,
        [FromServices] IWebFileTransformationWriteService writeService)
```

The routes in epic #18 (`/SubtitleSync/Item/{id}` etc.) are therefore achievable verbatim with
`[Route("SubtitleSync")]`. The top-level namespace is shared with core routes, so `SubtitleSync` is a
good distinctive choice.

### `IPluginServiceRegistrator`

`MediaBrowser.Controller.Plugins.IPluginServiceRegistrator`, shipped in `Jellyfin.Controller`. Full
definition at
[v10.11.11](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Plugins/IPluginServiceRegistrator.cs):

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace MediaBrowser.Controller.Plugins;

/// <remarks>
/// This interface is only used for service registration and requires a parameterless constructor.
/// </remarks>
public interface IPluginServiceRegistrator
{
    void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost);
}
```

**It did not change in 10.11.** The same two-argument signature is present at `v10.10.7` and
`v10.9.11`. The file does not exist at that path at `v10.8.13`, so `IServerApplicationHost` landed in
the 10.9 cycle.

The implementing class needs a public parameterless constructor; the server instantiates it via
`Activator.CreateInstance`
([`PluginManager.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/Plugins/PluginManager.cs)):

```csharp
var instance = (IPluginServiceRegistrator?)Activator.CreateInstance(pluginServiceRegistrator);
instance?.RegisterServices(serviceCollection, _appHost);
```

Real 10.11 implementation -
[OpenSubtitles](https://github.com/jellyfin/jellyfin-plugin-opensubtitles/blob/master/Jellyfin.Plugin.OpenSubtitles/PluginServiceRegistrator.cs):

```csharp
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(nameof(OpenSubtitles), c => { ... });
        serviceCollection.AddSingleton<ISubtitleProvider, OpenSubtitleDownloader>();
    }
}
```

intro-skipper shows the fuller range: `AddHostedService<Entrypoint>()` for background work,
`AddTransient<T>()` for services its controllers inject, and `serviceCollection.Configure<MvcOptions>(...)`
to add an MVC convention.

---

## 4. Authorization policies

- Class: `Policies` (static)
- Namespace: **`MediaBrowser.Common.Api`** (assembly `Jellyfin.Common`, available transitively via
  `Jellyfin.Controller`)
- Constant: `public const string RequiresElevation = "RequiresElevation";`
- Source: [`MediaBrowser.Common/Api/Policies.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Common/Api/Policies.cs)

```csharp
namespace MediaBrowser.Common.Api;

public static class Policies
{
    public const string FirstTimeSetupOrElevated = "FirstTimeSetupOrElevated";
    public const string RequiresElevation = "RequiresElevation";
    public const string LocalAccessOnly = "LocalAccessOnly";
    public const string IgnoreParentalControl = "IgnoreParentalControl";
    public const string Download = "Download";
    public const string FirstTimeSetupOrDefault = "FirstTimeSetupOrDefault";
    public const string LocalAccessOrRequiresElevation = "LocalAccessOrRequiresElevation";
    public const string AnonymousLanAccessPolicy = "AnonymousLanAccessPolicy";
    public const string FirstTimeSetupOrIgnoreParentalControl = "FirstTimeSetupOrIgnoreParentalControl";
    public const string SyncPlayHasAccess = "SyncPlayHasAccess";
    public const string SyncPlayCreateGroup = "SyncPlayCreateGroup";
    public const string SyncPlayJoinGroup = "SyncPlayJoinGroup";
    public const string SyncPlayIsInGroup = "SyncPlayIsInGroup";
    public const string CollectionManagement = "CollectionManagement";
    public const string LiveTvAccess = "LiveTvAccess";
    public const string LiveTvManagement = "LiveTvManagement";
    public const string SubtitleManagement = "SubtitleManagement";
    public const string LyricManagement = "LyricManagement";
}
```

Applied as `[Authorize(Policy = Policies.RequiresElevation)]` with `using MediaBrowser.Common.Api;` and
`using Microsoft.AspNetCore.Authorization;`.

**The old location is dead.** Probing tags:

| tag | `Jellyfin.Api/Constants/Policies.cs` | `MediaBrowser.Common/Api/Policies.cs` |
| --- | --- | --- |
| v10.8.13 | present | absent |
| v10.9.11 | absent | present |
| v10.10.7 | absent | present |
| v10.11.11 | absent | present |

**Note for epic #18.** `Policies.SubtitleManagement` exists in 10.11 and is exactly the permission
Jellyfin itself uses to gate the "Edit subtitles" affordance (see section 12 -
`itemHelper.canEditSubtitles`). The epic says "admin only". `RequiresElevation` is defensible for the
ffmpeg and file-write endpoints, but if we want the injected menu item to be visible to the same set of
users Jellyfin already shows "Edit subtitles" to, `SubtitleManagement` is the matching policy. Worth an
explicit decision rather than a default.

---

## 5. Resolving an item, its media path, media sources and subtitle streams

`ILibraryManager` - namespace `MediaBrowser.Controller.Library`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Library/ILibraryManager.cs)).
Item lookup is **synchronous only**; there is no async variant in 10.11.

```csharp
BaseItem? GetItemById(Guid id);

T? GetItemById<T>(Guid id)
    where T : BaseItem;

public T? GetItemById<T>(Guid id, Guid userId)
    where T : BaseItem;

public T? GetItemById<T>(Guid id, User? user)
    where T : BaseItem;
```

The user-validating overloads are what the core API controllers use, e.g.
`_libraryManager.GetItemById<Video>(itemId, User.GetUserId())` in `SubtitleController`.

**Media file path** - `MediaBrowser.Controller.Entities.BaseItem`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Entities/BaseItem.cs)):
`public virtual string Path { get; set; }` (line 258), plus `ContainingFolderPath` (line 279) and
`FileNameWithoutExtension` (line 375, `System.IO.Path.GetFileNameWithoutExtension(Path)`).
`FileNameWithoutExtension` is exactly the prefix the external-subtitle scanner matches on, so use it
when naming our sibling `.srt` (issue #4).

**`IMediaSourceManager`** - namespace `MediaBrowser.Controller.Library`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Library/IMediaSourceManager.cs)):

```csharp
IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId);
IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query);

IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User user = null);

Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId,
    bool enablePathSubstitution, CancellationToken cancellationToken);

Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user,
    bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken);
```

`GetStaticMediaSources(item, false)` is what `SubtitleController` itself uses; take `.MediaStreams` off
the returned `MediaSourceInfo`. For a plain file item the `MediaSourceInfo.Id` is the item id in `"N"`
format (`itemId.ToString("N", CultureInfo.InvariantCulture)`).

`MediaStreamQuery` - namespace `MediaBrowser.Controller.Persistence`:

```csharp
public class MediaStreamQuery
{
    public MediaStreamType? Type { get; set; }
    public int? Index { get; set; }
    public Guid ItemId { get; set; }
}
```

`BaseItem.GetMediaStreams()` (line 1070) and `BaseItem.GetMediaSources(bool enablePathSubstitution)`
(line 1083) are convenience wrappers over the static `BaseItem.MediaSourceManager` the host wires up.
Injecting `IMediaSourceManager` is cleaner.

**`MediaStream`** - namespace `MediaBrowser.Model.Entities`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/Entities/MediaStream.cs)).
Fields that matter:

| Member | Declaration | Line |
| --- | --- | --- |
| `Index` | `public int Index { get; set; }` | 607 |
| `Type` | `public MediaStreamType Type { get; set; }` | 595 |
| `Codec` | `public string Codec { get; set; }` | 41 |
| `Language` | `public string Language { get; set; }` | 53 |
| `Title` | `public string Title { get; set; }` | 155 |
| `IsExternal` | `public bool IsExternal { get; set; }` | 619 |
| `Path` | `public string Path { get; set; }` | 694 |
| `IsDefault` | `public bool IsDefault { get; set; }` | 530 |
| `IsForced` | `public bool IsForced { get; set; }` | 536 |
| `IsHearingImpaired` | `public bool IsHearingImpaired { get; set; }` | 542 |

`IsHearingImpaired` **does exist in 10.11** and is non-nullable `bool` (the `DisplayTitle` getter still
writes `if (IsHearingImpaired == true)`, a leftover from when it was nullable). Filter on
`MediaStreamType.Subtitle`.

---

## 6. Running the server's ffmpeg

`IMediaEncoder` - namespace `MediaBrowser.Controller.MediaEncoding`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/MediaEncoding/IMediaEncoder.cs)):

```csharp
public interface IMediaEncoder : ITranscoderSupport
{
    /// <summary>Gets the encoder path.</summary>
    string EncoderPath { get; }

    /// <summary>Gets the probe path.</summary>
    string ProbePath { get; }

    Version EncoderVersion { get; }
    ...
}
```

Registered as a singleton, so injectable
([`ApplicationHost.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/ApplicationHost.cs) line 518).

**`EncoderLocationType` is gone in 10.11.** A recursive tree scan at `v10.11.11` finds no
`EncoderLocationType` and no `FFmpegLocation` file. Any 10.8-era sample referencing it is dead code.

**There is no public "run ffmpeg" helper.** `IMediaEncoder` exposes only argument builders
(`GetInputArgument`, `GetInputPathArgument`, `GetExternalSubtitleInputArgument`, `GetTimeParameter`,
`EscapeSubtitleFilterPath`) plus purpose-built extractors (`ExtractAudioImage`, `ExtractVideoImage`,
`GetMediaInfo`). Plugins spawn the process themselves.

Jellyfin's own canonical pattern, from
[`SubtitleEncoder.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.MediaEncoding/Subtitles/SubtitleEncoder.cs) line 807:

```csharp
using (var process = new Process
{
    StartInfo = new ProcessStartInfo
    {
        CreateNoWindow = true,
        UseShellExecute = false,
        FileName = _mediaEncoder.EncoderPath,
        Arguments = processArgs,
        WindowStyle = ProcessWindowStyle.Hidden,
        ErrorDialog = false
    },
    EnableRaisingEvents = true
})
```

Intro Skipper on its `10.11` branch
([`IntroSkipper/FFmpeg/FFmpegService.cs` at `10.11/v1.10.11.22`](https://github.com/intro-skipper/intro-skipper/blob/10.11/v1.10.11.22/IntroSkipper/FFmpeg/FFmpegService.cs))
uses `ArgumentList` rather than a joined string, which is safer on Windows paths:

```csharp
var info = new ProcessStartInfo(processPath)
{
    WindowStyle = ProcessWindowStyle.Hidden,
    CreateNoWindow = true,
    UseShellExecute = false,
    ErrorDialog = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};

foreach (var arg in args)
{
    info.ArgumentList.Add(arg);
}

using var process = new Process { StartInfo = info };
process.Start();
...
using var cancellationRegistration = cancellationToken.Register(() => KillProcessTree(process));
using var ms = new MemoryStream();

var stdoutTask = DrainAsync(process.StandardOutput.BaseStream, stderr ? null : ms, cancellationToken);
var stderrTask = DrainAsync(process.StandardError.BaseStream, stderr ? ms : null, cancellationToken);
await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
```

Two details worth copying for issue #6 (PCM streaming):

- **Drain stdout and stderr concurrently before `WaitForExitAsync`.** Not draining both pipes deadlocks
  ffmpeg on large PCM output. This is exactly our failure mode.
- Apply a timeout CTS and `Kill(entireProcessTree: true)` on cancel. Also prefix every invocation with
  `-hide_banner -loglevel warning`.

Note Intro Skipper does **not** use `IMediaEncoder.EncoderPath`; it reads
`serverConfiguration.GetEncodingOptions().EncoderAppPathDisplay`
([`Plugin.cs`](https://github.com/intro-skipper/intro-skipper/blob/10.11/v1.10.11.22/IntroSkipper/Plugin.cs) line 72).
`EncoderAppPathDisplay` is documented in
[`EncodingOptions.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/Configuration/EncodingOptions.cs)
(line 137) as "the current FFmpeg path being used by the system". `IMediaEncoder.EncoderPath` is what
core itself uses and is the better-typed route; `EncoderAppPathDisplay` is an equivalent fallback.

---

## 7. Getting a subtitle track as SRT

### Jellyfin's own endpoints

[`Jellyfin.Api/Controllers/SubtitleController.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Jellyfin.Api/Controllers/SubtitleController.cs).
The class is `[Route("")]`, so route templates are absolute.

| Line | Route | Auth |
| --- | --- | --- |
| 91 | `[HttpDelete("Videos/{itemId}/Subtitles/{index}")]` | `Policies.RequiresElevation` |
| 118 | `[HttpGet("Items/{itemId}/RemoteSearch/Subtitles/{language}")]` | `Policies.SubtitleManagement` |
| 144 | `[HttpPost("Items/{itemId}/RemoteSearch/Subtitles/{subtitleId}")]` | `Policies.SubtitleManagement` |
| 179 | `[HttpGet("Providers/Subtitles/Subtitles/{subtitleId}")]` | `Policies.SubtitleManagement` |
| **208** | `[HttpGet("Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/Stream.{routeFormat}")]` | **none** |
| **295** | `[HttpGet("Videos/{routeItemId}/{routeMediaSourceId}/Subtitles/{routeIndex}/{routeStartPositionTicks}/Stream.{routeFormat}")]` | **none** |
| 338 | `[HttpGet("Videos/{itemId}/{mediaSourceId}/Subtitles/{index}/subtitles.m3u8")]` | `[Authorize]` |
| 420 | `[HttpPost("Videos/{itemId}/Subtitles")]` | `Policies.SubtitleManagement` |

Signature of the primary one:

```csharp
public async Task<ActionResult> GetSubtitle(
    [FromRoute, Required] Guid routeItemId,
    [FromRoute, Required] string routeMediaSourceId,
    [FromRoute, Required] int routeIndex,
    [FromRoute, Required] string routeFormat,
    [FromQuery, ParameterObsolete] Guid? itemId,
    [FromQuery, ParameterObsolete] string? mediaSourceId,
    [FromQuery, ParameterObsolete] int? index,
    [FromQuery, ParameterObsolete] string? format,
    [FromQuery] long? endPositionTicks,
    [FromQuery] bool copyTimestamps = false,
    [FromQuery] bool addVttTimeMap = false,
    [FromQuery] long startPositionTicks = 0)
```

The query-string variants are `[ParameterObsolete]`; use the route form. If `format` is empty the
controller short-circuits and `PhysicalFile`s the external subtitle straight off disk.

Both `Stream.{routeFormat}` actions carry **no `[Authorize]` attribute**, and no global authorization
filter was found in `AddJellyfinApi` (`AddMvc` adds formatters and model binders only; the only
authorization setup is `AddAuthorizationCore`, which applies where `[Authorize]` is present). So these
two endpoints are effectively anonymous in 10.11.

### `ISubtitleEncoder` - use this instead of proxying

**Namespace is `MediaBrowser.Controller.MediaEncoding`, NOT `MediaBrowser.Controller.Subtitles`.**
(`MediaBrowser.Controller/Subtitles/` holds `ISubtitleManager`, `ISubtitleProvider`,
`SubtitleResponse`, `SubtitleSearchRequest`.)

[Source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/MediaEncoding/ISubtitleEncoder.cs):

```csharp
Task<Stream> GetSubtitles(
    BaseItem item,
    string mediaSourceId,
    int subtitleStreamIndex,
    string outputFormat,
    long startTimeTicks,
    long endTimeTicks,
    bool preserveOriginalTimestamps,
    CancellationToken cancellationToken);

Task<string> GetSubtitleFileCharacterSet(MediaStream subtitleStream, string language, MediaSourceInfo mediaSource, CancellationToken cancellationToken);

Task<string> GetSubtitleFilePath(MediaStream subtitleStream, MediaSourceInfo mediaSource, CancellationToken cancellationToken);

Task ExtractAllExtractableSubtitles(MediaSourceInfo mediaSource, CancellationToken cancellationToken);
```

`GetSubtitles` handles external files and embedded streams transparently: resolves the source, extracts
via ffmpeg if embedded, parses, filters events, writes via the requested writer.

**A plugin can inject `ISubtitleEncoder` directly.** All relevant services are singletons in
[`ApplicationHost.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/ApplicationHost.cs):

```csharp
serviceCollection.AddSingleton<IMediaEncoder, MediaBrowser.MediaEncoding.Encoder.MediaEncoder>();  // 518
serviceCollection.AddSingleton<ILibraryManager, LibraryManager>();                                  // 527
serviceCollection.AddSingleton<IMediaSourceManager, MediaSourceManager>();                          // 542
serviceCollection.AddSingleton<IProviderManager, ProviderManager>();                                // 547
serviceCollection.AddSingleton<ISubtitleEncoder, SubtitleEncoder>();                                // 570
```

For SRT pass `outputFormat: "srt"`. Valid values come from
[`MediaBrowser.Model.MediaInfo.SubtitleFormat`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/MediaInfo/SubtitleFormat.cs):
`SRT = "srt"`, `SUBRIP = "subrip"`, `SSA`, `ASS`, `VTT`, `WEBVTT`, `TTML`; the encoder's `TryGetWriter`
also accepts `"json"`.

**Answer to the issue's question: call `ISubtitleEncoder` directly, do not proxy the HTTP endpoint.**
In-process, no auth dance, no self-request, and it covers external and embedded uniformly.

Related: `MediaBrowser.Controller.IO.IPathManager`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/IO/IPathManager.cs))
is what `SubtitleEncoder` itself takes for its extraction cache.
`GetSubtitlePath(string mediaSourceId, int streamIndex, string extension)` gives the same cache
location core uses, useful for checking whether an embedded track is already extracted.

---

## 8. Triggering a refresh so a new sibling file is indexed

`IProviderManager` - namespace `MediaBrowser.Controller.Providers`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Providers/IProviderManager.cs)):

```csharp
void QueueRefresh(Guid itemId, MetadataRefreshOptions options, RefreshPriority priority);   // line 36
Task RefreshFullItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken);
Task<ItemUpdateType> RefreshSingleItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken);  // line 54
```

`QueueRefresh` takes a **`Guid itemId`**, not a `BaseItem`.

**The sanctioned pattern** is what Jellyfin does after downloading a remote subtitle
(`SubtitleController.DownloadRemoteSubtitles`):

```csharp
await _subtitleManager.DownloadSubtitles(item, subtitleId, CancellationToken.None)
    .ConfigureAwait(false);

_providerManager.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);
```

Copy that one-liner after writing our `.srt` (issue #8).

`MetadataRefreshOptions`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Controller/Providers/MetadataRefreshOptions.cs))
- **the constructor does require an `IDirectoryService`**:

```csharp
public class MetadataRefreshOptions : ImageRefreshOptions
{
    public MetadataRefreshOptions(IDirectoryService directoryService)
        : base(directoryService)
    {
        MetadataRefreshMode = MetadataRefreshMode.Default;
    }

    public MetadataRefreshOptions(MetadataRefreshOptions copy) { ... }

    public bool ReplaceAllMetadata { get; set; }
    public bool RegenerateTrickplay { get; set; }
    public MetadataRefreshMode MetadataRefreshMode { get; set; }
    public RemoteSearchResult SearchResult { get; set; }
    public string[] RefreshPaths { get; set; }
    public bool ForceSave { get; set; }
    public bool EnableRemoteContentProbe { get; set; }
}
```

**There is no separate `ImageRefreshMode` enum** - `ImageRefreshOptions.ImageRefreshMode` is typed
`MetadataRefreshMode`. The single enum is:

```csharp
public enum MetadataRefreshMode { None = 0, ValidationOnly = 1, Default = 2, FullRefresh = 3 }
```

`RefreshPriority`: `High = 0`, `Normal = 1`, `Low = 2`.

`MediaBrowser.Controller.Providers.DirectoryService(IFileSystem fileSystem)` caches directory listings
in `ConcurrentDictionary`s. **Always construct a fresh instance after writing the file** - a stale one
will not see the new `.srt`.

`ILibraryManager.QueueLibraryScan()` (line 662) is explicitly documented "This exists so plugins can
trigger a library scan", but it is a full-library scan and far too heavy here.

Why the refresh picks the file up:
[`ProbeProvider.HasChanged`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Providers/MediaInfo/ProbeProvider.cs) line 145:

```csharp
var externalFiles = new HashSet<string>(_subtitleResolver.GetExternalFiles(video, directoryService, false).Select(info => info.Path), StringComparer.OrdinalIgnoreCase);
if (!new HashSet<string>(video.SubtitleFiles, StringComparer.Ordinal).SetEquals(externalFiles))
{
    _logger.LogDebug("Refreshing {ItemPath} due to external subtitles change.", item.Path);
    return true;
}
```

which leads to `FFProbeVideoInfo.AddExternalSubtitlesAsync`. Note it passes `clearCache: false`,
reinforcing the fresh-`DirectoryService` requirement.

---

## 9. External subtitle filename conventions

Two stages: a candidate filter, then a segment parser.

### Stage 1 - candidate filter

[`MediaInfoResolver.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Providers/MediaInfo/MediaInfoResolver.cs) line 234:

```csharp
ReadOnlySpan<char> prefix = video.FileNameWithoutExtension;
foreach (var file in files)
{
    var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file.AsSpan());
    if (fileNameWithoutExtension.Length >= prefix.Length
        && prefix.Equals(fileNameWithoutExtension[..prefix.Length], StringComparison.OrdinalIgnoreCase)
        && (fileNameWithoutExtension.Length == prefix.Length || _namingOptions.MediaFlagDelimiters.Contains(fileNameWithoutExtension[prefix.Length])))
    {
        var externalPathInfo = _externalPathParser.ParseFile(file, fileNameWithoutExtension[prefix.Length..].ToString());
```

The file must live in `video.ContainingFolderPath` (or the item's internal metadata path), must start
with the video's filename-without-extension (case-insensitive), and the next character must be a
`MediaFlagDelimiters` char. `SubtitleResolver` is a thin subclass with `DlnaProfileType.Subtitle`.

### Stage 2 - segment parser

`Emby.Naming.ExternalFiles.ExternalPathParser.ParseFile`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Naming/ExternalFiles/ExternalPathParser.cs)):

```csharp
while (languageString.Length > 0)
{
    int lastSeparator = languageString.LastIndexOf(separator);
    if (lastSeparator == -1) { break; }

    string currentSlice = languageString[lastSeparator..];
    string currentSliceWithoutSeparator = currentSlice[SeparatorLength..];

    if (_namingOptions.MediaDefaultFlags.Any(s => currentSliceWithoutSeparator.Contains(s, StringComparison.OrdinalIgnoreCase)))
    {
        pathInfo.IsDefault = true;
        ...
        continue;
    }

    if (_namingOptions.MediaForcedFlags.Any(s => currentSliceWithoutSeparator.Contains(s, StringComparison.OrdinalIgnoreCase)))
    {
        pathInfo.IsForced = true;
        ...
        continue;
    }

    var culture = _localizationManager.FindLanguageInfo(currentSliceWithoutSeparator);

    if (culture is not null && pathInfo.Language is null)
    {
        pathInfo.Language = culture.Name.Contains('-', StringComparison.OrdinalIgnoreCase)
                          ? culture.Name
                          : culture.ThreeLetterISOLanguageName;
        ...
    }
    else if (culture is not null && pathInfo.Language == "hin")
    {
        pathInfo.IsHearingImpaired = true;
        pathInfo.Language = ...;
    }
    else if (_namingOptions.MediaHearingImpairedFlags.Any(s => currentSliceWithoutSeparator.Equals(s, StringComparison.OrdinalIgnoreCase)))
    {
        pathInfo.IsHearingImpaired = true;
        ...
    }
    else
    {
        titleString = currentSlice + titleString;
    }

    languageString = languageString[..lastSeparator];
}

pathInfo.Title = titleString.Length >= SeparatorLength ? titleString[SeparatorLength..] : null;
```

Flag vocabularies,
[`NamingOptions.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Naming/Common/NamingOptions.cs) lines 297-318:

```csharp
MediaFlagDelimiters = [ '.' ];
MediaForcedFlags = [ "foreign", "forced" ];
MediaDefaultFlags = [ "default" ];
MediaHearingImpairedFlags = [ "cc", "hi", "sdh" ];
```

**How `<base>.<lang>.<title>.srt` actually parses.** Segments are consumed **right to left**, and
classification is **content-based, not positional**. Nothing says "the second segment is the language".
For `Movie.eng.Signs.srt`: `.Signs` is not a flag and does not resolve as a language, so it becomes the
title; `.eng` resolves, so `Language = "eng"`. Result `Language=eng`, `Title="Signs"`.

A segment becomes:

- `IsDefault` if it **contains** `"default"` (substring, case-insensitive)
- `IsForced` if it **contains** `"foreign"` or `"forced"` (substring, so `.Unforced` also trips it)
- the **Language** if `ILocalizationManager.FindLanguageInfo` resolves it **and no language has been
  assigned yet** - with two language-like segments the **rightmost wins**
- `IsHearingImpaired` if it **exactly equals** `"cc"`, `"hi"` or `"sdh"` (`Equals`, not `Contains`)
- otherwise part of the **Title**, prepended, so multiple leftover segments keep their original order
  (`Movie.eng.My.Notes.srt` gives `Title = "My.Notes"`)

Language is stored as `culture.Name` when it contains `-` (e.g. `zh-Hans`), else
`culture.ThreeLetterISOLanguageName` (e.g. `eng`).

The `.hi` collision is handled explicitly: a bare right-most `.hi` resolves to Hindi
(`Language = "hin"`). If a real language appears further left (`Movie.eng.hi.srt`) the second branch
fires, setting `IsHearingImpaired = true` and overwriting `Language` with `"eng"`. Prefer `.sdh` or
`.cc` for unambiguous HI marking.

Flags do not need to be last - each is stripped and the scan continues, so `Movie.eng.forced.Signs.srt`
yields `Language=eng`, `IsForced=true`, `Title="Signs"`.

### Parser result to `MediaStream`

`MediaInfoResolver.MergeMetadata` (line 337) - parsed values only fill gaps ffprobe left:

```csharp
mediaStream.Path = pathInfo.Path;
mediaStream.IsExternal = true;
mediaStream.Title = string.IsNullOrEmpty(mediaStream.Title) ? (string.IsNullOrEmpty(pathInfo.Title) ? null : pathInfo.Title) : mediaStream.Title;
mediaStream.Language = string.IsNullOrEmpty(mediaStream.Language) ? (string.IsNullOrEmpty(pathInfo.Language) ? null : pathInfo.Language) : mediaStream.Language;
```

and in `GetExternalStreamsAsync` (line 120), when the external file has exactly one stream:

```csharp
mediaStream.Index = startIndex++;
mediaStream.IsDefault = pathInfo.IsDefault;
mediaStream.IsForced = pathInfo.IsForced || mediaStream.IsForced;
mediaStream.IsHearingImpaired = pathInfo.IsHearingImpaired || mediaStream.IsHearingImpaired;
```

### How the title surfaces in the track picker

`MediaStream.DisplayTitle`, subtitle branch
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/Entities/MediaStream.cs) lines ~388-465).
Attributes are appended in order: full language name (or `Und` if `Language` is empty), Hearing
Impaired, Default, Forced, `Codec.ToUpperInvariant()`, External.

```csharp
if (!string.IsNullOrEmpty(Title))
{
    var result = new StringBuilder(Title);
    foreach (var tag in attributes)
    {
        // Keep Tags that are not already in Title.
        if (!Title.Contains(tag, StringComparison.OrdinalIgnoreCase))
        {
            result.Append(" - ").Append(tag);
        }
    }

    return result.ToString();
}

return string.Join(" - ", attributes);
```

**Key behaviour:** when `Title` is set it becomes the **leading** text, and each attribute is appended
as ` - <attr>` **only if `Title` does not already contain that string** (case-insensitive substring).
So `Movie.eng.Synced.srt` displays as `Synced - English - SRT - External`. Naming it
`Movie.eng.Synced External.srt` would suppress the `External` tag. Unlike audio, the subtitle branch
always renders the language or `Und`.

**Recommended naming for issue #4:** `<FileNameWithoutExtension>.<lang>.<marker>.srt`, e.g.
`Movie.eng.synced.srt`. Avoid markers containing `default`, `forced` or `foreign`; avoid markers equal
to `cc`, `hi` or `sdh`; avoid a marker that is itself a resolvable language name. `synced` is safe on
all three counts. Epic #18 already specifies `<base>.<lang>.synced.srt` - **confirmed safe.**

---

## 10. Application paths and the plugin data directory

`IApplicationPaths` - namespace `MediaBrowser.Common.Configuration`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Common/Configuration/IApplicationPaths.cs)):

```csharp
string ProgramDataPath { get; }
string WebPath { get; }
string ProgramSystemPath { get; }
string DataPath { get; }
string ImageCachePath { get; }
string PluginsPath { get; }
string PluginConfigurationsPath { get; }
string LogDirectoryPath { get; }
string ConfigurationDirectoryPath { get; }
string SystemConfigurationFilePath { get; }
string CachePath { get; }
string TempDirectory { get; }
string VirtualDataPath { get; }
string TrickplayPath { get; }
string BackupPath { get; }

void MakeSanityCheckOrThrow();
void CreateAndCheckMarker(string path, string markerName, bool recursive = false);
```

Registered as a singleton (`ApplicationHost.cs` line 479:
`serviceCollection.AddSingleton<IApplicationPaths>(ApplicationPaths);`).

`IServerApplicationPaths : IApplicationPaths` - namespace `MediaBrowser.Controller` - adds
`RootFolderPath`, `DefaultUserViewsPath`, `PeoplePath`, `GenrePath`, `MusicGenrePath`, `StudioPath`,
`YearPath`, `UserConfigurationDirectoryPath`, `DefaultInternalMetadataPath`, `InternalMetadataPath`,
`VirtualInternalMetadataPath`, `ArtistsPath`.

### Where to put our signal cache (issue #9)

**Do not use `BasePlugin.DataFolderPath`.** It exists (`public string DataFolderPath { get; private set; }`)
but three things about it are traps, all verified in source:

1. It is computed inside `BasePlugin<T>`'s constructor as
   `Path.Combine(ApplicationPaths.PluginsPath, Path.GetFileNameWithoutExtension(assemblyFilePath))`,
   with `"_" + Version` appended if that directory does not exist. That is the plugin's **install**
   directory, so it is wiped on plugin update or uninstall.
2. `SetAttributes` is called only from `BasePluginOfT.cs`; `PluginManager.cs` does not call it. A
   plugin deriving from the non-generic `BasePlugin` gets a null `DataFolderPath`.
3. Nothing creates the directory for you. `Directory.CreateDirectory` is called only for the
   configuration file's folder inside `SaveConfiguration`.

Intro Skipper, a real 10.11 plugin with exactly our caching problem, builds its own folder under
`DataPath` instead:

```csharp
var pluginDirName = "introskipper";
var pluginCachePath = "chromaprints";
var introsDirectory = Path.Join(applicationPaths.DataPath, pluginDirName);
```

That survives plugin upgrades. **Recommendation:** `Path.Join(applicationPaths.DataPath, "subtitlesync")`
for the signal cache (it is expensive to regenerate, so `DataPath` beats `CachePath`), and
`applicationPaths.TempDirectory` for scratch PCM. `PluginConfigurationsPath` is for the XML config only
(`BasePlugin<T>.ConfigurationFilePath` is
`Path.Combine(ApplicationPaths.PluginConfigurationsPath, ConfigurationFileName)`); do not put a cache
there.

---

## 11. The File Transformation plugin

**This is the highest-risk dependency in the epic. Read this section in full before committing to it.**

- Repo: [`IAmParadox27/jellyfin-plugin-file-transformation`](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation)
- **Plugin GUID: `5e87cc92-571a-4d8d-8d98-d2d4147f9f90`**
  ([`FileTransformationPlugin.cs`](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2.5.11.0/src/Jellyfin.Plugin.FileTransformation/FileTransformationPlugin.cs))
- Latest tag at time of research: `2.5.11.0`. Repo last pushed 2026-07-01. 480 stars.
- Install repository: `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
- Targets `net9.0` for 10.11, references `Jellyfin.Model` / `Jellyfin.Controller` / `Jellyfin.Data` /
  `Jellyfin.Extensions` at `10.11.0`, plus `Lib.Harmony 2.4.0`.

### How it actually works (and why it is fragile)

It is not a hook. It **Harmony-patches `Jellyfin.Server.Startup.Configure`** and re-implements that
whole method, substituting its own `IFileProvider` for the two `UseDefaultFiles` / `UseStaticFiles`
calls that serve `/web`. From
[`Helpers/StartupHelper.cs`](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2.5.11.0/src/Jellyfin.Plugin.FileTransformation/Helpers/StartupHelper.cs):

```csharp
// When updating Jellyfin version ensure this function is updated to match the targeted version of Jellyfin.
internal static bool Patch_Startup_Configure(IApplicationBuilder app, IWebHostEnvironment env,
    IConfiguration appConfig, ref object __instance)
{
    ...
    mainApp.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = WebStaticFilesFileProvider?.Invoke(serverConfigurationManager, mainApp)
                       ?? new PhysicalFileProvider(serverConfigurationManager.ApplicationPaths.WebPath),
        RequestPath = "/web",
        ContentTypeProvider = extensionProvider
    }.ConfigureVersionSpecific());
    ...
    return false;   // suppress the original Startup.Configure
}
```

It also reaches into private fields by reflection (`_serverConfigurationManager`,
`_serverApplicationHost`, `IServerApplicationHost.ApplicationPaths`, `LoggerFactory`,
`PublishedServerUrl`). This is why the author ships **a separate build per Jellyfin patch release** and
why his FAQ says "the plugins are strictly 1 version compatible". Any change to `Startup.Configure` in
a 10.11.x point release can break it.

**Risk statement for epic #18:** the epic calls this "unsupported by Jellyfin and can break on any web
client update". It is worse than that - it can break on any *server* point release, independent of the
web client. The Dashboard fallback is not a nicety, it is the primary path in practice.

### Registering a transformation from our plugin

Two supported entry points.

**(a) Reflection into the loaded assembly** (what all of IAmParadox's own dependent plugins do; needed
because Jellyfin loads each plugin into a separate `AssemblyLoadContext`, so a direct project reference
does not work). Real example from
[`jellyfin-plugin-pages/Services/StartupService.cs`](https://github.com/IAmParadox27/jellyfin-plugin-pages/blob/main/src/Jellyfin.Plugin.PluginPages/Services/StartupService.cs):

```csharp
JObject payload = new JObject();
payload.Add("id", "9340b171-0ae4-4d13-9970-9c4c4feba227");
payload.Add("fileNamePattern", "index.html");
payload.Add("callbackAssembly", GetType().Assembly.FullName);
payload.Add("callbackClass", typeof(TransformationPatches).FullName);
payload.Add("callbackMethod", nameof(TransformationPatches.IndexHtml));

Assembly? fileTransformationAssembly =
    AssemblyLoadContext.All.SelectMany(x => x.Assemblies).FirstOrDefault(x =>
        x.FullName?.Contains(".FileTransformation") ?? false);

if (fileTransformationAssembly != null)
{
    Type? pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");

    if (pluginInterfaceType != null)
    {
        pluginInterfaceType.GetMethod("RegisterTransformation")?.Invoke(null, new object?[] { payload });
    }
}
```

Note the actual signature is
`public static void RegisterTransformation(Newtonsoft.Json.Linq.JObject payload)`
([`PluginInterface.cs`](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2.5.11.0/src/Jellyfin.Plugin.FileTransformation/PluginInterface.cs)),
so **we need a `Newtonsoft.Json` PackageReference** to build the payload object (or construct the
`JObject` itself reflectively). This is a real, non-obvious constraint on our csproj.

Registration is done from an `IScheduledTask` whose trigger fires on startup (see
`StartupServiceHelper.GetDefaultTriggers()` in the same repo).

**(b) HTTP POST to File Transformation's own controller** -
`POST /FileTransformation/RegisterTransformation`, body = the same payload, `[Authorize(Policy =
Policies.RequiresElevation)]`
([`FileTransformationController.cs`](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2.5.11.0/src/Jellyfin.Plugin.FileTransformation/Controller/FileTransformationController.cs)).
Avoids the Newtonsoft dependency but needs an authenticated call back into our own server. Prefer (a).

### The payload schema

[`Models/TransformationRegistrationPayload.cs`](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2.5.11.0/src/Jellyfin.Plugin.FileTransformation/Models/TransformationRegistrationPayload.cs):

```csharp
public class TransformationRegistrationPayload
{
    [JsonPropertyName("id")]                    public Guid Id { get; set; }
    [JsonPropertyName("fileNamePattern")]       public string FileNamePattern { get; set; } = string.Empty;
    [JsonPropertyName("transformationEndpoint")] public string TransformationEndpoint { get; set; } = string.Empty;
    [JsonPropertyName("transformationPipe")]    public string? TransformationPipe { get; set; } = null;
    [JsonPropertyName("callbackAssembly")]      public string? CallbackAssembly { get; set; } = null;
    [JsonPropertyName("callbackClass")]         public string? CallbackClass { get; set; } = null;
    [JsonPropertyName("callbackMethod")]        public string? CallbackMethod { get; set; } = null;
}
```

`fileNamePattern` is a **regex** matched against the requested path - and, found the hard way during
#13, it is matched against the path spelled **two different ways**.
`WebFileTransformationService.NeedsTransformation` is called with the static file middleware's
subpath, `/index.html`, and tests the regex against it unmodified;
`RunTransformation` then strips the leading slash and tests the same regex against `index.html`. The
dictionary fast-path in between is keyed on the *pattern*, not on a filename, so it never helps. A
pattern of `^index\.html$` therefore matches neither call and the transformation silently never runs.
`(^|/)index\.html$` matches both. IAmParadox's own plugins sidestep this by passing a bare
`index.html`, which as a regex matches anywhere in either spelling. Three delivery mechanisms, tried
in order by
[`TransformationHelper.ApplyTransformation`](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2.5.11.0/src/Jellyfin.Plugin.FileTransformation/Helpers/TransformationHelper.cs):
in-process reflection callback (`callbackAssembly`/`callbackClass`/`callbackMethod`), named pipe
(`transformationPipe`), then HTTP POST (`transformationEndpoint`).

The callback must be a **public static method taking one parameter** whose shape deserialises from
`{ "contents": "<current file text>" }` and **returning `string`** (the new contents):

```csharp
ParameterInfo payloadParameter = method.GetParameters()[0];
object? paramObj = obj.ToObject(payloadParameter.ParameterType);
transformedString = (string)method.Invoke(null, new object?[] { paramObj })!;
```

So our patch class looks like:

```csharp
public class TransformationPayload { public string Contents { get; set; } = string.Empty; }

public static class TransformationPatches
{
    public static string ConfigJson(TransformationPayload payload) { ... return newContents; }
}
```

Internally File Transformation stores the transform via
`IWebFileTransformationWriteService.AddTransformation(Guid id, string path, TransformFile transformation)`
where `public delegate Task TransformFile(string path, Stream contents);`. That interface is not
reachable from our assembly - use `PluginInterface.RegisterTransformation`.

### Detecting that it is absent

The idiomatic detection is exactly the assembly probe above:

```csharp
Assembly? ft = AssemblyLoadContext.All.SelectMany(x => x.Assemblies)
    .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation") ?? false);
bool available = ft?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface") is not null;
```

Note the failure mode: `GetMethod(...)?.Invoke(...)` returns silently if the method is missing, so a
future API change looks identical to "not installed". **Log the distinction explicitly** and surface it
on our config page so the admin knows whether the Subtitles menu item will appear.

A cleaner alternative available in 10.11: inject `MediaBrowser.Common.Plugins.IPluginManager` and check
for the GUID directly.

```csharp
bool ftInstalled = _pluginManager.Plugins
    .Any(p => p.Id == Guid.Parse("5e87cc92-571a-4d8d-8d98-d2d4147f9f90")
              && p.Manifest.Status == PluginStatus.Active);
```

Use both: `IPluginManager` for "is it installed and active" (good for the UI message) and the assembly
probe for "can I actually call it" (good for the code path).

### What NOT to use

`IAmParadox27/jellyfin-plugin-referenceable` (the NuGet library that solves the cross-load-context
problem properly) tops out at version `1.2.0` for **Jellyfin 10.10.5**. There is no 10.11 version, and
File Transformation itself no longer references it. Do not plan around it.

---

## 12. UI extension points in the 10.11 web client

**Headline: there is no supported extension point in Jellyfin 10.11 for adding UI to the item detail
page, and no server-driven client-plugin loader at all. The epic's design assumption holds.** Details
below, because the reasons matter for choosing the least-bad injection target.

### The web client's `pluginManager` does not do discovery

[`src/components/pluginManager.js` at v10.11.11](https://github.com/jellyfin/jellyfin-web/blob/v10.11.11/src/components/pluginManager.js):

```js
// In lieu of automatic discovery, plugins will register dynamic objects
// Each object will have the following properties:
// name
// type (skin, screensaver, etc)
#register(obj) {
    this.pluginsList.push(obj);
    Events.trigger(this, 'registered', [obj]);
}
```

`loadPlugin(pluginSpec)` accepts a string and does either `window[pluginSpec]` (an already-loaded
global) or `import('../plugins/' + pluginSpec)` (a **webpack-bundled** module). It cannot load an
arbitrary URL. The list comes from `config.json`'s `plugins` array, and every entry there is a built-in
(`htmlVideoPlayer/plugin`, `syncPlay/plugin`, ...). Plugin "types" are players, screensavers and skins,
not detail-page UI.

Confirmed at [`src/index.jsx` v10.11.11](https://github.com/jellyfin/jellyfin-web/blob/v10.11.11/src/index.jsx):

```js
async function loadPlugins() {
    let list = await getPlugins();
    ...
    // add any native plugins
    if (window.NativeShell) { list = list.concat(window.NativeShell.getPlugins()); }
    await Promise.all(list.map(plugin => pluginManager.loadPlugin(plugin)));
}
```

`window.NativeShell` is for native wrappers (Android/desktop), not server plugins.

### Server-side branding gives CSS only, not JS

[`MediaBrowser.Model/Branding/BrandingOptions.cs` at v10.11.11](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/Branding/BrandingOptions.cs):

```csharp
public class BrandingOptions
{
    public string? LoginDisclaimer { get; set; }
    public string? CustomCss { get; set; }
    public bool SplashscreenEnabled { get; set; } = false;
    public string? SplashscreenLocation { get; set; }
}
```

Custom CSS is admin-editable and injected. **There is no custom-JS field.** CSS alone cannot add a menu
item that opens a page with an item id.

### Where the Subtitles affordance actually lives in 10.11

The item detail page is still the **legacy** controller, mounted as a legacy route inside the React
"experimental" app shell.
[`src/apps/experimental/routes/legacyRoutes/user.ts` v10.11.11](https://github.com/jellyfin/jellyfin-web/blob/v10.11.11/src/apps/experimental/routes/legacyRoutes/user.ts):

```ts
export const LEGACY_USER_ROUTES: LegacyRoute[] = [
    {
        path: 'details',
        pageProps: {
            controller: 'itemDetails/index',
            view: 'itemDetails/index.html'
        }
    },
```

The "Subtitles" entry in the `...` menu is built in
[`src/components/itemContextMenu.js` v10.11.11](https://github.com/jellyfin/jellyfin-web/blob/v10.11.11/src/components/itemContextMenu.js):

```js
if (itemHelper.canEditSubtitles(user, item) && options.editSubtitles !== false) {
    commands.push({
        name: globalize.translate('EditSubtitles'),
        id: 'editsubtitles',
        icon: 'closed_caption'
    });
}
```

and dispatched by:

```js
case 'editsubtitles':
    import('./subtitleeditor/subtitleeditor').then(({ default: subtitleEditor }) => {
        subtitleEditor.show(itemId, serverId).then(...);
    });
    break;
```

`itemContextMenu` is imported by both the legacy detail controller and the React
`MoreCommandsButton.tsx`, so patching it covers both surfaces. But it lives in a **content-hashed
chunk** - webpack output at v10.11.11 is:

```js
output: {
    filename: pathData => (pathData.chunk.name === 'serviceworker' ? '[name].js' : '[name].bundle.js'),
    chunkFilename: '[name].[contenthash].chunk.js',
```

so the File Transformation `fileNamePattern` regex would have to be hash-tolerant, and the search text
would be matched against **minified** output. This is the brittle path.

> **Settled during #13, against a live 10.11.11 container:** the chunk is
> `55802.9a5b7bc258c2f90abe5e.chunk.js`. Both halves of the name are unstable - `55802` is a webpack
> module id and the rest is a content hash - so there is nothing here a pattern could safely anchor
> to. `index.html` it is.

Also note that File Transformation only forces `Cache-Control: no-cache` for `index.html` and
`main.jellyfin.bundle.js` on 10.11
([`JellyfinVersionSpecific/10.11/StartupHelper_VersionSpecific.cs`](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2.5.11.0/src/Jellyfin.Plugin.FileTransformation/JellyfinVersionSpecific/10.11/StartupHelper_VersionSpecific.cs)),
so transforming a hashed chunk risks stale browser caches.

### A much more robust File Transformation target: `config.json`

`config.json` is fetched at runtime, uncached, and is plain JSON.
[`src/scripts/settings/webSettings.js` v10.11.11](https://github.com/jellyfin/jellyfin-web/blob/v10.11.11/src/scripts/settings/webSettings.js):

```js
const response = await fetchLocal('config.json', { cache: 'no-store' });
```

Its schema at v10.11.11 ([`src/types/webConfig.ts`](https://github.com/jellyfin/jellyfin-web/blob/v10.11.11/src/types/webConfig.ts)):

```ts
export interface WebConfig {
    includeCorsCredentials?: boolean
    multiserver?: boolean
    themes?: Theme[]
    menuLinks?: MenuLink[]
    servers?: string[]
    plugins?: string[]
}
interface MenuLink { name: string; icon?: string; url: string }
```

`menuLinks` is rendered into the main drawer by
[`src/scripts/libraryMenu.js`](https://github.com/jellyfin/jellyfin-web/blob/v10.11.11/src/scripts/libraryMenu.js):

```js
getMenuLinks().then(links => {
    links.forEach(link => {
        const option = document.createElement('a', 'emby-linkbutton');
        option.classList.add('navMenuOption', 'lnkMediaFolder');
        option.rel = 'noopener noreferrer';
        option.target = '_blank';
        option.href = link.url;
```

Caveats: it opens in a **new tab** (`target = '_blank'`), and it is a global sidebar link with no item
context, so it cannot replace the per-item Subtitles button. But transforming `config.json` is orders
of magnitude more stable than transforming a minified chunk, so it is worth having as a middle tier
between "detail-page injection" and "Dashboard only".

Also possible via `index.html` transformation: inject a `<script src="/SubtitleSync/client.js">` tag.
`index.html` is one of the two files File Transformation explicitly marks `no-cache` on 10.11, and
`index.html` is a stable, non-minified target. Our script can then attach a delegated click listener
and add the menu item to the DOM when the `...` menu opens. This decouples us from webpack chunk hashes
and minified identifiers entirely, at the cost of DOM-shape coupling. **Recommend this over patching
`itemContextMenu` inside a hashed chunk.**

### Unconfirmed on this topic

- Whether Jellyfin 12.0 (currently `v12.0-rc4`, using a new `src/apps/modern` React app) introduces a
  supported client plugin API. Not investigated, out of scope for 10.11, but our detail-page injection
  will need rewriting for 12.0 regardless - the item detail page is being rebuilt in React there.
- 10.11 release notes mention two new plugin APIs: custom database access (explicitly "HIGHLY
  experimental", stability targeted for 10.12) and an external URL provider interface for metadata
  plugins ([jellyfin.org/posts/jellyfin-release-10.11.0](https://jellyfin.org/posts/jellyfin-release-10.11.0/)).
  Neither is a UI extension point.

---

## 13. Plugin repository manifest JSON schema

The manifest is a **JSON array** of `PackageInfo` objects, each with a `versions` array of
`VersionInfo`. Both are plain `System.Text.Json` DTOs, so **unknown fields are silently ignored**.

[`MediaBrowser.Model/Updates/PackageInfo.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/Updates/PackageInfo.cs)
- exact JSON property names:

| JSON field | C# type | Notes |
| --- | --- | --- |
| `name` | string | |
| `description` | string | long description |
| `overview` | string | short blurb |
| `owner` | string | |
| `category` | string | |
| `guid` | Guid | maps to `PackageInfo.Id` |
| `versions` | `IList<VersionInfo>` | |
| `imageUrl` | string? | |

[`MediaBrowser.Model/Updates/VersionInfo.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Model/Updates/VersionInfo.cs),
verbatim minus doc comments:

```csharp
public class VersionInfo
{
    private SysVersion? _version;

    [JsonPropertyName("version")]
    public string Version
    {
        get => _version is null ? string.Empty : _version.ToString();
        set => _version = SysVersion.Parse(value);
    }

    public SysVersion VersionNumber => _version ?? new SysVersion(0, 0, 0);

    [JsonPropertyName("changelog")]
    public string? Changelog { get; set; }

    [JsonPropertyName("targetAbi")]
    public string? TargetAbi { get; set; }

    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; set; }

    [JsonPropertyName("checksum")]
    public string? Checksum { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("repositoryName")]
    public string RepositoryName { get; set; } = string.Empty;

    [JsonPropertyName("repositoryUrl")]
    public string RepositoryUrl { get; set; } = string.Empty;
}
```

`repositoryName` and `repositoryUrl` are filled in by the server at load time; do not emit them.

### `targetAbi`

- Format: a 4-part version string parsed by `System.Version.TryParse`. For 10.11 the correct value is
  **`"10.11.0.0"`**. That is what the official Jellyfin repository ships for 10.11-compatible plugins
  (verified live at `https://repo.jellyfin.org/files/plugin/manifest.json`, e.g. Bookshelf 13.0.0.0
  has `"targetAbi": "10.11.0.0"`).
- **It is a minimum, not an exact match.** From
  [`InstallationManager.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/Updates/InstallationManager.cs):

```csharp
if (!Version.TryParse(ver.TargetAbi, out var targetAbi))
{
    targetAbi = minimumVersion;   // new Version(0, 0, 0, 1)
}

// Only show plugins that are greater than or equal to targetAbi.
if (_applicationHost.ApplicationVersion >= targetAbi)
{
    continue;
}

// Not compatible with this version so remove it.
entry.Versions.Remove(ver);
```

and in `GetCompatibleVersions`:

```csharp
.Where(x => string.IsNullOrEmpty(x.TargetAbi) || Version.Parse(x.TargetAbi) <= appVer);
```

There is **no upper bound**. A plugin with `targetAbi: 10.11.0.0` is offered on a 12.0 server too.
Set `targetAbi` to the lowest 10.11 patch you actually need (`10.11.0.0` unless we depend on something
added later).

Note that IAmParadox's manifest publishes a **separate entry per Jellyfin patch release**
(`10.11.0.0`, `10.11.1.0`, `10.11.2.0`, ...) with a different zip for each. That is not a general
requirement; it is a consequence of File Transformation's Harmony patching (section 11). We should not
copy that pattern.

### `checksum`

MD5 of the zip, hex. From
[`InstallationManager.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/Updates/InstallationManager.cs):

```csharp
var hash = Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
if (!string.Equals(package.Checksum, hash, StringComparison.OrdinalIgnoreCase))
```

`Convert.ToHexString` produces uppercase, but the comparison is `OrdinalIgnoreCase`, so **either case
works**. The official Jellyfin repo emits lowercase; IAmParadox emits uppercase.

### `timestamp`

Parsed with:

```csharp
Timestamp = string.IsNullOrEmpty(versionInfo.Timestamp)
    ? DateTime.MinValue
    : DateTime.Parse(versionInfo.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
```

([`PluginManager.cs`](https://github.com/jellyfin/jellyfin/blob/v10.11.11/Emby.Server.Implementations/Plugins/PluginManager.cs))

So: invariant-culture parseable, converted to UTC. Use ISO 8601 with a `Z`, which is what the official
repo emits: `"2025-10-20T01:25:49Z"`.

### `dependencies` is inert

`VersionInfo` has **no** `Dependencies` property in 10.11 (nor in 10.10.7 - checked both). `PackageInfo`
has none either. Jellyfin's own test manifest
(`tests/Jellyfin.Server.Implementations.Tests/Test Data/Updates/manifest.json`) contains no
`dependencies` key. IAmParadox's manifest emits
`"dependencies": ["5e87cc92-571a-4d8d-8d98-d2d4147f9f90"]`, which `System.Text.Json` silently
discards.

**Conclusion: there is no plugin dependency mechanism in Jellyfin 10.11.** We cannot declare a hard
dependency on File Transformation. We must:

1. emit `dependencies` anyway, as documentation and future-proofing (it costs nothing), and
2. detect File Transformation's absence at runtime and degrade to the Dashboard page (section 11), and
3. tell the user in our install docs to install File Transformation first.

### Reference: minimal manifest for us

```json
[
  {
    "guid": "<our plugin guid>",
    "name": "Subtitle Sync",
    "overview": "Automatically re-time subtitles against the audio track",
    "description": "...",
    "owner": "SirCen",
    "category": "General",
    "imageUrl": "https://subtitlesync.sircen.dev/jellyfin/logo.png",
    "versions": [
      {
        "version": "1.0.0.0",
        "changelog": "Initial release",
        "targetAbi": "10.11.0.0",
        "sourceUrl": "https://github.com/SirCen/subtitle-sync/releases/download/plugin-v1.0.0.0/subtitle-sync_1.0.0.0.zip",
        "checksum": "0123456789abcdef0123456789abcdef",
        "timestamp": "2026-08-07T12:00:00Z"
      }
    ]
  }
]
```

### The in-zip `meta.json`

Separate schema, `MediaBrowser.Common.Plugins.PluginManifest`
([source](https://github.com/jellyfin/jellyfin/blob/v10.11.11/MediaBrowser.Common/Plugins/PluginManifest.cs)):
`category`, `changelog`, `description`, `guid`, `name`, `overview`, `owner`, `targetAbi`,
`timestamp` (a real `DateTime` here, not a string), `version`, `status`, `autoUpdate`, `imagePath`,
`assemblies`. It is written by the server on install from the repository manifest, and reconciled with
any `meta.json` present in the zip. Also no `dependencies`.

---

## Unconfirmed

Things we could not verify against a primary source. Do not build on these without checking first.

- **`IServerApplicationPaths` injectability.** `IApplicationPaths` is explicitly
  `AddSingleton`-registered (`ApplicationHost.cs` line 479). No explicit
  `AddSingleton<IServerApplicationPaths>` line was found, although it is the concrete type the host is
  constructed with and appears resolvable in practice. **Depend on `IApplicationPaths`.**
- **Whether `BackupPath`, `MakeSanityCheckOrThrow()` and `CreateAndCheckMarker(...)` are new in 10.11.**
  Their presence at v10.11.11 is confirmed; the diff against 10.10 was not done line by line.
- **Whether a plugin built against `Jellyfin.Controller 10.10.x` loads unmodified on 10.11.** The ABI
  check only compares `targetAbi` to the server version; it would not catch a binary break. Assume a
  rebuild is required.
- **Whether `EncoderLocationType` was renamed rather than removed.** A recursive tree scan at v10.11.11
  found no `EncoderLocationType` and no `FFmpegLocation` file, and nothing obviously equivalent.
- ~~**Which webpack chunk `itemContextMenu.js` lands in at 10.11, and the exact content-hashed
  filename.**~~ **SETTLED** during #13, against the running `jellyfin/jellyfin:10.11.11` container:
  it is `55802.9a5b7bc258c2f90abe5e.chunk.js`. Both halves of that name are unstable - `55802` is a
  webpack module id and the rest is a content hash - and the contents are minified. This confirmed
  the `index.html` script-injection approach over chunk patching.
- **Whether Jellyfin 12.0 introduces a supported client plugin API.** Not investigated; out of scope for
  10.11. Note that `master` is `v12.0-rc4` and the item detail page is being rebuilt in React under
  `src/apps/modern`, so our injection will need rework for 12.0 regardless.
- **Official documentation for plugin development.** There is none.
  `https://jellyfin.org/docs/general/contributing/development/plugins/` returns 404, and
  `jellyfin/jellyfin.org@master` has no plugin development page under `docs/general/contributing/`
  (only `branding`, `development`, `documentation`, `issues`, `llm-policies`, `release-procedure`,
  `source-tree`). Every claim in this document therefore comes from source, which is stronger, but it
  means there is no stable spec to point at.

---

## Impact on epic #18

Things in the epic that this research changes or sharpens.

| Epic assumption | Status |
| --- | --- |
| Detail-page button injected via File Transformation, Dashboard fallback | **Holds, but riskier than stated.** File Transformation Harmony-patches `Startup.Configure`, so it can break on any *server* point release, not just a web client update. Treat the Dashboard page as the primary path. |
| "Declare the dependency" on File Transformation | **Not possible.** 10.11 has no plugin dependency mechanism; `dependencies` in a repository manifest is silently discarded. Runtime detection plus install docs are the only options. See section 13. |
| Permissions: admin only on every endpoint and on the menu item | **Reconsider.** `Policies.SubtitleManagement` exists in 10.11 and is what Jellyfin uses to gate its own "Edit subtitles" affordance. `RequiresElevation` is right for the ffmpeg and file-write endpoints; `SubtitleManagement` matches the audience for the menu item. Needs an explicit decision. |
| Output `<base>.<lang>.synced.srt` | **Confirmed safe.** `synced` is not a default/forced/HI flag and does not resolve as a language, so it becomes the stream `Title` and displays as `synced - English - SRT - External`. See section 9. |
| `GET /SubtitleSync/Subtitle/{id}` - proxy Jellyfin or call `ISubtitleEncoder`? | **Call `ISubtitleEncoder` directly.** It is a DI singleton, handles external and embedded uniformly, and needs no auth round-trip. See section 7. |
| Server ffmpeg to 16 kHz mono s16le | **Confirmed viable** via `IMediaEncoder.EncoderPath` and `Process`. Critical detail: drain stdout **and** stderr concurrently or ffmpeg deadlocks on large PCM output. See section 6. |
| Server-side signal cache | **Do not use `BasePlugin.DataFolderPath`** - it is the install directory and is wiped on plugin update. Use `Path.Join(IApplicationPaths.DataPath, "subtitlesync")`. See section 10. |
| Routes like `/SubtitleSync/Item/{id}` | **Confirmed.** Controllers are auto-discovered from the plugin assembly; `[Route("SubtitleSync")]` on a `ControllerBase` gives exactly these paths. See section 3. |
| Repository manifest at a fixed URL, MD5 checksum | **Confirmed.** `targetAbi` `"10.11.0.0"`, MD5 hex (either case), ISO 8601 UTC timestamp. See section 13. |
| Bundling `lib/` as an embedded C# resource served to the browser | **Confirmed mechanism** - `IHasWebPages.GetPages()` returning extra `PluginPageInfo` entries for `.js` resources, as OpenSubtitles does. See section 2. |

New risk not in the epic: **`Newtonsoft.Json` is a hard build dependency** if we register the
transformation via `PluginInterface.RegisterTransformation`, because its parameter is a
`Newtonsoft.Json.Linq.JObject`. See section 11.
