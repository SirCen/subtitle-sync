using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.SubtitleSync.Injection;

/// <summary>
/// Registers the <c>index.html</c> transformation once, as the server starts.
/// </summary>
/// <remarks>
/// <para>
/// File Transformation keeps its registrations in memory, so this has to happen
/// on every start rather than once ever.
/// </para>
/// <para>
/// An <see cref="IHostedService"/> rather than a scheduled task with a startup
/// trigger, which is what File Transformation's own dependants use. Two reasons.
/// It runs before Kestrel starts serving, which closes the window in which a
/// browser could fetch an untransformed <c>index.html</c> and get a client with
/// no menu item until its next reload. And a scheduled task would appear under
/// Dashboard &gt; Scheduled Tasks as something an administrator could run, pause
/// or misread as part of the sync workflow, which it is not.
/// </para>
/// </remarks>
public sealed class InjectionStartupService : IHostedService
{
    private readonly FileTransformationRegistrar _registrar;
    private readonly InjectionState _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="InjectionStartupService"/> class.
    /// </summary>
    /// <param name="registrar">Does the detection and the registration.</param>
    /// <param name="state">The shared record the configuration page reads.</param>
    public InjectionStartupService(FileTransformationRegistrar registrar, InjectionState state)
    {
        _registrar = registrar;
        _state = state;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var result = _registrar.Register();

        _state.Availability = result.Availability;
        _state.FileTransformationVersion = result.FileTransformationVersion;
        _state.Detail = result.Detail;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
