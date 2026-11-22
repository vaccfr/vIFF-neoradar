using System;
using System.Collections.Generic;
using System.Linq;

namespace Viff.NeoRadar.Strips;

/// <summary>
/// Represents the CDM data the CDM plugin keeps in flight strip box 0, so EuroScope and NeoRadar strips carry the same.
/// </summary>
/// <remarks>
/// The box holds twelve fields joined by '/': ASRT/TSAC/TOBT/TSAT/TTOT/deIce/ecfmpId/manualCtot/CTOC/setBy/autoStartup/ATOT.
/// Times are "HHMM" or "HHMMSS", as the CDM plugin writes them.
/// </remarks>
internal sealed record StripData
{
    public const int Asrt = 0;
    public const int Tsac = 1;
    public const int Tobt = 2;
    public const int Tsat = 3;
    public const int Ttot = 4;
    public const int DeIce = 5;
    public const int EcfmpId = 6;
    public const int ManualCtot = 7;
    public const int Ctoc = 8;
    public const int SetBy = 9;
    public const int AutoStartup = 10;
    public const int Atot = 11;

    /// <summary>The value of <see cref="AutoStartup"/> while automatic start-up is armed.</summary>
    public const string AutoStartupArmed = "1";

    /// <summary>The value of <see cref="AutoStartup"/> once automatic start-up has set STUP.</summary>
    public const string AutoStartupDone = "2";

    /// <summary>The value of <see cref="Tsac"/> while a TSAC waits for the TSAT that Ready TOBT asked for.</summary>
    public const string TsacPending = "9999";

    private const int FieldCount = 12;
    private const int MaxLength = 50;

    // Dropped first, in this order, when the box would exceed its 50 characters.
    private static readonly int[] s_droppable = { Atot, EcfmpId };

    private readonly string[] _fields;

    private StripData(string[] fields)
    {
        _fields = fields;
    }

    /// <summary>Gets a strip with every field empty.</summary>
    public static StripData Empty { get; } = new(Enumerable.Repeat(string.Empty, FieldCount).ToArray());

    public string this[int index] => index is >= 0 and < FieldCount ? _fields[index] : string.Empty;

    /// <summary>
    /// Reads box 0 as the CDM plugin writes it; anything missing reads empty.
    /// </summary>
    public static StripData Parse(string? box)
    {
        string[] fields = Enumerable.Repeat(string.Empty, FieldCount).ToArray();
        if (!string.IsNullOrEmpty(box))
        {
            string[] values = box.Split('/');
            for (int i = 0; i < Math.Min(values.Length, FieldCount); i++)
            {
                fields[i] = values[i].Trim();
            }
        }

        return new StripData(fields);
    }

    /// <summary>
    /// Returns a copy with one field replaced; '/' and ':' are removed, as neither can be stored.
    /// </summary>
    public StripData With(int index, string value)
    {
        if (index is < 0 or >= FieldCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        string[] fields = (string[])_fields.Clone();
        fields[index] = (value ?? string.Empty).Replace("/", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return new StripData(fields);
    }

    /// <summary>
    /// Writes the box as the CDM plugin does, always with all twelve fields, dropping ATOT then the ECFMP id if it would
    /// exceed 50 characters; empty when every field is empty.
    /// </summary>
    public string Format()
    {
        if (_fields.All(f => f.Length == 0))
        {
            return string.Empty;
        }

        string[] fields = (string[])_fields.Clone();
        string text = string.Join('/', fields);
        foreach (int index in s_droppable)
        {
            if (text.Length <= MaxLength)
            {
                break;
            }

            fields[index] = string.Empty;
            text = string.Join('/', fields);
        }

        return text.Length <= MaxLength ? text : text[..MaxLength];
    }

    public bool Equals(StripData? other) => other is not null && _fields.SequenceEqual(other._fields);

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (string field in _fields)
        {
            hash.Add(field);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => Format();

    /// <summary>
    /// Gets the fields, for tests and logs.
    /// </summary>
    public IReadOnlyList<string> Fields => _fields;
}
