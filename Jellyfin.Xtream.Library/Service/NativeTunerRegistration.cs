// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Adds the native tuner to Jellyfin's Live TV tuner list while Enable Native Tuner is on
/// (GitHub #152).
/// <para>
/// Jellyfin asks every registered tuner type for channels, but it treats Live TV as switched off
/// for every user, and hides the Refresh Guide task, until at least one tuner is listed under
/// Tuner Devices. Registering the tuner type alone is therefore not enough: without a listed
/// tuner nobody sees Live TV.
/// </para>
/// <para>
/// The entry this class adds carries <see cref="FriendlyName"/>, so it can tell its own entry
/// from one a user added by hand. Switching the native tuner off removes only its own.
/// </para>
/// </summary>
public sealed class NativeTunerRegistration : IHostedService, IDisposable
{
    /// <summary>The name shown under Tuner Devices for the entry this class adds.</summary>
    internal const string FriendlyName = "Xtream Library (added by the plugin)";

    private const string LiveTvConfigKey = "livetv";

    private readonly ITunerHostManager _tunerHostManager;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<NativeTunerRegistration> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private EventHandler<BasePluginConfiguration>? _subscribedHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeTunerRegistration"/> class.
    /// </summary>
    /// <param name="tunerHostManager">Jellyfin's tuner host manager.</param>
    /// <param name="serverConfig">The Jellyfin server configuration manager.</param>
    /// <param name="logger">The logger.</param>
    public NativeTunerRegistration(
        ITunerHostManager tunerHostManager,
        IServerConfigurationManager serverConfig,
        ILogger<NativeTunerRegistration> logger)
    {
        _tunerHostManager = tunerHostManager ?? throw new ArgumentNullException(nameof(tunerHostManager));
        _serverConfig = serverConfig ?? throw new ArgumentNullException(nameof(serverConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await SyncSafelyAsync().ConfigureAwait(false);

        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            _subscribedHandler = (_, _) => _ = SyncSafelyAsync();
            plugin.ConfigurationChanged += _subscribedHandler;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (_subscribedHandler is not null && plugin is not null)
        {
            plugin.ConfigurationChanged -= _subscribedHandler;
        }

        _subscribedHandler = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds or removes the plugin's own tuner entry to match the plugin settings.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>A task.</returns>
    internal async Task SyncAsync(PluginConfiguration config)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var tuners = _serverConfig.GetConfiguration<LiveTvOptions>(LiveTvConfigKey).TunerHosts ?? Array.Empty<TunerHostInfo>();
            bool wanted = config.EnableLiveTv && config.EnableNativeTuner;

            if (wanted)
            {
                // Any entry of this type will do, including one added by hand before this existed.
                if (!tuners.Any(t => string.Equals(t.Type, XtreamTunerHost.TunerType, StringComparison.OrdinalIgnoreCase)))
                {
                    await _tunerHostManager.SaveTunerHost(new TunerHostInfo
                    {
                        Type = XtreamTunerHost.TunerType,
                        FriendlyName = FriendlyName,
                    }).ConfigureAwait(false);
                    _logger.LogInformation("Added the Xtream Library native tuner under Live TV Tuner Devices");
                }

                return;
            }

            foreach (var entry in tuners.Where(IsOwnEntry).ToList())
            {
                _tunerHostManager.DeleteTunerHost(entry.Id);
                _logger.LogInformation("Removed the Xtream Library native tuner from Live TV Tuner Devices, since Native Tuner is off");
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _sync.Dispose();

    private static bool IsOwnEntry(TunerHostInfo info)
        => string.Equals(info.Type, XtreamTunerHost.TunerType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(info.FriendlyName, FriendlyName, StringComparison.Ordinal);

    private async Task SyncSafelyAsync()
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config is not null)
            {
                await SyncAsync(config).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Never let this stop the server from starting or a settings save from completing;
            // adding the tuner by hand under Tuner Devices still works.
            _logger.LogWarning(ex, "Could not update the Xtream Library entry under Live TV Tuner Devices");
        }
    }
}
