using System;
using System.Collections.Generic;
using NeoRadar.PluginSdk.Expressions;
using NeoRadar.PluginSdk.Logger;
using NeoRadar.PluginSdk.Tag;
using Viff.NeoRadar.Cdm;

namespace Viff.NeoRadar.Tags;

/// <summary>
/// Registers the tag items and expressions, and pushes each flight's values, sending only what changed.
/// </summary>
internal sealed class TagPublisher
{
    private static readonly (CdmItem Item, string Name, string Title, string Description)[] s_items =
    {
        (CdmItem.Tobt, "TOBT", "E/TOBT", "TOBT at a CDM airport, otherwise the EOBT"),
        (CdmItem.SetBy, "BY", "BY", "Who set the TOBT: P pilot, A ATC, blank when not confirmed"),
        (CdmItem.Tsat, "TSAT", "TSAT", "Target start-up approval time, ~ before it is calculated, coloured by the start-up window"),
        (CdmItem.Ttg, "TTG", "TTG", "Minutes until TSAT"),
        (CdmItem.Ttot, "TTOT", "TTOT", "Target take-off time"),
        (CdmItem.Ctot, "CTOT", "CTOT", "Calculated take-off time, red when it can no longer be met"),
        (CdmItem.Etfms, "ETFMS", "ETFMS", "Network status: FLS-*, REA, SAM, SRM, SLC, DES or ACT"),
        (CdmItem.Ready, "RDY", "RDY", "Ready for start-up: green once RDY is set"),
        (CdmItem.Asrt, "ASRT", "ASRT", "Actual start-up request time"),
        (CdmItem.Asat, "ASAT", "ASAT", "Actual start-up approval time, as this client saw it"),
        (CdmItem.DeIce, "DEICE", "DEICE", "De-icing: STND or the remote pad"),
        (CdmItem.Message, "MSG", "MSG", "VDGS message status: green once the pilot was informed"),
        (CdmItem.Regulation, "REG", "REG", "The regulation behind the CTOT, or MANUAL"),
        (CdmItem.Eobt, "EOBT", "EOBT", "The EOBT filed in the flight plan"),
    };

    private readonly ITagInterface _tags;
    private readonly IExpressionApi _expressions;
    private readonly ILoggerApi _logger;
    private readonly Dictionary<CdmItem, string> _itemIds = new();
    private string _isCdmId = string.Empty;
    private string _listedId = string.Empty;
    private string _readyId = string.Empty;
    private string _hasCtotId = string.Empty;
    private string _informedId = string.Empty;
    private string _tsatWindowId = string.Empty;
    private string _etfmsId = string.Empty;

    // Guards the caches; the publish job and the sync job both publish.
    private readonly object _lock = new();
    private readonly Dictionary<(string Id, string Callsign), TagText> _pushedItems = new();
    private readonly Dictionary<(string Id, string Callsign), string> _pushedExpressions = new();
    private readonly HashSet<string> _published = new(StringComparer.OrdinalIgnoreCase);

    public TagPublisher(ITagInterface tags, IExpressionApi expressions, ILoggerApi logger)
    {
        _tags = tags;
        _expressions = expressions;
        _logger = logger;
    }

    public void Register()
    {
        foreach ((CdmItem item, string name, string title, string description) in s_items)
        {
            string id = _tags.RegisterTagItem(new TagItemDefinition(name, string.Empty) { Title = title, Description = description });
            if (id.Length == 0)
            {
                _logger.Error($"The tag item {name} was refused");
            }

            _itemIds[item] = id;
        }

        _isCdmId = Declare(ExpressionDefinition.Boolean("Is CDM", false, "The departure airport runs CDM on vIFF"));
        _listedId = Declare(ExpressionDefinition.Boolean("Listed", false, "A departure from a watched airport, not yet airborne"));
        _readyId = Declare(ExpressionDefinition.Boolean("Ready", false, "RDY is set"));
        _hasCtotId = Declare(ExpressionDefinition.Boolean("Has CTOT", false, "The flight has an ATFCM or manual CTOT"));
        _informedId = Declare(ExpressionDefinition.Boolean("Informed", false, "The pilot was sent the VDGS message"));
        _tsatWindowId = Declare(ExpressionDefinition.Text("TSAT Window", string.Empty,
            "pending, future, window, closing or expired; empty when not a CDM flight or started"));
        _etfmsId = Declare(ExpressionDefinition.Text("ETFMS", string.Empty, "The network status shown in the ETFMS item"));
    }

    /// <summary>
    /// Pushes every flight's values, and resets those of flights no longer listed.
    /// </summary>
    public void Publish(IReadOnlyList<FlightView> flights, DateTimeOffset now)
    {
        bool flashOn = now.Second % 2 == 0;
        lock (_lock)
        {
            HashSet<string> current = new(StringComparer.OrdinalIgnoreCase);
            foreach (FlightView flight in flights)
            {
                string callsign = flight.Record.Callsign;
                current.Add(callsign);
                FlightTags tags = CdmTagFormatter.Format(flight, now, flashOn);
                foreach (KeyValuePair<CdmItem, TagText> item in tags.Items)
                {
                    PushItem(_itemIds[item.Key], callsign, item.Value);
                }

                PushExpression(_listedId, callsign, true);
                PushExpression(_isCdmId, callsign, tags.IsCdm);
                PushExpression(_readyId, callsign, tags.Ready);
                PushExpression(_hasCtotId, callsign, tags.HasCtot);
                PushExpression(_informedId, callsign, tags.Informed);
                PushExpression(_tsatWindowId, callsign, tags.Tsat == TsatState.None ? string.Empty : tags.Tsat.ToString().ToLowerInvariant());
                PushExpression(_etfmsId, callsign, tags.Etfms);
            }

            foreach (string gone in _published)
            {
                if (!current.Contains(gone))
                {
                    Reset(gone);
                }
            }

            _published.Clear();
            _published.UnionWith(current);
        }
    }

    private void Reset(string callsign)
    {
        foreach (string id in _itemIds.Values)
        {
            PushItem(id, callsign, new TagText(string.Empty));
        }

        PushExpression(_listedId, callsign, false);
        PushExpression(_isCdmId, callsign, false);
        PushExpression(_readyId, callsign, false);
        PushExpression(_hasCtotId, callsign, false);
        PushExpression(_informedId, callsign, false);
        PushExpression(_tsatWindowId, callsign, string.Empty);
        PushExpression(_etfmsId, callsign, string.Empty);
    }

    private string Declare(ExpressionDefinition definition)
    {
        string id = _expressions.Declare(definition);
        if (id.Length == 0)
        {
            _logger.Error($"The expression {definition.Name} was refused");
        }

        return id;
    }

    // Must be called under _lock.
    private void PushItem(string id, string callsign, TagText value)
    {
        if (id.Length == 0 || (_pushedItems.TryGetValue((id, callsign), out TagText pushed) && pushed == value))
        {
            return;
        }

        if (_tags.UpdateTagValue(id, value.Text, new TagContext(callsign, value.Colour)))
        {
            _pushedItems[(id, callsign)] = value;
        }
    }

    // Must be called under _lock.
    private void PushExpression(string id, string callsign, bool value)
    {
        if (id.Length > 0 && Changed(id, callsign, value ? "1" : "0") && _expressions.Push(id, callsign, value))
        {
            _pushedExpressions[(id, callsign)] = value ? "1" : "0";
        }
    }

    // Must be called under _lock.
    private void PushExpression(string id, string callsign, string value)
    {
        if (id.Length > 0 && Changed(id, callsign, value) && _expressions.Push(id, callsign, value))
        {
            _pushedExpressions[(id, callsign)] = value;
        }
    }

    private bool Changed(string id, string callsign, string value) =>
        !_pushedExpressions.TryGetValue((id, callsign), out string? pushed) || pushed != value;
}
