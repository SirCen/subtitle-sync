namespace Jellyfin.Plugin.SubtitleSync.Injection;

/// <summary>
/// How the attempt to reach the File Transformation plugin turned out.
/// </summary>
/// <remarks>
/// Three outcomes rather than a boolean, because the middle one is the trap.
/// <c>GetMethod(...)?.Invoke(...)</c> returns silently when the method is gone,
/// so a File Transformation release that renames its entry point looks exactly
/// like it never being installed. Naming the states is what lets the
/// configuration page tell an administrator "install this" apart from "the thing
/// you installed no longer fits".
/// </remarks>
public enum InjectionAvailability
{
    /// <summary>Detection has not run yet. The server is still starting.</summary>
    Unknown = 0,

    /// <summary>
    /// No plugin with File Transformation's id is loaded. Expected, and not an
    /// error: the Dashboard route to the sync page is the primary one.
    /// </summary>
    NotInstalled = 1,

    /// <summary>
    /// File Transformation is installed but its API was not where we looked -
    /// no assembly, no <c>PluginInterface</c> type, or no
    /// <c>RegisterTransformation</c> method. Its version and ours disagree.
    /// </summary>
    Incompatible = 2,

    /// <summary>The transformation was registered. The menu item should appear.</summary>
    Registered = 3,

    /// <summary>
    /// The API was found and the call threw. File Transformation is loaded but
    /// something inside it failed, so the menu item will not appear.
    /// </summary>
    RegistrationFailed = 4,
}

/// <summary>
/// The result of registering the index.html transformation, published so the
/// configuration page can say what happened.
/// </summary>
/// <remarks>
/// A mutable singleton rather than a value passed around, because the two things
/// that care about it - the startup service that writes it and the status
/// endpoint that reads it - have no other relationship. It is written exactly
/// once, during startup, and read-only afterwards.
/// </remarks>
public sealed class InjectionState
{
    /// <summary>
    /// Gets or sets the outcome of the registration attempt.
    /// </summary>
    public InjectionAvailability Availability { get; set; }

    /// <summary>
    /// Gets or sets the version of the File Transformation plugin that is
    /// loaded, or <see langword="null"/> if none is.
    /// </summary>
    public string? FileTransformationVersion { get; set; }

    /// <summary>
    /// Gets or sets a one-line explanation of a failure, for the administrator.
    /// <see langword="null"/> when nothing went wrong.
    /// </summary>
    public string? Detail { get; set; }
}
