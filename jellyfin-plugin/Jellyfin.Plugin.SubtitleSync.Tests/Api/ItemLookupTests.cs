using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using Jellyfin.Plugin.SubtitleSync.Api;
using Xunit;

namespace Jellyfin.Plugin.SubtitleSync.Tests.Api;

/// <summary>
/// Covers the rule that turns a route's item id into an item.
/// </summary>
/// <remarks>
/// <para>
/// These are here because of a real defect: <c>PcmController</c> shipped a
/// third, hand-written copy of this rule that called the unscoped
/// <c>GetItemById</c>, so a user with <c>EnableSubtitleManagement</c> and no
/// library access got 404 from the metadata endpoints and 200 with the whole
/// decoded soundtrack from the one endpoint that returns audio.
/// </para>
/// <para>
/// <see cref="ItemLookup.Resolve{T}"/> takes the library as two delegates for
/// exactly this reason: the decision - which overload, and what an
/// undeserialisable row means - is the part worth testing, and it is the part
/// that needs no running server to test. <see cref="ItemLookupUsesTheSharedRule"/>
/// then covers the other half, which no unit test of the rule can: that every
/// controller actually goes through it.
/// </para>
/// </remarks>
public class ItemLookupTests
{
    private static readonly Guid _item = new("7685f9d9cd1527ddfeb31c0776a45e83");
    private static readonly Guid _user = new("446523870c8a4877a50276a921c65f30");

    [Fact]
    public void AnAuthenticatedUserGetsTheScopedLookupAndNeverTheUnscopedOne()
    {
        var unscopedCalls = 0;
        var scoped = new List<(Guid Item, Guid User)>();

        var found = ItemLookup.Resolve<string>(
            _item,
            _user,
            _ =>
            {
                unscopedCalls++;
                return "leaked";
            },
            (id, userId) =>
            {
                scoped.Add((id, userId));
                return "visible";
            },
            NoUnresolvable);

        Assert.Equal("visible", found);
        Assert.Equal(0, unscopedCalls);
        Assert.Equal([(_item, _user)], scoped);
    }

    [Fact]
    public void AnItemTheUserCannotSeeIsNotFoundRatherThanFetchedUnscoped()
    {
        var unscopedCalls = 0;

        // What the scoped overload does for an item outside the caller's
        // libraries: it returns null rather than throwing, and that null is the
        // answer. Falling back to the unscoped lookup here would reinstate the
        // bypass.
        var found = ItemLookup.Resolve<string>(
            _item,
            _user,
            _ =>
            {
                unscopedCalls++;
                return "leaked";
            },
            (_, _) => null,
            NoUnresolvable);

        Assert.Null(found);
        Assert.Equal(0, unscopedCalls);
    }

    [Fact]
    public void AnApiKeyHasNoUserSoItFallsBackToTheUnscopedLookup()
    {
        var scopedCalls = 0;

        var found = ItemLookup.Resolve<string>(
            _item,
            Guid.Empty,
            _ => "server-wide",
            (_, _) =>
            {
                scopedCalls++;
                return null;
            },
            NoUnresolvable);

        Assert.Equal("server-wide", found);
        Assert.Equal(0, scopedCalls);
    }

    [Fact]
    public void TheAllZeroIdIsRefusedWithoutTouchingTheLibrary()
    {
        // Asking the repository about Guid.Empty is a 400 text/plain from the
        // exception middleware, not the 404 every other absent id gets.
        var found = ItemLookup.Resolve<string>(
            Guid.Empty,
            _user,
            _ => throw new InvalidOperationException("the unscoped lookup was called"),
            (_, _) => throw new InvalidOperationException("the scoped lookup was called"),
            NoUnresolvable);

        Assert.Null(found);
    }

    [Fact]
    public void ARowThatCannotBeDeserialisedIsReportedAndAnsweredAsNotFound()
    {
        // Observed against 10.11.11 for ...0001: BaseItemRepository throws
        // "Cannot deserialize unknown type" instead of returning null, and an
        // endpoint without this catch answers 500 with a stack trace.
        var reported = new List<Guid>();
        var thrown = new InvalidOperationException("Cannot deserialize unknown type.");

        var found = ItemLookup.Resolve<string>(
            _item,
            _user,
            _ => null,
            (_, _) => throw thrown,
            (id, ex) =>
            {
                Assert.Same(thrown, ex);
                reported.Add(id);
            });

        Assert.Null(found);
        Assert.Equal([_item], reported);
    }

    [Fact]
    public void AnUnrelatedFailureIsNotSwallowed()
    {
        Assert.Throws<TimeoutException>(() => ItemLookup.Resolve<string>(
            _item,
            _user,
            _ => null,
            (_, _) => throw new TimeoutException(),
            NoUnresolvable));
    }

    [Theory]
    [InlineData("446523870c8a4877a50276a921c65f30")]
    [InlineData("44652387-0c8a-4877-a502-76a921c65f30")]
    public void TheUserIdComesOutOfTheJellyfinClaim(string value)
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ItemLookup.UserIdClaim, value)]));

        Assert.Equal(_user, ItemLookup.UserIdFrom(principal));
    }

    [Fact]
    public void APrincipalWithoutTheClaimIsTreatedAsAnApiKey()
    {
        Assert.Equal(Guid.Empty, ItemLookup.UserIdFrom(null));
        Assert.Equal(Guid.Empty, ItemLookup.UserIdFrom(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Equal(
            Guid.Empty,
            ItemLookup.UserIdFrom(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ItemLookup.UserIdClaim, "not-a-guid")]))));
    }

    /// <summary>
    /// Fails the build if any controller resolves an item id itself instead of
    /// going through <see cref="ItemLookup"/>.
    /// </summary>
    /// <remarks>
    /// A source scan, in the same spirit as
    /// <see cref="PluginGuidConsistencyTests"/>, because the defect this guards
    /// against is not a wrong answer from a method - it is a call that never
    /// reaches the method. Nothing about the type system stops a new endpoint
    /// from calling <c>ILibraryManager.GetItemById</c> directly, and the
    /// resulting hole is invisible until someone points a restricted account at
    /// it.
    /// </remarks>
    [Fact]
    public void ItemLookupUsesTheSharedRule()
    {
        var apiDirectory = Path.Combine(
            RepositoryRoot(),
            "jellyfin-plugin",
            "Jellyfin.Plugin.SubtitleSync",
            "Api");

        Assert.True(Directory.Exists(apiDirectory), $"{apiDirectory} does not exist");

        var offenders = Directory.EnumerateFiles(apiDirectory, "*.cs")
            .Where(file => !string.Equals(
                Path.GetFileName(file),
                "ItemLookup.cs",
                StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("GetItemById", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These controllers call ILibraryManager.GetItemById directly instead of ItemLookup.Find, "
            + "which is how PcmController came to serve audio from libraries the caller cannot see: "
            + string.Join(", ", offenders));
    }

    private static void NoUnresolvable(Guid itemId, Exception exception)
        => Assert.Fail($"the undeserialisable-row path was taken for {itemId}: {exception}");

    /// <summary>
    /// Walks up from the test assembly until it finds the repository root.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
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
}
