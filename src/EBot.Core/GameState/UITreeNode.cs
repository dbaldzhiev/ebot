using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EBot.Core.GameState;

/// <summary>
/// Strips EVE Online's custom HTML-like markup from a display text string.
/// EVE embeds colour/font tags and tab markers in _setText / _text values.
///
/// Examples of stripped patterns:
///   &lt;color=#ff000000&gt;Jita&lt;/color&gt;   →  "Jita"
///   &lt;fontsize=12&gt;Jita&lt;/fontsize&gt;   →  "Jita"
///   &lt;t&gt;   Jita                      →  "Jita"   (leading tab marker)
///   &lt;br&gt;                           →  " "
/// </summary>
public static partial class EveTextUtil
{
    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();

    public static string? StripTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Contains("Failed to read string bytes", StringComparison.OrdinalIgnoreCase)) return null;
        // Replace <br> with space first
        var s = raw.Replace("<br>", " ", StringComparison.OrdinalIgnoreCase);
        s = TagRegex().Replace(s, "");
        s = s.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}

/// <summary>
/// Raw UI tree node as read from the Sanderling memory reading JSON.
/// This represents a single node in the EVE Online UI tree hierarchy.
/// </summary>
public sealed class UITreeNode
{
    [JsonPropertyName("pythonObjectTypeName")]
    public string PythonObjectTypeName { get; set; } = string.Empty;

    [JsonPropertyName("pythonObjectAddress")]
    public string PythonObjectAddress { get; set; } = string.Empty;

    [JsonPropertyName("dictEntriesOfInterest")]
    public Dictionary<string, JsonElement>? DictEntriesOfInterest { get; set; }

    [JsonPropertyName("otherDictEntriesKeys")]
    public List<string>? OtherDictEntriesKeys { get; set; }

    [JsonPropertyName("children")]
    public List<UITreeNode>? Children { get; set; }

    // ─── Dict Value Accessors ──────────────────────────────────────────

    /// <summary>
    /// Gets a dictionary entry value as string, if present.
    /// Handles both direct string values and complex objects.
    /// </summary>
    public string? GetDictString(string key)
    {
        if (DictEntriesOfInterest == null) return null;
        if (!DictEntriesOfInterest.TryGetValue(key, out var element)) return null;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            // Nested UITreeNode (Python object like Link) — extract visible text from its dict
            JsonValueKind.Object when element.TryGetProperty("dictEntriesOfInterest", out var dei) =>
                dei.TryGetProperty("_setText", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() :
                dei.TryGetProperty("_text",    out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() :
                null,
            // For objects like {"int": "...", "int_low32": 1234}, try _setText
            JsonValueKind.Object when element.TryGetProperty("_setText", out var setText) =>
                setText.GetString(),
            _ => element.GetRawText(),
        };
    }

    /// <summary>
    /// Gets a dictionary entry value as int.
    /// Handles direct numbers and the {"int": "...", "int_low32": N} format.
    /// </summary>
    public int? GetDictInt(string key)
    {
        if (DictEntriesOfInterest == null) return null;
        if (!DictEntriesOfInterest.TryGetValue(key, out var element)) return null;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i : null,
            JsonValueKind.String => int.TryParse(element.GetString(), out var i) ? i : null,
            // Complex object: {"int": "bignum", "int_low32": 2054}
            JsonValueKind.Object when element.TryGetProperty("int_low32", out var low32) =>
                low32.ValueKind == JsonValueKind.Number ? low32.GetInt32() : null,
            _ => null,
        };
    }

    /// <summary>
    /// Gets a dictionary entry value as double.
    /// Handles direct numbers and the {"int": "...", "int_low32": N} format.
    /// </summary>
    public double? GetDictDouble(string key)
    {
        if (DictEntriesOfInterest == null) return null;
        if (!DictEntriesOfInterest.TryGetValue(key, out var element)) return null;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => double.TryParse(element.GetString(), out var d) ? d : null,
            JsonValueKind.Object when element.TryGetProperty("int_low32", out var low32) =>
                low32.ValueKind == JsonValueKind.Number ? low32.GetDouble() : null,
            _ => null,
        };
    }

    /// <summary>
    /// Gets a dictionary entry as a bool.
    /// Handles JSON booleans, numbers (0=false), string "True"/"1", and
    /// the Sanderling {"bool": true} nested-object format for Python booleans.
    /// </summary>
    public bool? GetDictBool(string key)
    {
        if (DictEntriesOfInterest == null) return null;
        if (!DictEntriesOfInterest.TryGetValue(key, out var element)) return null;

        return element.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => element.GetDouble() != 0,
            JsonValueKind.String => element.GetString() switch
            {
                "True" or "true" or "1" => true,
                "False" or "false" or "0" => false,
                _ => null,
            },
            // {"bool": true} — Python bool encoded as object by Sanderling
            JsonValueKind.Object when element.TryGetProperty("bool", out var b) =>
                b.ValueKind == JsonValueKind.True ? true
                : b.ValueKind == JsonValueKind.False ? false
                : null,
            _ => null,
        };
    }

    /// <summary>
    /// Gets a color from dict entries. Color objects have aPercent, rPercent, gPercent, bPercent.
    /// </summary>
    public UIColor? GetDictColor(string key)
    {
        if (DictEntriesOfInterest == null) return null;
        if (!DictEntriesOfInterest.TryGetValue(key, out var element)) return null;
        if (element.ValueKind != JsonValueKind.Object) return null;

        return new UIColor
        {
            APercent = element.TryGetProperty("aPercent", out var a) ? a.GetDouble() : 0,
            RPercent = element.TryGetProperty("rPercent", out var r) ? r.GetDouble() : 0,
            GPercent = element.TryGetProperty("gPercent", out var g) ? g.GetDouble() : 0,
            BPercent = element.TryGetProperty("bPercent", out var b) ? b.GetDouble() : 0,
        };
    }

    /// <summary>
    /// Checks if a key exists in otherDictEntriesKeys.
    /// </summary>
    public bool HasOtherDictKey(string key) =>
        OtherDictEntriesKeys?.Contains(key) == true;

    /// <summary>
    /// Gets the raw JsonElement for a dict entry.
    /// </summary>
    public JsonElement? GetDictElement(string key)
    {
        if (DictEntriesOfInterest == null) return null;
        return DictEntriesOfInterest.TryGetValue(key, out var element) ? element : null;
    }
}

/// <summary>
/// A color value from the EVE UI, with percentages (0-100).
/// </summary>
public sealed class UIColor
{
    public double APercent { get; set; }
    public double RPercent { get; set; }
    public double GPercent { get; set; }
    public double BPercent { get; set; }
}

/// <summary>
/// A UI tree node annotated with its computed display region on screen.
/// </summary>
public sealed class UITreeNodeWithDisplayRegion
{
    public UITreeNode Node { get; init; } = null!;
    public DisplayRegion Region { get; init; } = new();
    public List<UITreeNodeWithDisplayRegion> Children { get; init; } = [];

    /// <summary>
    /// Gets a stable identifier for this node based on its type and name.
    /// Addresses are not always stable across frames, but names often are.
    /// </summary>
    public string StableId => $"{Node.PythonObjectTypeName}:{Node.GetDictString("_name") ?? "noname"}";

    /// <summary>
    /// Checks if the node has a valid display region (non-zero size).
    /// </summary>
    public bool IsVisible => Region.Width > 0 && Region.Height > 0;

    /// <summary>
    /// Gets the center point of this node's display region  (useful for clicking).
    /// </summary>
    public (int X, int Y) Center =>
        (Region.X + Region.Width / 2, Region.Y + Region.Height / 2);

    /// <summary>
    /// Finds the first descendant matching a query, or null.
    /// </summary>
    public UITreeNodeWithDisplayRegion? QueryFirst(string selector)
    {
        return UIQuery.Parse(selector).MatchFirst(this);
    }

    /// <summary>
    /// Finds all descendants matching a query.
    /// </summary>
    public IEnumerable<UITreeNodeWithDisplayRegion> QueryAll(string selector)
    {
        return UIQuery.Parse(selector).MatchAll(this);
    }

    /// <summary>
    /// Recursively finds all descendant nodes matching a predicate.
    /// </summary>
    public IEnumerable<UITreeNodeWithDisplayRegion> FindAll(Func<UITreeNodeWithDisplayRegion, bool> predicate)
    {
        if (predicate(this)) yield return this;
        foreach (var child in Children)
        {
            foreach (var match in child.FindAll(predicate))
                yield return match;
        }
    }

    /// <summary>
    /// Finds the first descendant matching a predicate, or null.
    /// </summary>
    public UITreeNodeWithDisplayRegion? FindFirst(Func<UITreeNodeWithDisplayRegion, bool> predicate)
    {
        if (predicate(this)) return this;
        foreach (var child in Children)
        {
            var found = child.FindFirst(predicate);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Returns all display text contained in this node and descendants.
    /// EVE HTML tags (&lt;color=...&gt;, &lt;t&gt;, etc.) are stripped automatically.
    /// </summary>
    public IEnumerable<string> GetAllContainedDisplayTexts()
    {
        var setText = EveTextUtil.StripTags(Node.GetDictString("_setText"));
        if (setText != null) yield return setText;

        var text = EveTextUtil.StripTags(Node.GetDictString("_text"));
        if (text != null) yield return text;

        var hint = EveTextUtil.StripTags(Node.GetDictString("_hint"));
        if (hint != null) yield return hint;

        foreach (var child in Children)
            foreach (var t in child.GetAllContainedDisplayTexts())
                yield return t;
    }
}

/// <summary>
/// A rectangular display region in screen coordinates.
/// </summary>
public sealed class DisplayRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool Contains(int px, int py) =>
        px >= X && px < X + Width && py >= Y && py < Y + Height;
}
