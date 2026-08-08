using System;
using System.IO;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.SubtitleSync.Injection;

/// <summary>
/// The shape File Transformation hands our callback.
/// </summary>
/// <remarks>
/// It builds <c>{ "contents": "&lt;the file&gt;" }</c> as a
/// <c>Newtonsoft.Json.Linq.JObject</c> and calls
/// <c>obj.ToObject(parameterType)</c>, so this type has to be deserialisable by
/// Newtonsoft rather than by <c>System.Text.Json</c>. The attribute is belt and
/// braces - Json.NET matches names case-insensitively anyway - but it is the
/// only thing tying this property to the wire name, so it is written down.
/// </remarks>
public sealed class TransformationPayload
{
    /// <summary>
    /// Gets or sets the current contents of the file being served.
    /// </summary>
    [JsonProperty("contents")]
    public string Contents { get; set; } = string.Empty;
}

/// <summary>
/// Inlines the Subtitles-menu script into the web client's <c>index.html</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the callback File Transformation invokes by reflection. Three
/// consequences shape everything below.
/// </para>
/// <para>
/// <b>It must never throw.</b> The plugin casts our return value with
/// <c>(string)method.Invoke(...)</c> while serving an HTTP request for
/// <c>index.html</c>. An exception here does not produce a missing button, it
/// produces a web client that will not load. Every path returns the original
/// contents on any failure.
/// </para>
/// <para>
/// <b>It must never return a shorter string than it was given.</b> File
/// Transformation seeks the stream to zero and writes over it without
/// truncating, so a shorter result would leave the tail of the original file
/// behind. Appending only, as we do, is safe; rewriting is not.
/// </para>
/// <para>
/// <b>It must be idempotent.</b> Nothing promises the transformation is applied
/// exactly once per response, so a document that already carries our marker is
/// returned untouched.
/// </para>
/// <para>
/// <c>index.html</c> is the target rather than the chunk that actually builds
/// the menu. On a real 10.11.11 install that chunk is
/// <c>55802.9a5b7bc258c2f90abe5e.chunk.js</c> - a webpack module id and a
/// content hash, both of which change on any client rebuild - and File
/// Transformation only forces <c>Cache-Control: no-cache</c> on
/// <c>index.html</c> and <c>main.jellyfin.bundle.js</c>. Patching minified
/// output behind a browser cache would be brittle twice over.
/// </para>
/// </remarks>
public static class IndexHtmlTransformation
{
    /// <summary>
    /// Manifest resource name of the bundled script, set as
    /// <c>LogicalName</c> in the csproj.
    /// </summary>
    private const string ScriptResource = "Jellyfin.Plugin.SubtitleSync.Web.subtitleSyncInject.js";

    /// <summary>
    /// Attribute stamped on the injected tag. Its presence is how a second
    /// application of the transformation recognises the first.
    /// </summary>
    internal const string Marker = "data-subtitle-sync";

    /// <summary>
    /// Where the tag goes. Anything else on the page has already been requested
    /// by the time the browser reaches it, so this cannot delay first paint.
    /// </summary>
    private const string InsertBefore = "</body>";

    /// <summary>
    /// The bundled script, read from the assembly at most once.
    /// </summary>
    /// <remarks>
    /// <see cref="Lazy{T}"/> rather than a hand-rolled double-checked lock: this
    /// is read on the file-serving path, possibly concurrently, and the
    /// hand-rolled version is exactly the sort of thing that looks right and is
    /// not.
    /// </remarks>
    private static readonly Lazy<string> _script = new(ReadScript);

    /// <summary>
    /// The File Transformation callback. Named in the registration payload, so
    /// renaming it silently disables the menu item.
    /// </summary>
    /// <param name="payload">The file as it stands, supplied by File Transformation.</param>
    /// <returns>The file with our script tag added, or unchanged on any failure.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "This runs inside a third-party plugin's file-serving path by reflection, and its return value is cast unconditionally. Any exception escaping here breaks the web client for every user. Returning the untransformed file costs a menu item; letting it propagate costs Jellyfin.")]
    public static string IndexHtml(TransformationPayload payload)
    {
        var contents = payload?.Contents ?? string.Empty;

        try
        {
            return Inject(contents, _script.Value);
        }
        catch (Exception)
        {
            return contents;
        }
    }

    /// <summary>
    /// Adds the script tag to a document. Pure, so it can be tested without a
    /// Jellyfin server or a File Transformation install.
    /// </summary>
    /// <param name="html">The document as served.</param>
    /// <param name="script">The already-bundled script body.</param>
    /// <returns>The document with the script inlined before <c>&lt;/body&gt;</c>.</returns>
    internal static string Inject(string html, string script)
    {
        if (string.IsNullOrEmpty(html) || string.IsNullOrEmpty(script))
        {
            return html;
        }

        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var tag = "\n<script " + Marker + "=\"1\" type=\"text/javascript\">\n"
            + script
            + "\n</script>\n";

        var at = html.LastIndexOf(InsertBefore, StringComparison.OrdinalIgnoreCase);

        // No </body> means this is not the document we were expecting. Append
        // rather than give up: a trailing script still runs, and the alternative
        // is silently doing nothing on a client whose markup we have not seen.
        return at < 0 ? html + tag : html.Insert(at, tag);
    }

    /// <summary>
    /// Reads the bundled script out of this assembly, once.
    /// </summary>
    /// <returns>The script body, or an empty string if the resource is missing.</returns>
    private static string ReadScript()
    {
        using var stream = typeof(IndexHtmlTransformation).Assembly
            .GetManifestResourceStream(ScriptResource);

        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The values the registration payload has to carry to reach
    /// <see cref="IndexHtml"/>, kept next to the method they describe so a
    /// rename cannot break them silently.
    /// </summary>
    /// <returns>Assembly full name, declaring type name and method name.</returns>
    internal static (string Assembly, string Class, string Method) CallbackTarget()
    {
        var type = typeof(IndexHtmlTransformation);
        return (
            type.Assembly.FullName ?? type.Assembly.GetName().Name ?? string.Empty,
            type.FullName ?? nameof(IndexHtmlTransformation),
            nameof(IndexHtml));
    }

    /// <summary>
    /// Whether the bundled script is actually present in this assembly. A build
    /// that lost the embedded resource would otherwise register a transformation
    /// that does nothing.
    /// </summary>
    /// <returns>True when the resource is embedded and non-empty.</returns>
    internal static bool HasScript()
    {
        try
        {
            return _script.Value.Length > 0;
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException or NotImplementedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Exposed so a test can prove the resource name in this file matches the
    /// <c>LogicalName</c> in the csproj.
    /// </summary>
    /// <returns>The manifest resource name of the bundled script.</returns>
    internal static string ScriptResourceName() => ScriptResource;

    /// <summary>
    /// The names of every manifest resource in this assembly, for diagnostics.
    /// </summary>
    /// <returns>The resource names.</returns>
    internal static string[] ResourceNames() =>
        typeof(IndexHtmlTransformation).Assembly.GetManifestResourceNames();
}
