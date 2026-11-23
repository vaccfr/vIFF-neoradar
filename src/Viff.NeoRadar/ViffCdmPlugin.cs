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
using Viff.NeoRadar.Config;
using Viff.NeoRadar.Panel;
using Viff.NeoRadar.Strips;
using Viff.NeoRadar.Tags;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar;

/// <summary>
/// The vIFF CDM plugin: A-CDM and ATFCM departure management for NeoRadar, backed by the vIFF system.
/// </summary>
public sealed class ViffCdmPlugin : BasePlugin
{
    internal const string PluginVersion = "0.2.0-alpha";
    private const string DefaultApiUrl = "https://viff-system.network/";

    // The placeholder key in the open-source EuroScope CDM plugin: vIFF reads accept it, writes refuse it (401). It
    // applies when the build carries no key (BuildSecrets, from .env) and the controller set none with .cdm apikey.
    private const string PlaceholderApiKey = "TEST";
    private const int DefaultRefreshSeconds = 15;

    private ILoggerApi? _logger;
    private IControllerDataApi? _controllerData;
    private IChatApi? _chat;
    private IMenuApi? _menu;
    private JobScheduler? _scheduler;
    private JobScheduler.PeriodicJob? _syncJob;
    private ViffApiClient? _api;
    private Notifier? _notifier;
    private CdmStore? _store;
    private TagMenus? _tagMenus;
    private MasterRole? _master;
    private AirportPanel? _panel;
    private AtfcmList? _atfcm;
    private StripStore? _strips;
    private RemoteText? _remote;
    private string _commandId = string.Empty;
    private string _panelMenuId = string.Empty;
    private string _atfcmMenuId = string.Empty;

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
            Compose(coreApi, clientInfo);
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
            // Mute first: work still finishing on the pool must not post to a client that is shutting down.
            _notifier?.Mute();
            _scheduler?.Stop(TimeSpan.FromSeconds(2));
            _master?.ReleaseAll(TimeSpan.FromSeconds(2));
            _panel?.Close();
            _atfcm?.Close();
            if (_commandId.Length > 0)
            {
                _chat?.UnregisterCommand(_commandId);
            }

            if (_panelMenuId.Length > 0)
            {
                _menu?.RemoveMenuItem(_panelMenuId);
            }

            if (_atfcmMenuId.Length > 0)
            {
                _menu?.RemoveMenuItem(_atfcmMenuId);
            }

            _api?.Dispose();
            _remote?.Dispose();
        });

        _scheduler = null;
        _syncJob = null;
        _api = null;
        _notifier = null;
        _store = null;
        _tagMenus = null;
        _master = null;
        _panel = null;
        _atfcm = null;
        _strips = null;
        _remote = null;
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

    public override void OnWindowEvent(WindowEvent evt) => Guard(nameof(OnWindowEvent), () =>
    {
        _panel?.OnWindowEvent(evt);
        _atfcm?.OnWindowEvent(evt);
    });

    public override void OnMenuItemClicked(MenuItemClickedEvent evt) => Guard(nameof(OnMenuItemClicked), () =>
    {
        if (evt.ItemId == _panelMenuId)
        {
            _panel?.Open();
        }
        else if (evt.ItemId == _atfcmMenuId)
        {
            _atfcm?.Open();
        }
    });

    public override void OnFlightStripAnnotationsChanged(FlightStripAnnotationsChangedEvent evt) =>
        Guard(nameof(OnFlightStripAnnotationsChanged), () => RefreshStrip(evt.Callsign));

    public override void OnFlightStripPushed(FlightStripPushedEvent evt) =>
        Guard(nameof(OnFlightStripPushed), () => RefreshStrip(evt.Callsign));

    public override void OnFsdConnected(FsdConnectedEvent evt) => Guard(nameof(OnFsdConnected), () =>
    {
        _logger?.Info("Connected to the network; reading vIFF now");
        _master?.NoteConnected();
        RequestSync();
    });

    public override void OnFsdDisconnected(FsdDisconnectedEvent evt) => Guard(nameof(OnFsdDisconnected), () =>
    {
        _logger?.Info("Disconnected from the network; releasing any master role held");
        MasterRole? master = _master;
        _scheduler?.RunOnce("release on disconnect", ct => master?.ReleaseAllAsync(ct) ?? System.Threading.Tasks.Task.CompletedTask);
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

    private void Compose(ICoreApi core, ClientInformation clientInfo)
    {
        TimeProvider clock = TimeProvider.System;
        ISettingsApi settings = core.Settings;
        _controllerData = core.ControllerData;
        _chat = core.Chat;
        _menu = core.Menu;

        Uri apiUrl = ReadApiUrl(settings.Get("apiUrl"));
        string? settingsKey = settings.Get("apiKey") is { Length: > 0 } key ? key : null;
        string apiKey = settingsKey ?? BuiltInApiKey;
        string keySource = settingsKey is not null ? "set with .cdm apikey" : BuildSecrets.ApiKey.Length > 0 ? "built in" : "placeholder, read only";
        int refreshSeconds = int.TryParse(settings.Get("refreshSeconds"), out int seconds) && seconds >= 5 ? seconds : DefaultRefreshSeconds;
        string? readySetTsacSetting = settings.Get("readySetTsac");

        JobScheduler scheduler = new(clock, core.Logger);
        ViffApiClient api = new(apiUrl, apiKey, $"vIFF-neoradar/{PluginVersion}", core.Logger);
        Notifier notifier = new(core.Chat, core.Feedback, core.Logger);
        ConnectionGate gate = new(core.Fsd);
        CdmStore store = new();
        AirportControls controls = new(settings);
        RemoteText remote = new($"vIFF-neoradar/{PluginVersion}", core.Logger);
        EventSlots slots = new();
        ConfigLoader config = new(() => core.Package.GetPackagePath(), clientInfo.DocumentsPath, remote, slots, core.Logger);

        // The readySetTsac setting, when set, overrides CDMconfig.xml's.
        bool ReadySetTsac() => readySetTsacSetting is { Length: > 0 }
            ? !string.Equals(readySetTsacSetting, "false", StringComparison.OrdinalIgnoreCase)
            : config.Current.Config.ReadySetTsac;
        NetworkStatuses statuses = new();
        StripStore strips = new(core.ControllerData);
        StripWriter stripWriter = new(strips, core.ControllerData, core.Logger);
        StripSync stripSync = new(strips, stripWriter, core.ControllerData, notifier);
        MasterSequencing sequencing = new(api, store, controls, core.Logger, message => notifier.Say(message), slots, () => config.Current);
        CdmSync sync = new(api, store, controls, sequencing, gate, core.Airport, core.Flightplan, core.ControllerData, clock,
            config, statuses, core.Aircraft, core.Logger);
        TagPublisher publisher = new(core.Tag.GetInterface(), core.Expressions, core.Logger);

        FlightExtras ExtrasOf(FlightView flight) => new(
            stripWriter.Current(flight.Record.Callsign),
            slots.Find(flight.Record.Cid, flight.Record.Callsign, flight.Record.Departure, flight.Record.Arrival),
            controls.IsCtotIgnored(flight.Record.Callsign));

        void PublishAll()
        {
            publisher.Publish(AllFlights(store, sync), clock.GetUtcNow(), ExtrasOf);
            publisher.PublishCompliance(sync.LatestTraffic.Callsigns, statuses);
        }

        void Changed()
        {
            if (scheduler.IsStopped)
            {
                return;
            }

            PublishAll();
            _panel?.Refresh();
            RequestSync();
        }

        FlightActions actions = new(api, store, gate, scheduler, notifier, clock, Changed, stripWriter, controls, slots, ReadySetTsac);
        MasterRole master = new(api, store, sync, gate, scheduler, notifier, Changed);
        AirportPanel panel = new(core.Window.GetInterface(), store, sync, controls, master, gate, notifier);
        AtfcmList atfcm = new(core.Window.GetInterface(), api, core.Focus, settings, gate, scheduler, () => core.Airport.GetConfigurations()
            .Where(c => c.Status == AirportStatus.Active && c.DepRunways.Count > 0)
            .Select(c => c.Icao)
            .ToList());
        TagMenus tagMenus = new(core.Tag.GetInterface(), store, controls, actions, core.Logger,
            record => slots.Find(record.Cid, record.Callsign, record.Departure, record.Arrival), stripWriter.Current);
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

            api.SetApiKey(key ?? BuiltInApiKey);
        }, atfcm.Open, () => config.Current, () => config.SearchFolders, sync.RequestConfigReload, core.Logger);

        publisher.Register();
        tagMenus.Register();
        _commandId = core.Chat.RegisterCommand("cdm", CdmCommand.Definition, command);
        if (_commandId.Length == 0)
        {
            core.Logger.Error("The .cdm command was refused; another plugin may have registered it");
        }

        _panelMenuId = core.Menu.RegisterMenuItem(new MenuItemDefinition("Airport Panel", "CDM Airport Panel"));
        _atfcmMenuId = core.Menu.RegisterMenuItem(new MenuItemDefinition("ATFCM List", "ATFCM Flight List"));

        sync.Synced += () =>
        {
            master.NoteConnected();
            IReadOnlyList<FlightView> flights = store.Flights();
            stripSync.Track(flights);
            if (gate.CanWrite(out string me, out _))
            {
                stripSync.Mirror(flights, new HashSet<string>(store.AirportsMasteredBy(me), StringComparer.OrdinalIgnoreCase));
            }

            PublishAll();
            panel.Refresh();
        };

        _scheduler = scheduler;
        _api = api;
        _notifier = notifier;
        _store = store;
        _tagMenus = tagMenus;
        _master = master;
        _panel = panel;
        _atfcm = atfcm;
        _strips = strips;
        _remote = remote;
        _syncJob = scheduler.Every("sync", TimeSpan.FromSeconds(refreshSeconds), sync.RunAsync);
        scheduler.Every("tags", TimeSpan.FromSeconds(1), ct =>
        {
            if (!ct.IsCancellationRequested)
            {
                PublishAll();
            }

            return System.Threading.Tasks.Task.CompletedTask;
        });

        // One strip write a second, the client's limit; the master also sets STUP for armed flights in their window.
        scheduler.Every("strips", TimeSpan.FromSeconds(1), ct =>
        {
            if (ct.IsCancellationRequested)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            stripWriter.Tick();
            if (gate.CanWrite(out string me, out _))
            {
                stripSync.RunAutoStartup(store.Flights(), new HashSet<string>(store.AirportsMasteredBy(me), StringComparer.OrdinalIgnoreCase), clock.GetUtcNow());
            }

            return System.Threading.Tasks.Task.CompletedTask;
        });
        scheduler.Every("atfcm", TimeSpan.FromMinutes(1), ct => atfcm.IsOpen ? atfcm.RefreshAsync(ct) : System.Threading.Tasks.Task.CompletedTask);

        core.Logger.Info($"vIFF CDM {PluginVersion} reading {apiUrl} every {refreshSeconds} s, API key {keySource}; " +
                         $"CDMconfig.xml looked for in {string.Join(" then ", config.SearchFolders)}");
    }

    private static IReadOnlyList<FlightView> AllFlights(CdmStore store, CdmSync sync) =>
        store.Flights().Concat(store.NetworkFlights(sync.LatestTraffic)).ToList();

    private static string BuiltInApiKey => BuildSecrets.ApiKey.Length > 0 ? BuildSecrets.ApiKey : PlaceholderApiKey;

    private void RefreshStrip(string callsign)
    {
        if (_store?.Find(callsign) is not null)
        {
            _strips?.Refresh(callsign);
        }
    }

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
