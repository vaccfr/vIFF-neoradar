using System;
using System.Collections.Generic;
using System.Linq;
using NeoRadar.PluginSdk;
using NeoRadar.PluginSdk.Airport;
using NeoRadar.PluginSdk.Chat;
using NeoRadar.PluginSdk.ControllerData;
using NeoRadar.PluginSdk.Fsd;
using NeoRadar.PluginSdk.Logger;
using NeoRadar.PluginSdk.Menu;
using NeoRadar.PluginSdk.Settings;
using NeoRadar.PluginSdk.Tag;
using NeoRadar.PluginSdk.Timer;
using NeoRadar.PluginSdk.Window;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Commands;
using Viff.NeoRadar.Common;
using Viff.NeoRadar.Panel;
using Viff.NeoRadar.Tags;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar;

/// <summary>
/// The vIFF CDM plugin: A-CDM and ATFCM departure management for NeoRadar, backed by the vIFF system.
/// </summary>
public sealed class ViffCdmPlugin : BasePlugin
{
    private const string PluginVersion = "0.1.0-alpha";
    private const string DefaultApiUrl = "https://viff-system.network/";

    // The placeholder key in the open-source EuroScope CDM plugin: vIFF reads accept it, writes refuse it (401), so
    // a controller who writes sets the real key with .cdm apikey.
    private const string DefaultApiKey = "TEST";
    private const int DefaultRefreshSeconds = 15;

    private ILoggerApi? _logger;
    private IControllerDataApi? _controllerData;
    private IChatApi? _chat;
    private IMenuApi? _menu;
    private JobScheduler? _scheduler;
    private JobScheduler.PeriodicJob? _syncJob;
    private ViffApiClient? _api;
    private CdmStore? _store;
    private TagMenus? _tagMenus;
    private MasterRole? _master;
    private AirportPanel? _panel;
    private string _commandId = string.Empty;
    private string _panelMenuId = string.Empty;

    public override PluginManifest Manifest { get; } = new(
        Name: "vIFF CDM",
        Author: "vIFF-neoradar contributors",
        Version: PluginVersion,
        SdkMajorVersion: PluginSdkVersion.Major,
        Description: "A-CDM and ATFCM departure management backed by vIFF: TOBT, TSAT, TTOT, CTOT, RDY and the CDM master role",
        Website: "https://github.com/vaccfr/vIFF-neoradar");

    public override void Initialize(PluginManifest metadata, ICoreApi coreApi, ClientInformation clientInfo)
    {
        _logger = coreApi.Logger;
        try
        {
            Compose(coreApi);
        }
        catch (Exception ex)
        {
            coreApi.Logger.Error($"Could not start the vIFF CDM plugin: {ex}");
        }
    }

    public override void Shutdown()
    {
        Guard(nameof(Shutdown), () =>
        {
            _scheduler?.Stop(TimeSpan.FromSeconds(2));
            _master?.ReleaseAll(TimeSpan.FromSeconds(2));
            _panel?.Close();
            if (_commandId.Length > 0)
            {
                _chat?.UnregisterCommand(_commandId);
            }

            if (_panelMenuId.Length > 0)
            {
                _menu?.RemoveMenuItem(_panelMenuId);
            }

            _api?.Dispose();
        });

        _scheduler = null;
        _syncJob = null;
        _api = null;
        _store = null;
        _tagMenus = null;
        _master = null;
        _panel = null;
    }

    public override void OnTimer(TimerEvent evt) => Guard(nameof(OnTimer), () => _scheduler?.Tick());

    public override void OnTagAction(TagActionEvent evt) => Guard(nameof(OnTagAction), () => _tagMenus?.OnTagAction(evt));

    public override bool OnTagShowDropdown(TagShowDropdownEvent evt)
    {
        bool show = false;
        Guard(nameof(OnTagShowDropdown), () => show = _tagMenus?.OnTagShowDropdown(evt) ?? false);
        return show;
    }

    public override void OnTagDropdownAction(DropdownActionEvent evt) => Guard(nameof(OnTagDropdownAction), () => _tagMenus?.OnTagDropdownAction(evt));

    public override void OnWindowEvent(WindowEvent evt) => Guard(nameof(OnWindowEvent), () => _panel?.OnWindowEvent(evt));

    public override void OnMenuItemClicked(MenuItemClickedEvent evt) => Guard(nameof(OnMenuItemClicked), () =>
    {
        if (evt.ItemId == _panelMenuId)
        {
            _panel?.Open();
        }
    });

    public override void OnFsdConnected(FsdConnectedEvent evt) => Guard(nameof(OnFsdConnected), () =>
    {
        _master?.NoteConnected();
        RequestSync();
    });

    public override void OnFsdDisconnected(FsdDisconnectedEvent evt) => Guard(nameof(OnFsdDisconnected), () =>
    {
        MasterRole? master = _master;
        _scheduler?.RunOnce("release on disconnect", _ =>
        {
            master?.ReleaseAll(TimeSpan.FromSeconds(5));
            return System.Threading.Tasks.Task.CompletedTask;
        });
    });

    public override void OnAirportAdded(AirportAddedEvent evt) => Guard(nameof(OnAirportAdded), RequestSync);

    public override void OnAirportRemoved(AirportRemovedEvent evt) => Guard(nameof(OnAirportRemoved), RequestSync);

    public override void OnControllerDataUpdated(ControllerDataUpdatedEvent evt) => Guard(nameof(OnControllerDataUpdated), () =>
    {
        if (_store?.Find(evt.Callsign) is null || _controllerData is null)
        {
            return;
        }

        ControllerDataModel? data = _controllerData.GetByCallsign(evt.Callsign);
        _store.ObserveStartUp(evt.Callsign, data is not null && data.GroundStatus != GroundStatus.None, DateTimeOffset.UtcNow);
    });

    private void Compose(ICoreApi core)
    {
        TimeProvider clock = TimeProvider.System;
        ISettingsApi settings = core.Settings;
        _controllerData = core.ControllerData;
        _chat = core.Chat;
        _menu = core.Menu;

        Uri apiUrl = ReadApiUrl(settings.Get("apiUrl"));
        string apiKey = settings.Get("apiKey") is { Length: > 0 } key ? key : DefaultApiKey;
        int refreshSeconds = int.TryParse(settings.Get("refreshSeconds"), out int seconds) && seconds >= 5 ? seconds : DefaultRefreshSeconds;

        JobScheduler scheduler = new(clock, core.Logger);
        ViffApiClient api = new(apiUrl, apiKey, $"vIFF-neoradar/{PluginVersion}", core.Logger);
        Notifier notifier = new(core.Chat, core.Feedback);
        ConnectionGate gate = new(core.Fsd);
        CdmStore store = new();
        AirportControls controls = new(settings);
        MasterSequencing sequencing = new(api, store, controls, core.Logger, message => notifier.Say(message));
        CdmSync sync = new(api, store, controls, sequencing, gate, core.Airport, core.Flightplan, core.ControllerData, clock);
        TagPublisher publisher = new(core.Tag.GetInterface(), core.Expressions, core.Logger);

        void Changed()
        {
            publisher.Publish(AllFlights(store, sync), clock.GetUtcNow());
            _panel?.Refresh();
            RequestSync();
        }

        FlightActions actions = new(api, store, gate, scheduler, notifier, clock, Changed);
        MasterRole master = new(api, store, sync, gate, scheduler, notifier, Changed);
        AirportPanel panel = new(core.Window.GetInterface(), store, sync, controls, master, gate, notifier);
        TagMenus tagMenus = new(core.Tag.GetInterface(), store, controls, actions, core.Logger);
        CdmCommand command = new(store, sync, controls, master, panel, gate, notifier, clock, RequestSync, key =>
        {
            if (key is null)
            {
                settings.Remove("apiKey");
            }
            else
            {
                settings.Set("apiKey", key);
            }

            api.SetApiKey(key ?? DefaultApiKey);
        });

        publisher.Register();
        tagMenus.Register();
        _commandId = core.Chat.RegisterCommand("cdm", CdmCommand.Definition, command);
        if (_commandId.Length == 0)
        {
            core.Logger.Error("The .cdm command was refused; another plugin may have registered it");
        }

        _panelMenuId = core.Menu.RegisterMenuItem(new MenuItemDefinition("Airport Panel", "CDM Airport Panel"));

        sync.Synced += () =>
        {
            master.NoteConnected();
            publisher.Publish(AllFlights(store, sync), clock.GetUtcNow());
            panel.Refresh();
        };

        _scheduler = scheduler;
        _api = api;
        _store = store;
        _tagMenus = tagMenus;
        _master = master;
        _panel = panel;
        _syncJob = scheduler.Every("sync", TimeSpan.FromSeconds(refreshSeconds), sync.RunAsync);
        scheduler.Every("tags", TimeSpan.FromSeconds(1), _ =>
        {
            publisher.Publish(AllFlights(store, sync), clock.GetUtcNow());
            return System.Threading.Tasks.Task.CompletedTask;
        });

        core.Logger.Info($"vIFF CDM {PluginVersion} reading {apiUrl} every {refreshSeconds} s");
    }

    private static IReadOnlyList<FlightView> AllFlights(CdmStore store, CdmSync sync) =>
        store.Flights().Concat(store.NetworkFlights(sync.LatestTraffic)).ToList();

    private void RequestSync()
    {
        JobScheduler? scheduler = _scheduler;
        JobScheduler.PeriodicJob? job = _syncJob;
        if (scheduler is not null && job is not null)
        {
            scheduler.TriggerSoon(job);
        }
    }

    private Uri ReadApiUrl(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) &&
            Uri.TryCreate(configured.EndsWith('/') ? configured : configured + "/", UriKind.Absolute, out Uri? uri) &&
            uri.Scheme == Uri.UriSchemeHttps)
        {
            return uri;
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            _logger?.Warning($"Ignoring the apiUrl setting '{configured}'; it must be an https URL");
        }

        return new Uri(DefaultApiUrl);
    }

    private void Guard(string handler, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger?.Error($"[{handler}] {ex}");
        }
    }
}
