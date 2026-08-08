using System;
using System.Linq;
using Jellyfin.Plugin.SubtitleSync.Injection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.Injection;

/// <summary>
/// Tests for the callback File Transformation invokes on <c>index.html</c>.
/// </summary>
/// <remarks>
/// The point of these is the failure modes, not the happy path. This method runs
/// inside a third-party plugin's file-serving path, its return value is cast to
/// <c>string</c> without a null check, and the stream it is written back to is
/// not truncated first. Each of those is a way to break the entire web client
/// from code that is only trying to add a menu item.
/// </remarks>
public class IndexHtmlTransformationTests
{
    private const string Document =
        "<!doctype html><html><head><title>Jellyfin</title></head><body dir=\"ltr\">"
        + "<div id=\"reactRoot\"></div></body></html>";

    [Fact]
    public void Inject_PutsTheScriptInsideTheBody()
    {
        var result = IndexHtmlTransformation.Inject(Document, "console.log(1)");

        Assert.Contains("console.log(1)", result, StringComparison.Ordinal);
        Assert.True(
            result.IndexOf("console.log(1)", StringComparison.Ordinal)
            < result.IndexOf("</body>", StringComparison.Ordinal),
            "the script must land inside <body>, not after it");
    }

    /// <summary>
    /// File Transformation writes the result over the original stream from
    /// offset zero WITHOUT truncating it. A result shorter than the input would
    /// leave the tail of the old file behind and serve a corrupt document.
    /// </summary>
    [Fact]
    public void Inject_NeverShortensTheDocument()
    {
        var result = IndexHtmlTransformation.Inject(Document, "console.log(1)");

        Assert.True(result.Length > Document.Length);
    }

    /// <summary>
    /// Nothing promises a transformation is applied once per response.
    /// </summary>
    [Fact]
    public void Inject_IsIdempotent()
    {
        var once = IndexHtmlTransformation.Inject(Document, "console.log(1)");
        var twice = IndexHtmlTransformation.Inject(once, "console.log(1)");

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// A client whose markup we have never seen still gets the script, because
    /// a trailing script tag runs perfectly well and doing nothing would be a
    /// silent failure on exactly the version we most want to know about.
    /// </summary>
    [Fact]
    public void Inject_AppendsWhenThereIsNoBody()
    {
        var result = IndexHtmlTransformation.Inject("<html></html>", "console.log(1)");

        Assert.Contains("console.log(1)", result, StringComparison.Ordinal);
        Assert.StartsWith("<html></html>", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Inject_LeavesTheDocumentAloneWhenThereIsNoScript(string? script)
    {
        Assert.Equal(Document, IndexHtmlTransformation.Inject(Document, script!));
    }

    /// <summary>
    /// The contract with File Transformation: it does
    /// <c>(string)method.Invoke(...)</c>, so returning null is a
    /// NullReferenceException inside someone else's request pipeline.
    /// </summary>
    [Fact]
    public void IndexHtml_ReturnsAStringForANullPayload()
    {
        Assert.NotNull(IndexHtmlTransformation.IndexHtml(null!));
    }

    [Fact]
    public void IndexHtml_ReturnsTheDocumentWithTheScriptInIt()
    {
        var result = IndexHtmlTransformation.IndexHtml(
            new TransformationPayload { Contents = Document });

        Assert.Contains(IndexHtmlTransformation.Marker, result, StringComparison.Ordinal);
        Assert.Contains("</body>", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// The embedded resource name in the C# and the <c>LogicalName</c> in the
    /// csproj have to agree, and nothing but a test can check that they do.
    /// A mismatch would register a transformation that quietly injected nothing.
    /// </summary>
    [Fact]
    public void TheInjectedScriptIsActuallyEmbeddedInTheAssembly()
    {
        Assert.True(
            IndexHtmlTransformation.HasScript(),
            "the bundled script resource "
            + IndexHtmlTransformation.ScriptResourceName()
            + " is missing. Resources present: "
            + string.Join(", ", IndexHtmlTransformation.ResourceNames()));
    }

    /// <summary>
    /// The whole reason this file exists: the bundle is inlined into HTML, so a
    /// literal closing script tag anywhere in it would end the tag early and
    /// spill the rest of the bundle onto the page as text.
    /// </summary>
    [Fact]
    public void TheInjectedScriptCanBeInlinedIntoHtml()
    {
        var result = IndexHtmlTransformation.IndexHtml(
            new TransformationPayload { Contents = Document });

        // Exactly two: the one we opened and the one we closed.
        Assert.Equal(1, Occurrences(result, "<script "));
        Assert.Equal(1, Occurrences(result, "</script>"));
    }

    /// <summary>
    /// The callback triple is what File Transformation reflects on. If any of
    /// the three is wrong the registration succeeds and the callback is never
    /// invoked, which is the hardest failure here to notice.
    /// </summary>
    [Fact]
    public void TheCallbackTargetResolvesBackToTheMethodItNames()
    {
        var (assemblyName, className, methodName) = IndexHtmlTransformation.CallbackTarget();

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.FullName == assemblyName);
        Assert.NotNull(assembly);

        var type = assembly!.GetType(className);
        Assert.NotNull(type);

        var method = type!.GetMethod(methodName);
        Assert.NotNull(method);
        Assert.True(method!.IsStatic, "File Transformation invokes the callback with a null target");
        Assert.Equal(typeof(string), method.ReturnType);
        Assert.Single(method.GetParameters());
    }

    /// <summary>
    /// File Transformation builds <c>{ "contents": ... }</c> as a Newtonsoft
    /// JObject and calls <c>ToObject(parameterType)</c> on it. This asserts the
    /// payload type survives that, which the property name casing could
    /// otherwise break.
    /// </summary>
    [Fact]
    public void ThePayloadTypeDeserialisesFromWhatFileTransformationSends()
    {
        var sent = new JObject { ["contents"] = Document };

        var payload = sent.ToObject<TransformationPayload>();

        Assert.NotNull(payload);
        Assert.Equal(Document, payload!.Contents);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }
}
