using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Viff.NeoRadar.Config;

/// <summary>
/// Specifies when an unstarted flight loses its TSAT, the CDM plugin's <c>invalidateAtTsat</c>.
/// </summary>
internal enum TsatInvalidation
{
    /// <summary>Never; the TSAT moves on with the sequence.</summary>
    Never,

    /// <summary>At TSAT +6, unless start-up was requested (ASRT), the CDM plugin's "asrt".</summary>
    UnlessReady,

    /// <summary>At TSAT +6, even with a start-up request.</summary>
    Always,
}

/// <summary>
/// Represents the options of a CDM plugin <c>CDMconfig.xml</c> this plugin uses; everything else in the file is ignored.
/// </summary>
/// <remarks>Each option falls back to the CDM plugin's own default when the file leaves it out.</remarks>
internal sealed record CdmConfig
{
    /// <summary>Gets the departure rate per hour used when no rate.txt line and no vIFF rate applies, or 0.</summary>
    public int Rate { get; init; }

    /// <summary>Gets the LVO departure rate per hour used when no rate.txt line applies, or 0.</summary>
    public int RateLvo { get; init; }

    public int DefaultTaxiMinutes { get; init; } = 15;

    /// <summary>Gets the de-icing minutes per ICAO wake category, L, M, H and J.</summary>
    public IReadOnlyDictionary<string, int> DeIceMinutes { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["L"] = 5, ["M"] = 9, ["H"] = 12, ["J"] = 15 };

    /// <summary>Gets the remote de-icing pads, REM1 to REM5 as named, with their extra taxi minutes.</summary>
    public IReadOnlyList<(string Name, int ExtraTaxiMinutes)> RemotePads { get; init; } = Array.Empty<(string, int)>();

    public bool ReadySetTsac { get; init; } = true;

    public TsatInvalidation InvalidateAtTsat { get; init; } = TsatInvalidation.UnlessReady;

    /// <summary>Gets a value indicating whether confirmed TOBTs are sequenced first, the CDM plugin's <c>reqTobtPriority</c>.</summary>
    public bool ReqTobtPriority { get; init; } = true;

    /// <summary>Gets a value indicating whether event slot flights are sequenced first, the CDM plugin's <c>eventPriority</c>.</summary>
    public bool EventPriority { get; init; } = true;

    /// <summary>Gets a value indicating whether take-offs are planned in 10-minute blocks, the CDM plugin's BMI mode.</summary>
    public bool Bmi { get; init; }

    /// <summary>Gets the default runway pressure in minutes, or -1 when runway pressure is off everywhere.</summary>
    public int RunwayPressure { get; init; } = -1;

    public string RatesUrl { get; init; } = string.Empty;

    public string TaxiZonesUrl { get; init; } = string.Empty;

    public string SlotsUrl { get; init; } = string.Empty;

    public string SidIntervalUrl { get; init; } = string.Empty;

    /// <summary>Gets the VDGS private message text, kept for when the SDK can send one.</summary>
    public string PrivateMessage { get; init; } = string.Empty;

    /// <summary>
    /// Reads a CDMconfig.xml, collecting a warning for each value it cannot read rather than failing.
    /// </summary>
    public static CdmConfig Parse(string xml, List<string> warnings)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            warnings.Add($"CDMconfig.xml is not valid XML ({ex.Message}); using the defaults");
            return new CdmConfig();
        }

        XElement root = document.Root ?? new XElement("CDM");
        CdmConfig defaults = new();

        string Attribute(string element, string attribute) =>
            root.Element(element)?.Attribute(attribute)?.Value.Trim() ?? string.Empty;

        int Number(string element, string attribute, int fallback)
        {
            string text = Attribute(element, attribute);
            if (text.Length == 0)
            {
                return fallback;
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }

            warnings.Add($"CDMconfig.xml <{element} {attribute}=\"{text}\"> is not a number; using {fallback}");
            return fallback;
        }

        bool Flag(string element, bool fallback)
        {
            string text = Attribute(element, "mode");
            return text.Length == 0 ? fallback : string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }

        Dictionary<string, int> deIce = new(StringComparer.OrdinalIgnoreCase)
        {
            ["L"] = Number("DeIceTimes", "light", 5),
            ["M"] = Number("DeIceTimes", "medium", 9),
            ["H"] = Number("DeIceTimes", "heavy", 12),
            ["J"] = Number("DeIceTimes", "super", 15),
        };

        List<(string, int)> pads = new();
        for (int i = 1; i <= 5; i++)
        {
            string name = Attribute("DeIceRemTaxi", $"rem{i}Name");
            if (name.Length > 0)
            {
                pads.Add((name.ToUpperInvariant(), Math.Max(0, Number("DeIceRemTaxi", $"rem{i}", 0))));
            }
        }

        string invalidate = Attribute("invalidateAtTsat", "mode").ToLowerInvariant();
        return new CdmConfig
        {
            Rate = Math.Max(0, Number("rate", "ops", 0)),
            RateLvo = Math.Max(0, Number("rateLvo", "ops", 0)),
            DefaultTaxiMinutes = Math.Max(1, Number("DefaultTaxiTime", "minutes", defaults.DefaultTaxiMinutes)),
            DeIceMinutes = deIce,
            RemotePads = pads,
            ReadySetTsac = Flag("readySetTsac", defaults.ReadySetTsac),
            InvalidateAtTsat = invalidate switch
            {
                "true" => TsatInvalidation.Always,
                "false" => TsatInvalidation.Never,
                "asrt" => TsatInvalidation.UnlessReady,
                _ => defaults.InvalidateAtTsat,
            },
            ReqTobtPriority = Flag("reqTobtPriority", defaults.ReqTobtPriority),
            EventPriority = Flag("eventPriority", defaults.EventPriority),
            Bmi = Flag("bmi", defaults.Bmi),
            RunwayPressure = Math.Max(-1, Number("runwayPressure", "minutes", defaults.RunwayPressure)),
            RatesUrl = Attribute("Rates", "url"),
            TaxiZonesUrl = Attribute("Taxizones", "url"),
            SlotsUrl = Attribute("Slots", "url"),
            SidIntervalUrl = Attribute("sidInterval", "url"),
            PrivateMessage = Attribute("PrivateMessage", "text"),
        };
    }

    /// <summary>
    /// Describes the options in one line, for the log.
    /// </summary>
    public string Describe() =>
        $"rate {Rate}/h (LVO {RateLvo}), default taxi {DefaultTaxiMinutes} min, BMI {(Bmi ? "on" : "off")}, " +
        $"runway pressure {(RunwayPressure < 0 ? "off" : RunwayPressure + " min")}, invalidate at TSAT {InvalidateAtTsat}, " +
        $"readySetTsac {ReadySetTsac}, reqTobtPriority {ReqTobtPriority}, eventPriority {EventPriority}, " +
        $"remote pads {(RemotePads.Count == 0 ? "none" : string.Join(",", RemotePads.Select(p => $"{p.Name}+{p.ExtraTaxiMinutes}")))}";
}
