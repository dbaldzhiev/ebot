using System.Text.RegularExpressions;

namespace EBot.Core.GameState;

/// <summary>
/// Shared EVE Online game constants and parsing helpers used across the framework.
/// </summary>
public static partial class EveConstants
{
    // ─── Station / Structure keywords ──────────────────────────────────────

    public static readonly string[] StationKeywords =
    [
        "Station", "Outpost", "Astrahus", "Fortizar", "Keepstar",
        "Raitaru", "Azbel", "Sotiyo", "Tatara", "Athanor", "Metenox",
        "Structure", "Engineering", "Citadel", "Refinery"
    ];

    public static readonly string[] NonDockableKeywords =
    [
        "Ship", "Pod", "Capsule", "Drone", "Fighter",
        "Gate", "Beacon", "Asteroid", "Cloud", "Wreck", "Rat",
    ];

    // ─── Distance parsing ───────────────────────────────────────────────────

    [GeneratedRegex(@"([\d.,]+)\s*(m|km|au)", RegexOptions.IgnoreCase)]
    public static partial Regex DistanceRegex();

    /// <summary>Parses an EVE distance string ("12.4 km", "500 m", "1.5 AU") to metres.</summary>
    public static double? ParseDistanceMeters(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = DistanceRegex().Match(text);
        if (!m.Success) return null;

        var valStr = m.Groups[1].Value.Replace(",", "").Replace(" ", "");
        if (!double.TryParse(valStr,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var val)) return null;

        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "km" => val * 1_000,
            "au" => val * 149_597_870_700.0,
            _    => val,
        };
    }
}
