using System;

namespace Viff.NeoRadar.Cdm;

/// <summary>
/// Reads and writes the de-icing choice vIFF stores as "STAND" or "REMOTE/{pad}", and the time it adds to the EXOT.
/// </summary>
internal static class DeIce
{
    public const string StandLabel = "STND";

    private const string StandValue = "STAND";
    private const string RemotePrefix = "REMOTE/";

    /// <summary>
    /// Gets the short label shown in the DEICE item, empty for no de-icing.
    /// </summary>
    public static string Label(string stored)
    {
        if (string.Equals(stored, StandValue, StringComparison.OrdinalIgnoreCase))
        {
            return StandLabel;
        }

        return stored.StartsWith(RemotePrefix, StringComparison.OrdinalIgnoreCase) ? stored[RemotePrefix.Length..] : stored;
    }

    /// <summary>
    /// Gets the value to store for a label chosen in the DEICE menu, empty for none.
    /// </summary>
    public static string Stored(string label)
    {
        if (label.Length == 0)
        {
            return string.Empty;
        }

        return string.Equals(label, StandLabel, StringComparison.OrdinalIgnoreCase) ? StandValue : RemotePrefix + label;
    }

    /// <summary>
    /// Gets the remote pad a stored value names, or null for none or on-stand de-icing.
    /// </summary>
    public static string? RemotePad(string stored) =>
        stored.StartsWith(RemotePrefix, StringComparison.OrdinalIgnoreCase) ? stored[RemotePrefix.Length..] : null;

    /// <summary>
    /// Gets the de-icing time in minutes for an ICAO wake category: L 5, M 9, H 12, J 15.
    /// </summary>
    public static int MinutesFor(string wakeCategory) => wakeCategory.ToUpperInvariant() switch
    {
        "L" => 5,
        "H" => 12,
        "J" => 15,
        _ => 9,
    };
}
