using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NeoRadar.PluginSdk.Logger;
using Viff.NeoRadar.Cdm;
using Viff.NeoRadar.Viff;

namespace Viff.NeoRadar.Config;

/// <summary>
/// Represents a vACC's CDM setup as last loaded: CDMconfig.xml and the files it names.
/// </summary>
/// <param name="Source">Where CDMconfig.xml was found, for the log and <c>.cdm status</c>, or "none".</param>
internal sealed record CdmSetup(CdmConfig Config, RateTable Rates, TaxiZones Zones, SidIntervals Intervals, string Source)
{
    public static CdmSetup None { get; } = new(new CdmConfig(), RateTable.Empty, TaxiZones.Empty, SidIntervals.Empty, "none");

    public bool Loaded => Source != "none";
}

/// <summary>
/// Finds and loads a vACC's CDM setup, in the CDM plugin's own format, from the package or the controller's documents.
/// </summary>
/// <remarks>
/// CDMconfig.xml is looked for in the package's <c>cdm/</c> folder, then in <c>Documents/NeoRadar/vIFF CDM/</c>. Each
/// data file is downloaded from the URL the config gives it, falling back to the last copy downloaded, or read from
/// beside the config when the URL is blank, as the CDM plugin does. Without a config the plugin runs on vIFF's rates.
/// </remarks>
internal sealed class ConfigLoader
{
    public const string ConfigFileName = "CDMconfig.xml";
    public const string PackageFolder = "cdm";
    public const string DocumentsFolder = "vIFF CDM";

    private readonly Func<string?> _packagePath;
    private readonly string _documentsPath;
    private readonly IRemoteText _remote;
    private readonly EventSlots _slots;
    private readonly ILoggerApi _logger;

    // Swapped whole by the loading job; read by the sync, tag and command threads.
    private volatile CdmSetup _current = CdmSetup.None;
    private string _folder = string.Empty;

    public ConfigLoader(Func<string?> packagePath, string documentsPath, IRemoteText remote, EventSlots slots, ILoggerApi logger)
    {
        _packagePath = packagePath;
        _documentsPath = documentsPath;
        _remote = remote;
        _slots = slots;
        _logger = logger;
    }

    public CdmSetup Current => _current;

    /// <summary>
    /// Gets the folders searched for CDMconfig.xml, first found wins.
    /// </summary>
    public IReadOnlyList<string> SearchFolders
    {
        get
        {
            List<string> folders = new();
            string? package = SafePackagePath();
            if (!string.IsNullOrEmpty(package))
            {
                folders.Add(Path.Combine(package, PackageFolder));
            }

            if (_documentsPath.Length > 0)
            {
                folders.Add(Path.Combine(_documentsPath, DocumentsFolder));
            }

            return folders;
        }
    }

    /// <summary>
    /// Returns whether the place CDMconfig.xml should come from changed since the last load, as after a package switch.
    /// </summary>
    public bool SourceChanged() => FindConfigFolder() != _folder;

    /// <summary>
    /// Loads CDMconfig.xml and every file it names; a file that cannot be read leaves that part empty, with a warning.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        string folder = FindConfigFolder();
        _folder = folder;
        if (folder.Length == 0)
        {
            _current = CdmSetup.None;
            _slots.Replace(Array.Empty<EventSlot>());
            _logger.Info($"No {ConfigFileName} in {string.Join(" or ", SearchFolders)}: sequencing on vIFF's rates and taxi times only");
            return;
        }

        string configPath = Path.Combine(folder, ConfigFileName);
        List<string> warnings = new();
        string xml;
        try
        {
            xml = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning($"Could not read {configPath} ({ex.Message}); keeping the CDM setup from {_current.Source}");
            return;
        }

        CdmConfig config = CdmConfig.Parse(xml, warnings);

        string? rates = await ReadAsync(config.RatesUrl, "rate.txt", folder, cancellationToken).ConfigureAwait(false);
        string? zones = await ReadAsync(config.TaxiZonesUrl, "taxizones.txt", folder, cancellationToken).ConfigureAwait(false);
        string? intervals = await ReadAsync(config.SidIntervalUrl, "sidInterval.txt", folder, cancellationToken).ConfigureAwait(false);
        CdmSetup setup = new(
            config,
            rates is null ? RateTable.Empty : RateTable.Parse(rates, warnings),
            zones is null ? TaxiZones.Empty : TaxiZones.Parse(zones, warnings),
            intervals is null ? SidIntervals.Empty : SidIntervals.Parse(intervals, warnings),
            configPath);
        _current = setup;

        foreach (string warning in warnings)
        {
            _logger.Warning(warning);
        }

        _logger.Info($"CDM setup from {configPath}: {config.Describe()}");
        _logger.Info($"CDM setup: {setup.Rates.Count} rate line(s) for {setup.Rates.Airports.Count} airport(s), " +
                     $"{setup.Zones.Count} taxi zone(s), {setup.Intervals.Count} SID interval(s), {warnings.Count} line(s) skipped");
        await LoadSlotsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reloads only the event slots, which change during an event.
    /// </summary>
    public async Task LoadSlotsAsync(CancellationToken cancellationToken)
    {
        if (_folder.Length == 0)
        {
            return;
        }

        string? text = await ReadAsync(_current.Config.SlotsUrl, "ctot.txt", _folder, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<EventSlot> slots = text is null ? Array.Empty<EventSlot>() : EventSlots.Parse(text);
        int before = _slots.Count;
        _slots.Replace(slots);
        if (slots.Count != before)
        {
            _logger.Info($"Event slots: {slots.Count} booked");
        }
    }

    private string FindConfigFolder()
    {
        foreach (string folder in SearchFolders)
        {
            if (File.Exists(Path.Combine(folder, ConfigFileName)))
            {
                return folder;
            }
        }

        return string.Empty;
    }

    // A URL is downloaded and cached under the documents folder; a blank URL reads the file beside the config.
    private async Task<string?> ReadAsync(string url, string fileName, string folder, CancellationToken cancellationToken)
    {
        if (url.Length == 0)
        {
            string local = Path.Combine(folder, fileName);
            if (!File.Exists(local))
            {
                _logger.Debug($"{fileName}: no URL in {ConfigFileName} and no file at {local}");
                return null;
            }

            _logger.Debug($"{fileName}: read from {local}");
            return await ReadFileAsync(local, fileName, cancellationToken).ConfigureAwait(false);
        }

        string cache = Path.Combine(_documentsPath, DocumentsFolder, "cache", fileName);
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            _logger.Warning($"{fileName}: '{url}' in {ConfigFileName} is not an http(s) URL; the file is not loaded");
            return null;
        }

        string? text = await _remote.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (text is not null)
        {
            _logger.Debug($"{fileName}: downloaded {text.Length} characters from {uri}");
            TryWriteCache(cache, text);
            return text;
        }

        if (File.Exists(cache))
        {
            _logger.Warning($"{fileName}: using the copy downloaded {File.GetLastWriteTimeUtc(cache):yyyy-MM-dd HH:mm}Z, as {uri} could not be read");
            return await ReadFileAsync(cache, fileName, cancellationToken).ConfigureAwait(false);
        }

        _logger.Warning($"{fileName}: {uri} could not be read and no copy was downloaded before; the file is not loaded");
        return null;
    }

    private async Task<string?> ReadFileAsync(string path, string fileName, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning($"{fileName}: could not read {path} ({ex.Message}); the file is not loaded");
            return null;
        }
    }

    private void TryWriteCache(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning($"Could not cache {path}: {ex.Message}");
        }
    }

    private string? SafePackagePath()
    {
        try
        {
            return _packagePath();
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not read the package path: {ex.Message}");
            return null;
        }
    }
}
