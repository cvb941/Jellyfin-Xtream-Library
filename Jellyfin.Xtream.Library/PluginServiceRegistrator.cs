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

using System.Linq;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library;

/// <summary>
/// Registers services for the Xtream Library plugin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient<IXtreamClient, XtreamClient>();
        serviceCollection.AddHttpClient<IDispatcharrClient, DispatcharrClient>();
        serviceCollection.AddSingleton<MetadataCache>();
        serviceCollection.AddSingleton<IMetadataLookupService, MetadataLookupService>();
        serviceCollection.AddSingleton<SnapshotService>();
        serviceCollection.AddSingleton<StrmSyncService>();
        serviceCollection.AddSingleton<LiveTvService>();
        serviceCollection.AddSingleton<ITunerHost, XtreamTunerHost>();
        serviceCollection.AddSingleton<CatchupEpgCache>();

        // Registered whatever the settings say, and gated at runtime in IsEnabledFor. Skipping the
        // registration instead would have Jellyfin delete the channel and everything under it on
        // the next scan, taking watched state with it (GitHub #108).
        serviceCollection.AddSingleton<IChannel, XtreamCatchupChannel>();
        serviceCollection.AddSingleton<IScheduledTask, SyncLibraryTask>();
        serviceCollection.AddHostedService<Service.BetaChannelManager>();
        serviceCollection.AddHostedService<Service.NativeTunerRegistration>();
        serviceCollection.AddHostedService<Service.NativeTunerWarmup>();

        DecorateTrickplayManager(serviceCollection);
    }

    private static void DecorateTrickplayManager(IServiceCollection serviceCollection)
    {
        var existing = serviceCollection.LastOrDefault(d => d.ServiceType == typeof(ITrickplayManager));
        if (existing is null)
        {
            return;
        }

        serviceCollection.Remove(existing);

        if (existing.ImplementationType is not null)
        {
            serviceCollection.Add(new ServiceDescriptor(existing.ImplementationType, existing.ImplementationType, existing.Lifetime));
        }

        serviceCollection.Add(new ServiceDescriptor(
            typeof(ITrickplayManager),
            sp =>
            {
                ITrickplayManager inner;
                if (existing.ImplementationInstance is ITrickplayManager instance)
                {
                    inner = instance;
                }
                else if (existing.ImplementationFactory is not null)
                {
                    inner = (ITrickplayManager)existing.ImplementationFactory(sp);
                }
                else
                {
                    inner = (ITrickplayManager)sp.GetRequiredService(existing.ImplementationType!);
                }

                return new CatchupTrickplayManager(
                    inner,
                    sp.GetRequiredService<ILibraryManager>(),
                    sp.GetRequiredService<IMediaEncoder>(),
                    sp.GetRequiredService<IApplicationPaths>(),
                    sp.GetRequiredService<CatchupEpgCache>(),
                    sp.GetRequiredService<ILogger<CatchupTrickplayManager>>());
            },
            existing.Lifetime));
    }
}
