using System;
using System.Globalization;

namespace Viff.NeoRadar.Common;

/// <summary>
/// Converts the dateless UTC clock times vIFF uses ("HHMM" or "HHMMSS") to and from instants.
/// </summary>
internal static class CdmTime
{
    /// <summary>
    /// Parses "HHMM" or "HHMMSS" into a time of day, or null when the text is empty or not a valid time.
    /// </summary>
    public static TimeSpan? ParseTimeOfDay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        if ((trimmed.Length != 4 && trimmed.Length != 6) || !IsDigits(trimmed))
        {
            return null;
        }

        int hours = int.Parse(trimmed.AsSpan(0, 2), CultureInfo.InvariantCulture);
        int minutes = int.Parse(trimmed.AsSpan(2, 2), CultureInfo.InvariantCulture);
        int seconds = trimmed.Length == 6 ? int.Parse(trimmed.AsSpan(4, 2), CultureInfo.InvariantCulture) : 0;
        if (hours > 23 || minutes > 59 || seconds > 59)
        {
            return null;
        }

        return new TimeSpan(hours, minutes, seconds);
    }

    /// <summary>
    /// Places a time of day on the UTC date that puts it within 12 hours of <paramref name="nowUtc"/>.
    /// </summary>
    public static DateTimeOffset ResolveNear(TimeSpan timeOfDay, DateTimeOffset nowUtc)
    {
        DateTimeOffset now = nowUtc.ToUniversalTime();
        DateTimeOffset candidate = new DateTimeOffset(now.Date, TimeSpan.Zero) + timeOfDay;
        TimeSpan offset = candidate - now;
        if (offset > TimeSpan.FromHours(12))
        {
            return candidate.AddDays(-1);
        }

        if (offset < TimeSpan.FromHours(-12))
        {
            return candidate.AddDays(1);
        }

        return candidate;
    }

    /// <summary>
    /// Parses a vIFF clock time and places it near <paramref name="nowUtc"/>, or returns null when it is not a time.
    /// </summary>
    public static DateTimeOffset? ParseNear(string? text, DateTimeOffset nowUtc)
    {
        TimeSpan? timeOfDay = ParseTimeOfDay(text);
        return timeOfDay is null ? null : ResolveNear(timeOfDay.Value, nowUtc);
    }

    /// <summary>
    /// Formats an instant as "HHMM" UTC.
    /// </summary>
    public static string ToHhmm(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("HHmm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats an instant as "HHMMSS" UTC.
    /// </summary>
    public static string ToHhmmss(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns the "HHMM" part of a vIFF clock time, or empty when it is not a time.
    /// </summary>
    public static string Display(string? text)
    {
        TimeSpan? timeOfDay = ParseTimeOfDay(text);
        return timeOfDay is null ? string.Empty : $"{timeOfDay.Value.Hours:00}{timeOfDay.Value.Minutes:00}";
    }

    /// <summary>
    /// Rounds an instant up to the next whole minute, leaving one already on a minute unchanged.
    /// </summary>
    public static DateTimeOffset CeilToMinute(DateTimeOffset instant)
    {
        long ticksPastMinute = instant.UtcTicks % TimeSpan.TicksPerMinute;
        return ticksPastMinute == 0 ? instant : instant.AddTicks(TimeSpan.TicksPerMinute - ticksPastMinute);
    }

    private static bool IsDigits(string text)
    {
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }
}
