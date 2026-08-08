using System;
using System.Security.Claims;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SubtitleSync.Api;

/// <summary>
/// The one way this plugin turns a route's item id into an item.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared because a second copy of this rule is a permission bypass.</b> The
/// lookup has to go through the user-scoped overload, which is the difference
/// between "you cannot see it" answering 404 and an endpoint becoming a way to
/// read out of a library the caller has no access to. That rule was written
/// three times and one of the copies - <see cref="PcmController"/>, the endpoint
/// that returns decoded audio rather than metadata - used the unscoped overload
/// instead. A user with <c>EnableSubtitleManagement</c> and zero library access,
/// for whom <c>GET /Items</c> returns nothing, got 404 from the metadata
/// endpoints and 200 with the whole soundtrack from that one. Every controller
/// now calls this, and <c>ItemLookupUsageTests</c> fails the build if a new one
/// reaches for <c>ILibraryManager</c> directly.
/// </para>
/// <para>
/// <b>An API key has no user</b>, so <see cref="UserIdFrom"/> yields
/// <see cref="Guid.Empty"/> and the unscoped overload is the deliberate
/// fallback: an API key is a server-level credential and there is no user whose
/// visibility it could be scoped to.
/// </para>
/// </remarks>
internal static class ItemLookup
{
    /// <summary>
    /// The claim the server puts the authenticated user's id in.
    /// <c>Jellyfin.Api.Constants.InternalClaimTypes.UserId</c> at v10.11.11.
    /// Spelled out because <c>Jellyfin.Api</c> is not a package a plugin can
    /// reference, so neither the constant nor <c>User.GetUserId()</c> is
    /// reachable from here.
    /// </summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    /// <summary>
    /// Reads the authenticated user id out of the request claims.
    /// </summary>
    /// <param name="user">The request principal.</param>
    /// <returns>The user id, or <see cref="Guid.Empty"/> for an API key.</returns>
    public static Guid UserIdFrom(ClaimsPrincipal? user)
    {
        var value = user?.FindFirst(UserIdClaim)?.Value;

        return Guid.TryParse(value, out var userId) ? userId : Guid.Empty;
    }

    /// <summary>
    /// Resolves an item id against what the caller is allowed to see.
    /// </summary>
    /// <param name="libraryManager">The library.</param>
    /// <param name="user">The request principal.</param>
    /// <param name="itemId">The item id from the route.</param>
    /// <param name="onUnresolvable">
    /// Called with the id and the repository failure when a row exists but
    /// cannot be turned back into an item. Each controller passes its own
    /// source-generated logger delegate.
    /// </param>
    /// <returns>The item, or <see langword="null"/> if it does not exist, cannot
    /// be seen by this caller, or cannot be deserialised.</returns>
    public static BaseItem? Find(
        ILibraryManager libraryManager,
        ClaimsPrincipal? user,
        Guid itemId,
        Action<Guid, Exception> onUnresolvable)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);

        return Resolve<BaseItem>(
            itemId,
            UserIdFrom(user),
            id => libraryManager.GetItemById<BaseItem>(id),
            (id, userId) => libraryManager.GetItemById<BaseItem>(id, userId),
            onUnresolvable);
    }

    /// <summary>
    /// The decision itself, with the library reached through delegates so the
    /// rule can be tested without a running server.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="itemId">The item id from the route.</param>
    /// <param name="userId">The caller, or <see cref="Guid.Empty"/> for an API key.</param>
    /// <param name="unscoped">The lookup that ignores visibility.</param>
    /// <param name="scoped">The lookup that honours it.</param>
    /// <param name="onUnresolvable">Reports an undeserialisable row.</param>
    /// <returns>The item, or <see langword="null"/>.</returns>
    internal static T? Resolve<T>(
        Guid itemId,
        Guid userId,
        Func<Guid, T?> unscoped,
        Func<Guid, Guid, T?> scoped,
        Action<Guid, Exception> onUnresolvable)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(unscoped);
        ArgumentNullException.ThrowIfNull(scoped);
        ArgumentNullException.ThrowIfNull(onUnresolvable);

        // Guarded before the lookup rather than after: the all-zero id is not a
        // library row, and asking the repository about it is a 400 from the
        // exception middleware rather than the 404 every other absent id gets.
        if (itemId.Equals(Guid.Empty))
        {
            return null;
        }

        try
        {
            return userId.Equals(Guid.Empty)
                ? unscoped(itemId)
                : scoped(itemId, userId);
        }
        catch (InvalidOperationException ex)
        {
            // Observed against 10.11.11: a row whose stored type the server can
            // no longer resolve - left behind by an uninstalled plugin, or an
            // internal row such as the all-zeros-but-one id - throws "Cannot
            // deserialize unknown type" out of BaseItemRepository rather than
            // returning null. Jellyfin's own /Items/{id} answers 500 for the
            // same id. From the caller's point of view the item is not there,
            // so say that instead of leaking a stack trace.
            onUnresolvable(itemId, ex);
            return null;
        }
    }
}
