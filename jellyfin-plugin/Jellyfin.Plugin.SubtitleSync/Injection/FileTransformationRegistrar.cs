using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SubtitleSync.Injection;

/// <summary>
/// Asks the File Transformation plugin to run
/// <see cref="IndexHtmlTransformation.IndexHtml"/> over the web client's
/// <c>index.html</c>.
/// </summary>
/// <remarks>
/// <para>
/// Detection is done twice over, on purpose, because the two questions have
/// different answers and an administrator needs both. <see cref="IPluginManager"/>
/// says whether File Transformation is <i>installed and active</i>, which is
/// what the configuration page reports. Reflection over the loaded assemblies
/// says whether its API is <i>actually callable</i>, which is what decides
/// whether we try. Without the first, a File Transformation release that renamed
/// <c>PluginInterface</c> would be indistinguishable from it never having been
/// installed.
/// </para>
/// <para>
/// Reflection is not laziness here. Jellyfin loads every plugin into its own
/// <see cref="AssemblyLoadContext"/>, so a compile-time reference to File
/// Transformation would produce a different type identity from the one the
/// server has loaded and the call would fail at runtime. Every dependent plugin
/// in File Transformation's own ecosystem does exactly this.
/// </para>
/// </remarks>
public sealed partial class FileTransformationRegistrar
{
    /// <summary>
    /// Our transformation's own id, distinct from the plugin id. File
    /// Transformation keys its registration store on this, so it must be stable
    /// across restarts and unique to us.
    /// </summary>
    private static readonly Guid _transformationId = new("2b7f2f61-6e28-4a2f-9b7a-4d1c0e0b6f21");

    private readonly IPluginManager _pluginManager;
    private readonly ILogger<FileTransformationRegistrar> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationRegistrar"/> class.
    /// </summary>
    /// <param name="pluginManager">Used to tell "not installed" from "installed but changed".</param>
    /// <param name="logger">Where the outcome is recorded.</param>
    public FileTransformationRegistrar(
        IPluginManager pluginManager,
        ILogger<FileTransformationRegistrar> logger)
    {
        _pluginManager = pluginManager;
        _logger = logger;
    }

    /// <summary>
    /// Runs detection and, if everything is in place, registers the
    /// transformation.
    /// </summary>
    /// <returns>What happened, ready to be published on the configuration page.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every failure mode here belongs to a third-party plugin we reach by reflection: a missing type, a changed signature, a null Instance inside its own static entry point. None of them is worth taking the server down for, and all of them mean exactly one thing to the user - the Subtitles menu item will not appear.")]
    public InjectionState Register()
    {
        var installed = FindInstalledPlugin();
        var state = new InjectionState
        {
            FileTransformationVersion = installed?.Version?.ToString(),
        };

        if (installed is null)
        {
            state.Availability = InjectionAvailability.NotInstalled;
            state.Detail = FileTransformationFacts.PluginName
                + " is not installed, so the Subtitles menu item will not appear. "
                + "The sync page is still reachable from Dashboard > Plugins.";
            LogNotInstalled(FileTransformationFacts.PluginName);
            return state;
        }

        if (!IndexHtmlTransformation.HasScript())
        {
            state.Availability = InjectionAvailability.RegistrationFailed;
            state.Detail = "This build of Subtitle Sync is missing its injected script resource.";
            LogScriptResourceMissing(
                IndexHtmlTransformation.ScriptResourceName(),
                string.Join(", ", IndexHtmlTransformation.ResourceNames()));
            return state;
        }

        var register = FindRegisterMethod();
        if (register is null)
        {
            state.Availability = InjectionAvailability.Incompatible;
            state.Detail = FileTransformationFacts.PluginName + " "
                + (state.FileTransformationVersion ?? "?")
                + " is installed but its "
                + FileTransformationFacts.RegisterMethodName
                + " entry point was not found, so the Subtitles menu item will not appear.";
            LogEntryPointMissing(
                FileTransformationFacts.PluginName,
                state.FileTransformationVersion,
                FileTransformationFacts.PluginInterfaceTypeName,
                FileTransformationFacts.RegisterMethodName);
            return state;
        }

        try
        {
            register.Invoke(null, new object?[] { BuildPayload() });

            state.Availability = InjectionAvailability.Registered;
            LogRegistered(FileTransformationFacts.PluginName, state.FileTransformationVersion);
        }
        catch (Exception exception)
        {
            state.Availability = InjectionAvailability.RegistrationFailed;
            state.Detail = FileTransformationFacts.PluginName
                + " refused the registration, so the Subtitles menu item will not appear.";
            LogRegistrationRejected(
                FileTransformationFacts.PluginName,
                state.FileTransformationVersion,
                exception);
        }

        return state;
    }

    /// <summary>
    /// The payload File Transformation deserialises into its own
    /// <c>TransformationRegistrationPayload</c>.
    /// </summary>
    /// <remarks>
    /// A <see cref="JObject"/> because <c>RegisterTransformation</c> takes one -
    /// which is the whole reason this project references Newtonsoft.Json. The
    /// callback triple is read from the type itself rather than written out as
    /// strings, so renaming the method is a compile error here instead of a
    /// silent no-op at runtime.
    /// </remarks>
    /// <returns>The registration payload.</returns>
    private static JObject BuildPayload()
    {
        var (assembly, className, method) = IndexHtmlTransformation.CallbackTarget();

        return new JObject
        {
            ["id"] = _transformationId.ToString("D", CultureInfo.InvariantCulture),

            // A regex, and it has to survive being matched against the path
            // spelled two different ways. File Transformation asks
            // NeedsTransformation("/index.html") - leading slash, as the static
            // file middleware hands it over - and then RunTransformation with
            // the same path stripped of that slash. So `^index\.html$` matches
            // neither call reliably and a bare `index.html` matches far too
            // much. This anchors the end and allows either spelling of the
            // start.
            ["fileNamePattern"] = "(^|/)index\\.html$",
            ["callbackAssembly"] = assembly,
            ["callbackClass"] = className,
            ["callbackMethod"] = method,
        };
    }

    /// <summary>
    /// The loaded File Transformation plugin, if the server has one active.
    /// </summary>
    /// <returns>The loaded plugin, or null.</returns>
    private LocalPlugin? FindInstalledPlugin() =>
        _pluginManager.Plugins.FirstOrDefault(
            p => p.Id.Equals(FileTransformationFacts.PluginId)
                 && p.Manifest?.Status == PluginStatus.Active);

    /// <summary>
    /// <c>PluginInterface.RegisterTransformation</c> as the running server has
    /// it loaded, or null if it is not where we expect.
    /// </summary>
    /// <returns>The method, or null.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Walking every AssemblyLoadContext touches assemblies loaded by other plugins; a reflection-only or otherwise unloadable one throws on enumeration. That is not our failure to report.")]
    private MethodInfo? FindRegisterMethod()
    {
        try
        {
            var assembly = AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(a =>
                    a.FullName?.Contains(FileTransformationFacts.AssemblyMarker, StringComparison.Ordinal) == true);

            return assembly
                ?.GetType(FileTransformationFacts.PluginInterfaceTypeName)
                ?.GetMethod(
                    FileTransformationFacts.RegisterMethodName,
                    BindingFlags.Public | BindingFlags.Static);
        }
        catch (Exception exception)
        {
            LogProbeFailed(FileTransformationFacts.PluginName, exception);
            return null;
        }
    }

    /// <summary>
    /// Logs the ordinary, expected case: nobody installed File Transformation.
    /// </summary>
    /// <remarks>
    /// Information, not a warning. The Dashboard route to the sync page is the
    /// primary one by design, and an administrator who never wanted the menu
    /// item should not find a warning in their log every restart.
    /// <para>
    /// Source-generated rather than a plain <c>LogInformation</c> call because
    /// the project builds with <c>AnalysisMode=AllEnabledByDefault</c>, and
    /// CA1848 wants the allocation-free delegate form.
    /// </para>
    /// </remarks>
    /// <param name="plugin">The plugin's display name.</param>
    [LoggerMessage(
        EventId = 7601,
        Level = LogLevel.Information,
        Message = "{Plugin} is not installed, so the Subtitles menu item is disabled. The sync page is still reachable from Dashboard > Plugins.")]
    private partial void LogNotInstalled(string plugin);

    /// <summary>
    /// Logs a build of this plugin that lost its embedded script.
    /// </summary>
    /// <param name="resource">The resource that should have been there.</param>
    /// <param name="present">Every resource that actually is.</param>
    [LoggerMessage(
        EventId = 7602,
        Level = LogLevel.Error,
        Message = "The embedded resource {Resource} is missing from this build, so no script would be injected. Resources present: {Present}")]
    private partial void LogScriptResourceMissing(string resource, string present);

    /// <summary>
    /// Logs the version-mismatch case, which is the one worth telling apart.
    /// </summary>
    /// <param name="plugin">The plugin's display name.</param>
    /// <param name="version">The version that is loaded.</param>
    /// <param name="type">The type we looked for.</param>
    /// <param name="method">The method we looked for.</param>
    [LoggerMessage(
        EventId = 7603,
        Level = LogLevel.Warning,
        Message = "{Plugin} {Version} is loaded but {Type}.{Method} could not be resolved. This is a version mismatch, not a missing install; the Subtitles menu item will not appear.")]
    private partial void LogEntryPointMissing(string plugin, string? version, string type, string method);

    /// <summary>
    /// Logs the success case, so an administrator can confirm from the log
    /// alone that the menu item should be there.
    /// </summary>
    /// <param name="plugin">The plugin's display name.</param>
    /// <param name="version">The version that is loaded.</param>
    [LoggerMessage(
        EventId = 7604,
        Level = LogLevel.Information,
        Message = "Registered the index.html transformation with {Plugin} {Version}. The Subtitles menu item will appear for administrators.")]
    private partial void LogRegistered(string plugin, string? version);

    /// <summary>
    /// Logs a registration the other plugin threw out.
    /// </summary>
    /// <param name="plugin">The plugin's display name.</param>
    /// <param name="version">The version that is loaded.</param>
    /// <param name="exception">What it threw.</param>
    [LoggerMessage(
        EventId = 7605,
        Level = LogLevel.Error,
        Message = "{Plugin} {Version} rejected the transformation registration, so the Subtitles menu item will not appear.")]
    private partial void LogRegistrationRejected(string plugin, string? version, Exception exception);

    /// <summary>
    /// Logs a failure while walking the load contexts.
    /// </summary>
    /// <param name="plugin">The plugin's display name.</param>
    /// <param name="exception">What went wrong.</param>
    [LoggerMessage(
        EventId = 7606,
        Level = LogLevel.Debug,
        Message = "Probing the loaded assemblies for {Plugin} failed.")]
    private partial void LogProbeFailed(string plugin, Exception exception);
}
