namespace Viff.NeoRadar.Viff;

/// <summary>
/// Represents the CDM times a master stored on vIFF for one flight. Times are "HHMMSS", ASRT is "HHMM", empty for none.
/// </summary>
internal sealed record CdmData
{
    public string Tobt { get; init; } = string.Empty;

    public string Tsat { get; init; } = string.Empty;

    public string Ttot { get; init; } = string.Empty;

    public string Ctot { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string Asrt { get; init; } = string.Empty;

    /// <summary>Gets the departure runway and SID as "RWY/SID".</summary>
    public string DepInfo { get; init; } = string.Empty;

    public string Deice { get; init; } = string.Empty;

    /// <summary>Gets the start-up request a pilot made through the VDGS, "HHMM", or empty for none.</summary>
    public string ReqAsrt { get; init; } = string.Empty;

    /// <summary>Gets the TOBT a pilot or controller requested from the master, "HHMM", or empty for none.</summary>
    public string ReqTobt { get; init; } = string.Empty;

    /// <summary>Gets who made the pending TOBT request, such as PILOT or ATC.</summary>
    public string ReqTobtType { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the pilot confirmed the TOBT through the VDGS.</summary>
    public bool Confirmed { get; init; }
}

/// <summary>
/// Represents one departure as vIFF's <c>/ifps/depAirport</c> returns it.
/// </summary>
internal sealed record DepartureRecord
{
    public string Callsign { get; init; } = string.Empty;

    public string Departure { get; init; } = string.Empty;

    public string Arrival { get; init; } = string.Empty;

    /// <summary>Gets the filed EOBT, "HHMM".</summary>
    public string Eobt { get; init; } = string.Empty;

    /// <summary>Gets the off-block time vIFF holds for ATFCM, "HHMM".</summary>
    public string Tobt { get; init; } = string.Empty;

    /// <summary>Gets the taxi time in minutes vIFF holds for the flight, or 0 when unknown.</summary>
    public int TaxiMinutes { get; init; }

    /// <summary>Gets the ATFCM CTOT, "HHMM", or empty for none.</summary>
    public string Ctot { get; init; } = string.Empty;

    public string Aobt { get; init; } = string.Empty;

    public string Atot { get; init; } = string.Empty;

    /// <summary>Gets the network status, such as COMPLY, REA, FLS or AIRB.</summary>
    public string CdmStatus { get; init; } = string.Empty;

    public string AtfcmStatus { get; init; } = string.Empty;

    /// <summary>Gets the most penalising regulation behind the CTOT, or empty for none.</summary>
    public string Regulation { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the flight asked to be re-assessed for a better CTOT.</summary>
    public bool IsRea { get; init; }

    /// <summary>Gets a value indicating whether a controller sent the pilot the VDGS message.</summary>
    public bool Informed { get; init; }

    public CdmData Cdm { get; init; } = new();

    /// <summary>Gets a value indicating whether the flight is airborne.</summary>
    public bool IsAirborne => Atot.Length > 0;

    /// <summary>Gets a value indicating whether the master holds a manual CTOT and ATFCM none.</summary>
    public bool HasManualCtot =>
        Ctot.Length == 0 && Cdm.Ctot.Length > 0 && string.Equals(Cdm.Reason, CdmDataUpdate.ManualReason, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets the CTOT from ATFCM, or the manual one the master holds, or empty for none.</summary>
    public string EffectiveCtot => Ctot.Length > 0 ? Ctot : HasManualCtot ? Cdm.Ctot : string.Empty;
}

/// <summary>
/// Represents an airport's capacity as vIFF's CAD holds it.
/// </summary>
/// <param name="Icao">The airport's ICAO code.</param>
/// <param name="Rate">The departure rate per hour, or 0 when none is set.</param>
/// <param name="TaxiMinutes">The default taxi time in minutes, or 0 when none is set.</param>
/// <param name="IsCdm">Whether the airport runs CDM.</param>
internal sealed record AirportCapacity(string Icao, int Rate, int TaxiMinutes, bool IsCdm);

/// <summary>
/// Represents one regulated flight from vIFF's network-wide <c>/etfms/restricted</c> list.
/// </summary>
/// <param name="Callsign">The flight's callsign.</param>
/// <param name="Ctot">The CTOT, "HHMM".</param>
/// <param name="Regulation">The most penalising regulation, or empty when vIFF names none.</param>
/// <param name="Atot">The actual take-off time, "HHMM", or empty while on the ground.</param>
/// <param name="CdmStatus">The network status, such as COMPLY or REA.</param>
internal sealed record RestrictedFlight(string Callsign, string Ctot, string Regulation, string Atot, string CdmStatus);

/// <summary>
/// Represents the controller position vIFF lists as CDM master of an airport.
/// </summary>
internal sealed record MasterAirport(string Icao, string Position);

/// <summary>
/// Represents a full write of one flight's CDM data, as <c>/ifps/setCdmData</c> takes it.
/// </summary>
internal sealed record CdmDataUpdate(
    string Callsign,
    string Tobt,
    string Tsat,
    string Ttot,
    string Ctot,
    string Reason,
    string Asrt,
    string DepInfo,
    string Deice)
{
    /// <summary>
    /// Gets the reason vIFF stores with a CTOT the master set by hand.
    /// </summary>
    public const string ManualReason = "MANUAL";

    /// <summary>
    /// Creates an update that rewrites a flight's current CDM data unchanged, for callers to amend with <c>with</c>.
    /// </summary>
    public static CdmDataUpdate From(DepartureRecord record) => new(
        record.Callsign,
        record.Cdm.Tobt,
        record.Cdm.Tsat,
        record.Cdm.Ttot,
        record.EffectiveCtot,
        record.HasManualCtot ? ManualReason : record.EffectiveCtot.Length > 0 ? record.Regulation : string.Empty,
        record.Cdm.Asrt,
        record.Cdm.DepInfo,
        record.Cdm.Deice);

    /// <summary>
    /// Applies this update to a record, as the next poll would show it.
    /// </summary>
    public DepartureRecord ApplyTo(DepartureRecord record) => record with
    {
        Cdm = record.Cdm with
        {
            Tobt = Tobt,
            Tsat = Tsat,
            Ttot = Ttot,
            Ctot = Ctot,
            Reason = Reason,
            Asrt = Asrt,
            DepInfo = DepInfo,
            Deice = Deice,
        },
    };
}
