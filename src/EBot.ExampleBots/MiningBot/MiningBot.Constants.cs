using EBot.Core.GameState;

namespace EBot.ExampleBots.MiningBot;

public sealed partial class MiningBot
{
    // ─── Internal constants ────────────────────────────────────────────────

    /// <summary>Approach threshold: if nearest asteroid is farther than this, approach it. Covers most mining lasers.</summary>
    private const double DefaultLaserRangeM = 15_000;

    /// <summary>Capacitor % below which the bot waits before acting.</summary>
    private const int MinCapPct = 15;

    // ─── Ore value ranking (higher = more valuable) ───────────────────────────

    private static readonly Dictionary<string, int> _oreValue =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "scordite",   1 },
            { "plagioclase",2 },
            { "veldspar",   3 },
            { "pyroxeres",  4 },
            { "bistot",     5 },
            { "omber",      6 },
            { "jaspet",     7 },
            { "hedbergite", 8 },
            { "crokite",    9 },
            { "hemorphite", 10 },
            { "kernite",    11 },
            { "arkonor",    12 },
            { "gneiss",     13 },
            { "dark ochre", 14 },
            { "ochre",      14 },
            { "mercoxit",   15 },
            { "spodumain",  16 },
        };

    public static readonly string[] MainOreTypes =
    [
        "Veldspar", "Scordite", "Pyroxeres", "Plagioclase", "Omber", "Kernite", 
        "Jaspet", "Hemorphite", "Hedbergite", "Spodumain", "Crokite", "Bistot", 
        "Arkonor", "Mercoxit", "Gneiss", "Dark Ochre"
    ];

    private static readonly string[] _asteroidKeywords =
    [
        "asteroid",
        "veldspar", "scordite", "pyroxeres", "plagioclase",
        "omber", "kernite", "jaspet", "hemorphite", "hedbergite",
        "spodumain", "crokite", "bistot", "arkonor", "mercoxit",
        "gneiss", "dark ochre", "ochre",
        "cobaltite", "euxenite", "scheelite", "titanite",
        "chromite", "otavite", "sperrylite", "vanadinite",
        "carnotite", "zircon", "loparite", "monazite",
        "bezdnacine", "rakovene", "ducinium", "eifyrium", "talassonite",
    ];
}
