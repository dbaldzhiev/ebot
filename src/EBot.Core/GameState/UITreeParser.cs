using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace EBot.Core.GameState;

/// <summary>
/// Parses the raw Sanderling JSON memory reading into typed <see cref="ParsedUI"/> models.
/// </summary>
public sealed class UITreeParser
{
    private readonly ILogger<UITreeParser> _logger;

    public UITreeParser(ILogger<UITreeParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses a Sanderling JSON memory reading into a fully typed ParsedUI.
    /// </summary>
    public ParsedUI Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var rawRoot = JsonSerializer.Deserialize<UITreeNode>(json, JsonOptions);
        if (rawRoot == null)
        {
            _logger.LogWarning("Failed to deserialize UI tree root from JSON");
            return new ParsedUI();
        }

        // Build the annotated tree with display regions
        var root = BuildAnnotatedTree(rawRoot, null);

        // Extract typed UI elements
        return new ParsedUI
        {
            UITree = root,
            ContextMenus = FindContextMenus(root),
            ShipUI = FindShipUI(root),
            Targets = FindTargets(root),
            InfoPanelContainer = FindInfoPanelContainer(root),
            OverviewWindows = FindOverviewWindows(root),
            SelectedItemWindow = FindSelectedItemWindow(root),
            DronesWindow = FindDronesWindow(root),
            InventoryWindows = FindInventoryWindows(root),
            ChatWindowStacks = FindChatWindowStacks(root),
            ModuleButtonTooltip = FindModuleButtonTooltip(root),
            Neocom = FindNeocom(root),
            MessageBoxes = FindMessageBoxes(root),
            StationWindow = FindStationWindow(root),
            ProbeScannerWindow = FindProbeScannerWindow(root),
            MiningScanResultsWindow = FindMiningScanResultsWindow(root),
            CombatMessages = FindCombatMessages(root),
        };
    }

    private List<string> FindCombatMessages(UITreeNodeWithDisplayRegion root)
    {
        return root.FindAll(n => IsType(n, "CombatMessage", "CombatLog"))
            .SelectMany(n => n.GetAllContainedDisplayTexts())
            .Where(t => t.Length > 5)
            .ToList();
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Tree Building
    // ────────────────────────────────────────────────────────────────────────

    private UITreeNodeWithDisplayRegion BuildAnnotatedTree(UITreeNode raw, DisplayRegion? parentRegion)
    {
        var region = ComputeDisplayRegion(raw, parentRegion);
        var annotated = new UITreeNodeWithDisplayRegion
        {
            Node = raw,
            Region = region,
        };

        if (raw.Children != null)
        {
            foreach (var child in raw.Children)
            {
                annotated.Children.Add(BuildAnnotatedTree(child, region));
            }
        }

        return annotated;
    }

    private static DisplayRegion ComputeDisplayRegion(UITreeNode node, DisplayRegion? parent)
    {
        var x = (int)(node.GetDictDouble("_displayX") ?? 0);
        var y = (int)(node.GetDictDouble("_displayY") ?? 0);
        var w = (int)(node.GetDictDouble("_displayWidth") ?? node.GetDictDouble("_width") ?? 0);
        var h = (int)(node.GetDictDouble("_displayHeight") ?? node.GetDictDouble("_height") ?? 0);

        if (parent != null)
        {
            x += parent.X;
            y += parent.Y;
        }

        return new DisplayRegion { X = x, Y = y, Width = w, Height = h };
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Element Finders
    // ────────────────────────────────────────────────────────────────────────

    private List<ContextMenu> FindContextMenus(UITreeNodeWithDisplayRegion root)
    {
        // EVE's context menu container Python type name is exactly "ContextMenu" (Elm reference).
        // Submenus may also appear as separate ContextMenu or ContextSubMenu nodes (EVE creates them dynamically).
        // Entries are "MenuEntryView" at any descendant depth.
        return root.FindAll(n => IsType(n, "ContextMenu", "ContextSubMenu"))
            .Select(n => new ContextMenu
            {
                UINode = n,
                Entries = n.QueryAll("@MenuEntryView")
                    .Select(c => new ContextMenuEntry
                    {
                        UINode = c,
                        // Skip single-char glyphs (▶, ►, etc.) — prefer any text ≥2 chars with a letter.
                        Text = c.GetAllContainedDisplayTexts()
                                 .FirstOrDefault(t => t.Length >= 2 && t.Any(char.IsLetter))
                               ?? c.GetAllContainedDisplayTexts().FirstOrDefault(),
                        IsHighlighted = c.Node.GetDictBool("_hilite") ?? false,
                    })
                    .ToList(),
            })
            .ToList();
    }

    private ShipUI? FindShipUI(UITreeNodeWithDisplayRegion root)
    {
        var node = root.QueryFirst("@ShipUI");
        if (node == null) return null;

        // Each ShipSlot contains a ModuleButton child — pair them for sprite-based state detection
        var slotNodes = node.QueryAll("@ShipSlot")
            .Concat(node.QueryAll("@inFlightHighSlot"))
            .Concat(node.QueryAll("@inFlightMediumSlot"))
            .Concat(node.QueryAll("@inFlightLowSlot"))
            .Distinct();

        var moduleButtons = slotNodes
            .Select(slot =>
            {
                var btn = slot.QueryFirst("@ModuleButton") ?? slot;
                return BuildModuleButton(btn, slot);
            })
            .ToList();

        // Speed text: currentVelocityLabel or SpeedGauge label
        var speedText = node.QueryFirst("[_name=currentVelocityLabel]")?.GetAllContainedDisplayTexts().FirstOrDefault()
            ?? node.QueryFirst("@SpeedGauge")?.GetAllContainedDisplayTexts().FirstOrDefault();

        return new ShipUI
        {
            UINode = node,
            Capacitor = FindCapacitor(node),
            HitpointsPercent = FindHitpoints(node),
            Indication = FindIndication(node),
            ModuleButtons = moduleButtons,
            ModuleButtonsRows = ClassifyModuleRows(moduleButtons, node),
            StopButton = node.QueryFirst("@StopButton"),
            MaxSpeedButton = node.QueryFirst("@MaxSpeedButton"),
            SpeedText = speedText,
        };
    }

    private ShipUICapacitor? FindCapacitor(UITreeNodeWithDisplayRegion shipUI)
    {
        var capNode = shipUI.QueryFirst("@*Capacitor*");
        if (capNode == null) return null;

        var pmarks = capNode.QueryAll("@Pmark").ToList();
        int? levelPercent = null;

        if (pmarks.Count > 0)
        {
            // EVE's capacitor ring: filled (lit) cells have LOW _color alpha; empty (dark) cells have HIGH alpha.
            // Strategy 1 — use alpha of the _color dict entry (most reliable).
            var withColor = pmarks.Where(p => p.Node.GetDictColor("_color") != null).ToList();
            if (withColor.Count > 0)
            {
                var unlit = withColor.Count(p => p.Node.GetDictColor("_color")!.APercent > 30);
                levelPercent = (int)Math.Round((double)(withColor.Count - unlit) / withColor.Count * 100);
            }
            else
            {
                // Strategy 2 — use opacity/_opacity attribute (some EVE client versions)
                var withOpacity = pmarks
                    .Select(p => p.Node.GetDictDouble("_opacity") ?? p.Node.GetDictDouble("opacity"))
                    .Where(o => o.HasValue)
                    .ToList();
                if (withOpacity.Count > 0)
                {
                    // Low opacity = empty, high opacity = lit
                    var lit = withOpacity.Count(o => o!.Value > 0.5);
                    levelPercent = (int)Math.Round((double)lit / withOpacity.Count * 100);
                }
                else
                {
                    // Strategy 3 — display text fallback ("75%" or "75 %")
                    var pct = capNode.GetAllContainedDisplayTexts()
                        .Select(t => t.Replace(" ", "").Replace("%", ""))
                        .FirstOrDefault(t => int.TryParse(t, out _));
                    if (pct != null && int.TryParse(pct, out var parsed))
                        levelPercent = Math.Clamp(parsed, 0, 100);
                    else
                        // Last resort: count pmarks and assume full
                        levelPercent = pmarks.Count > 0 ? 100 : null;
                }
            }
        }

        return new ShipUICapacitor { UINode = capNode, LevelPercent = levelPercent };
    }

    private HitpointsPercent? FindHitpoints(UITreeNodeWithDisplayRegion container)
    {
        // Elm reference: find descendant whose _name == exact gauge name, read _lastValue (0.0–1.0)
        var shield    = GetGaugePercent(container, "shieldGauge");
        var armor     = GetGaugePercent(container, "armorGauge");
        var structure = GetGaugePercent(container, "structureGauge");

        if (shield == null && armor == null && structure == null) return null;

        return new HitpointsPercent
        {
            Shield    = shield    ?? 100,
            Armor     = armor     ?? 100,
            Structure = structure ?? 100,
        };
    }

    private static int? GetGaugePercent(UITreeNodeWithDisplayRegion container, string gaugeName)
    {
        var gauge = container.QueryFirst($"[_name={gaugeName}]");
        if (gauge == null) return null;

        var lastValue = gauge.Node.GetDictDouble("_lastValue");
        if (lastValue.HasValue)
            return (int)Math.Round(lastValue.Value * 100);

        // Fallback: text like "75%"
        var text = gauge.GetAllContainedDisplayTexts().LastOrDefault();
        if (text != null && int.TryParse(text.Replace("%", "").Trim(), out var pct))
            return pct;
        return null;
    }

    private ShipUIIndication? FindIndication(UITreeNodeWithDisplayRegion shipUI)
    {
        var node = shipUI.QueryFirst("@*Indication*");
        if (node == null) return null;

        return new ShipUIIndication
        {
            UINode = node,
            ManeuverType = node.GetAllContainedDisplayTexts().FirstOrDefault(),
        };
    }

    private static ShipUIModuleButton BuildModuleButton(
        UITreeNodeWithDisplayRegion moduleButton,
        UITreeNodeWithDisplayRegion slotNode)
    {
        // ramp_active: primary bool indicator that the module is cycling
        var isActiveFromAttr = moduleButton.Node.GetDictBool("ramp_active");

        // Sprite-based fallback for all states (EVE renders state via named sprites on the slot)
        bool HasSprite(UITreeNodeWithDisplayRegion container, params string[] names) =>
            names.Any(name =>
                container.FindFirst(n =>
                    (n.Node.PythonObjectTypeName.Contains("Sprite", StringComparison.OrdinalIgnoreCase)
                     || n.Node.PythonObjectTypeName.Contains("Icon", StringComparison.OrdinalIgnoreCase))
                    && string.Equals(n.Node.GetDictString("_name"), name, StringComparison.OrdinalIgnoreCase)) != null);

        // "active" state: ramp_active flag OR a visible ramp/active sprite with non-zero opacity
        bool spriteActive = HasSprite(slotNode, "ramp", "activeRamp", "active", "activation")
                         || HasSprite(moduleButton, "ramp", "activeRamp");

        bool? isActive = isActiveFromAttr ?? (spriteActive ? true : null);

        // "busy" = transitioning between on/off (activation/deactivation in progress)
        bool isBusy = HasSprite(slotNode, "busy", "deactivation")
                   || HasSprite(moduleButton, "busy");

        // "overloaded" = overheating
        bool isOverloaded = HasSprite(slotNode, "overheat", "overload", "overloadRamp", "overheatRamp")
                         || HasSprite(moduleButton, "overheat", "overload");

        // "offline" = module is offline (greyed out, usually a separate sprite or color)
        bool isOffline = HasSprite(slotNode, "offline", "offlineModule")
                      || HasSprite(moduleButton, "offline")
                      || (moduleButton.Node.GetDictBool("isOffline") == true);

        // If overloaded also implies active cycling
        if (isOverloaded && isActive == null) isActive = true;

        // Module name: EVE stores the name in the _hint attribute of the slot or button.
        // Strip "Slot N: " prefix that EVE sometimes prepends.
        var rawHint = EveTextUtil.StripTags(moduleButton.Node.GetDictString("_hint"))
                   ?? EveTextUtil.StripTags(slotNode.Node.GetDictString("_hint"));
        string? moduleName = null;
        if (rawHint != null)
        {
            var colon = rawHint.IndexOf(':');
            moduleName = colon >= 0 ? rawHint[(colon + 1)..].Trim() : rawHint.Trim();
            if (moduleName.Length == 0) moduleName = null;
        }

        // Fallback if hint is missing: use node name suffix (type ID)
        if (moduleName == null)
        {
            var nodeName = moduleButton.Node.GetDictString("_name") ?? "";
            if (nodeName.Contains('_'))
            {
                var id = nodeName.Split('_').Last();
                moduleName = id switch
                {
                    "482"   => "Mining Laser",  // Miner I (old typeID, kept for safety)
                    "483"   => "Mining Laser",  // Miner I
                    "578"   => "Mining Laser",  // Miner II
                    "487"   => "Mining Laser",  // Deep Core Miner I
                    "12108" => "Mining Laser",  // Deep Core Miner II
                    "22229" => "Mining Laser",  // Ice Harvester I
                    "22231" => "Mining Laser",  // Ice Harvester II
                    "17911" => "Mining Laser",  // Modulated Strip Miner I
                    "17912" => "Mining Laser",  // Modulated Strip Miner II
                    "16277" => "Mining Laser",  // Strip Miner I
                    "17913" => "Mining Laser",  // Modulated Deep Core Strip Miner I
                    "17914" => "Mining Laser",  // Modulated Deep Core Strip Miner II
                    "6001"  => "Afterburner",
                    "6003"  => "Afterburner",   // 1MN Monopropellant I
                    "6004"  => "Afterburner",   // 1MN Afterburner II
                    "6005"  => "Afterburner",   // 5MN Microwarpdrive I
                    "6006"  => "Microwarpdrive",
                    "6002"  => "Microwarpdrive",
                    "2054"  => "Afterburner",
                    _       => $"Module {id}"
                };
            }
        }

        // Last resort: identify by the icon texture path inside the button.
        // EVE icon group 12 = mining-related resources; group 6 = engineering/propulsion.
        if (moduleName == null || moduleName.StartsWith("Module "))
        {
            var iconTex = moduleButton.FindFirst(n =>
                !string.IsNullOrEmpty(n.Node.GetDictString("_texturePath")))
                ?.Node.GetDictString("_texturePath") ?? "";
            if (iconTex.Contains("/icons/12_64_", StringComparison.OrdinalIgnoreCase) ||
                iconTex.Contains("miningLaser",   StringComparison.OrdinalIgnoreCase) ||
                iconTex.Contains("iceHarvester",  StringComparison.OrdinalIgnoreCase))
                moduleName = "Mining Laser";
        }

        return new ShipUIModuleButton
        {
            UINode = slotNode,
            SlotNode = slotNode,
            Name = moduleName,
            IsActive = isActive,
            IsHiliteVisible = HasSprite(slotNode, "hilite", "hiliteSprite"),
            IsBusy = isBusy,
            IsOverloaded = isOverloaded,
            IsOffline = isOffline,
            RampRotationMilli = moduleButton.Node.GetDictInt("ramp_rotationMilli"),
        };
    }

    private static ShipUIModuleButtonRows ClassifyModuleRows(
        List<ShipUIModuleButton> buttons, UITreeNodeWithDisplayRegion shipUI)
    {
        if (buttons.Count == 0) return new ShipUIModuleButtonRows();

        // Primary: classify by slot _name (inFlightHighSlot*, inFlightMediumSlot*, inFlightLowSlot*, RigSlot*)
        static bool IsHighSlot(ShipUIModuleButton b)
        {
            var n = b.UINode.Node.GetDictString("_name") ?? "";
            return n.Contains("HighSlot",      StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("inFlightHigh",  StringComparison.OrdinalIgnoreCase);
        }
        static bool IsMidSlot(ShipUIModuleButton b)
        {
            var n = b.UINode.Node.GetDictString("_name") ?? "";
            return n.Contains("MediumSlot",    StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("MidSlot",       StringComparison.OrdinalIgnoreCase) ||
                   n.Contains("inFlightMedium",StringComparison.OrdinalIgnoreCase);
        }

        var top    = buttons.Where(IsHighSlot).ToList();
        var middle = buttons.Where(IsMidSlot).ToList();
        var bottom = buttons.Where(b => !IsHighSlot(b) && !IsMidSlot(b)).ToList();

        // Fallback if names not available: sort by Y, split into thirds
        if (top.Count == 0 && middle.Count == 0 && buttons.Count > 0)
        {
            var sorted = buttons.OrderBy(b => b.UINode.Region.Y).ToList();
            int third  = Math.Max(1, sorted.Count / 3);
            top    = sorted.Take(third).ToList();
            middle = sorted.Skip(third).Take(third).ToList();
            bottom = sorted.Skip(third * 2).ToList();
        }

        return new ShipUIModuleButtonRows { Top = top, Middle = middle, Bottom = bottom };
    }

    private List<Target> FindTargets(UITreeNodeWithDisplayRegion root)
    {
        return root.QueryAll("@TargetInBar")
            .Concat(root.QueryAll("@Target"))
            .Distinct()
            .Select(n =>
            {
                var allTexts = n.GetAllContainedDisplayTexts().ToList();
                var label = allTexts.FirstOrDefault(t => !EveConstants.DistanceRegex().IsMatch(t) && !t.EndsWith('%'));
                var distText = allTexts.FirstOrDefault(t => EveConstants.DistanceRegex().IsMatch(t));

                return new Target
                {
                    UINode           = n,
                    TextLabel        = label ?? allTexts.FirstOrDefault(),
                    DistanceText     = distText,
                    DistanceInMeters = distText != null ? ParseDistanceText(distText) : null,
                    HitpointsPercent = FindHitpoints(n),
                    IsActiveTarget   = n.Node.GetDictString("isActiveTarget") is "True" or "1",
                };
            })
            .ToList();
    }

    private InfoPanelContainer? FindInfoPanelContainer(UITreeNodeWithDisplayRegion root)
    {
        var node = root.FindFirst(n => IsType(n, "InfoPanelContainer"));
        if (node == null) return null;

        return new InfoPanelContainer
        {
            UINode = node,
            InfoPanelRoute = FindInfoPanelRoute(node),
            InfoPanelLocationInfo = FindInfoPanelLocationInfo(node),
        };
    }

    private InfoPanelRoute? FindInfoPanelRoute(UITreeNodeWithDisplayRegion container)
    {
        var node = container.FindFirst(n => IsType(n, "InfoPanelRoute"));
        if (node == null) return null;

        return new InfoPanelRoute
        {
            UINode = node,
            RouteElementMarkers = node.FindAll(n => IsType(n, "AutopilotDestination", "RouteMarker"))
                .ToList(),
        };
    }

    private InfoPanelLocationInfo? FindInfoPanelLocationInfo(UITreeNodeWithDisplayRegion container)
    {
        var node = container.FindFirst(n => IsType(n, "InfoPanelLocationInfo"));
        if (node == null) return null;

        // System name — try several known _name patterns from EVE client versions
        static bool IsSystemNameLabel(UITreeNodeWithDisplayRegion n)
        {
            var name = n.Node.GetDictString("_name") ?? "";
            return name.Contains("SystemName",   StringComparison.OrdinalIgnoreCase)
                || name.Contains("systemname",   StringComparison.OrdinalIgnoreCase)
                || name.Contains("solarSystem",  StringComparison.OrdinalIgnoreCase)
                || name.Equals("labelSystemName", StringComparison.OrdinalIgnoreCase)
                || name.Equals("currentSolarSystemName", StringComparison.OrdinalIgnoreCase);
        }

        var sysNode = node.FindFirst(IsSystemNameLabel);
        var systemName = sysNode != null
            ? (EveTextUtil.StripTags(sysNode.Node.GetDictString("_setText"))
               ?? EveTextUtil.StripTags(sysNode.Node.GetDictString("_text"))
               ?? sysNode.GetAllContainedDisplayTexts().FirstOrDefault())
            : null;

        // If not found by name, take the first non-numeric, non-security-status text in the panel
        systemName ??= node.GetAllContainedDisplayTexts()
            .FirstOrDefault(t => t.Length > 1
                && !double.TryParse(t.Replace(",", "."), out _)
                && !t.Contains("sec", StringComparison.OrdinalIgnoreCase));

        // Security status
        static bool IsSecurityLabel(UITreeNodeWithDisplayRegion n)
        {
            var name = n.Node.GetDictString("_name") ?? "";
            return name.Contains("SecStatus",  StringComparison.OrdinalIgnoreCase)
                || name.Contains("security",   StringComparison.OrdinalIgnoreCase)
                || name.Contains("secStatus",  StringComparison.OrdinalIgnoreCase)
                || name.Equals("labelSecurity", StringComparison.OrdinalIgnoreCase);
        }

        var secNode = node.FindFirst(IsSecurityLabel);
        var secText = secNode != null
            ? (EveTextUtil.StripTags(secNode.Node.GetDictString("_setText"))
               ?? EveTextUtil.StripTags(secNode.Node.GetDictString("_text"))
               ?? secNode.GetAllContainedDisplayTexts().FirstOrDefault())
            : null;

        var nearestNode = node.QueryFirst("[_name=nearestLocationInfo]");
        var nearestLocationName = nearestNode != null
            ? (EveTextUtil.StripTags(nearestNode.Node.GetDictString("_setText"))
               ?? EveTextUtil.StripTags(nearestNode.Node.GetDictString("_text"))
               ?? nearestNode.GetAllContainedDisplayTexts().FirstOrDefault())
            : null;

        return new InfoPanelLocationInfo
        {
            UINode             = node,
            SystemName         = systemName,
            SecurityStatusText = secText,
            NearestLocationName = nearestLocationName,
        };
    }

    private List<OverviewWindow> FindOverviewWindows(UITreeNodeWithDisplayRegion root)
    {
        return root.QueryAll("@OverView")
            .Concat(root.QueryAll("@OverviewWindow"))
            .Distinct()
            .Select(BuildOverviewWindow)
            .ToList();
    }

    private OverviewWindow BuildOverviewWindow(UITreeNodeWithDisplayRegion overviewNode)
    {
        // Extract column headers
        var headers = new List<(string Name, int X)>();
        var sortHeaders = overviewNode.QueryFirst("@SortHeaders") ?? overviewNode.QueryFirst("[_name=headers]");

        if (sortHeaders != null)
        {
            var headerNodes = sortHeaders.QueryAll("@Header");
            foreach (var headerNode in headerNodes)
            {
                var text = headerNode.GetAllContainedDisplayTexts()
                    .FirstOrDefault(t => t.Length >= 2 && t.Any(char.IsLetter));

                if (!string.IsNullOrEmpty(text))
                    headers.Add((text!, headerNode.Region.X));
            }
        }

        // Fallback: search for any nodes of type Header directly in the overview
        if (headers.Count == 0)
        {
            var headerNodes = overviewNode.QueryAll("@Header");
            foreach (var headerNode in headerNodes)
            {
                var text = headerNode.GetAllContainedDisplayTexts()
                    .FirstOrDefault(t => t.Length >= 2 && t.Any(char.IsLetter));

                if (!string.IsNullOrEmpty(text))
                    headers.Add((text!, headerNode.Region.X));
            }
        }

        var entries = overviewNode.QueryAll("@OverviewScrollEntry")
            .Select(e => BuildOverviewEntry(e, headers))
            .ToList();

        var tabs = overviewNode.QueryAll("@OverviewTab")
            .Select(tab => {
                var name = tab.GetAllContainedDisplayTexts().FirstOrDefault();
                
                // Detection 1: explicit attributes
                bool isActive = tab.Node.GetDictBool("_selected") == true
                    || tab.Node.GetDictBool("selected") == true
                    || (tab.Node.GetDictString("_state") ?? "").Contains("selected", StringComparison.OrdinalIgnoreCase);
                
                // Detection 2: label alpha (Active tabs have more opaque labels, usually ~90% vs ~50%)
                if (!isActive)
                {
                    var label = tab.FindFirst(n => n.Node.PythonObjectTypeName.Contains("Label", StringComparison.OrdinalIgnoreCase));
                    var color = label?.Node.GetDictColor("_color");
                    if (color != null && color.APercent > 70) isActive = true;
                }

                return new OverviewTab { UINode = tab, Name = name, IsActive = isActive };
            })
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .ToList();

        return new OverviewWindow
        {
            UINode = overviewNode,
            WindowName = overviewNode.Node.GetDictString("_name"),
            ColumnHeaders = headers.Select(h => h.Name).ToList(),
            Entries = entries,
            Tabs = tabs,
        };
    }

    private OverviewEntry BuildOverviewEntry(
        UITreeNodeWithDisplayRegion n,
        List<(string Name, int X)> columnHeaders)
    {
        var texts = n.GetAllContainedDisplayTexts().ToList();
        var distText = texts.FirstOrDefault(t => EveConstants.DistanceRegex().IsMatch(t));

        // Build cells dictionary by matching text-bearing nodes to column headers by X position
        var cellsTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Collect (cleanText, absoluteX) pairs for all text-bearing leaf nodes
        var textNodes = CollectTextNodesWithX(n).ToList();

        if (columnHeaders.Count > 0)
        {
            foreach (var (rawText, cellX) in textNodes)
            {
                if (string.IsNullOrWhiteSpace(rawText)) continue;
                var clean = rawText.Trim();
                // Find nearest column header — use generous tolerance because EVE's cell text
                // nodes are often indented a few pixels relative to the column header left edge.
                var best = columnHeaders.MinBy(h => Math.Abs(h.X - cellX));
                if (best != default && Math.Abs(best.X - cellX) <= 80)
                    cellsTexts.TryAdd(best.Name, clean);
            }
        }

        // Resolve semantic fields from column map (EVE uses "Name", "Type", "Ship Type", etc.)
        cellsTexts.TryGetValue("Name", out var name);
        if (string.IsNullOrEmpty(name)) cellsTexts.TryGetValue("Label", out name);

        // Fallback when column matching failed: leftmost text node is the name
        // (the Name column is always the leftmost non-icon column in EVE's overview)
        if (string.IsNullOrEmpty(name) && textNodes.Count > 0)
        {
            var leftmost = textNodes
                .Where(p => !EveConstants.DistanceRegex().IsMatch(p.Text) && !string.IsNullOrWhiteSpace(p.Text))
                .OrderBy(p => p.X)
                .FirstOrDefault();
            name = leftmost.Text?.Trim();
        }

        cellsTexts.TryGetValue("Type", out var objectType);
        if (string.IsNullOrEmpty(objectType)) cellsTexts.TryGetValue("Ship Type", out objectType);
        if (string.IsNullOrEmpty(objectType)) cellsTexts.TryGetValue("Category", out objectType);
        if (string.IsNullOrEmpty(objectType)) cellsTexts.TryGetValue("Entity Type", out objectType);
        if (string.IsNullOrEmpty(objectType)) cellsTexts.TryGetValue("Ship", out objectType);
        if (string.IsNullOrEmpty(objectType))
        {
            // Last-resort: any column header that contains "type" or "category" (case-insensitive)
            var typeCol = cellsTexts.Keys.FirstOrDefault(k =>
                k.Contains("type",     StringComparison.OrdinalIgnoreCase) ||
                k.Contains("category", StringComparison.OrdinalIgnoreCase));
            if (typeCol != null) cellsTexts.TryGetValue(typeCol, out objectType);
        }

        cellsTexts.TryGetValue("Distance", out var cellDistText);
        var resolvedDistText = !string.IsNullOrEmpty(cellDistText) ? cellDistText : distText;

        // IsAttackingMe: node named "attackingMe" (BlinkingSpriteOnSharedCurve) OR
        // hostileBracket texture, OR hint containing "attacking"
        bool isAttackingMe = n.FindFirst(e => {
            var nodeName = e.Node.GetDictString("_name") ?? "";
            if (nodeName.Equals("attackingMe", StringComparison.OrdinalIgnoreCase)) return true;
            var tex = e.Node.GetDictString("_texturePath") ?? "";
            if (tex.Contains("hostileBracket", StringComparison.OrdinalIgnoreCase)) return true;
            var hint = e.Node.GetDictString("_hint") ?? "";
            if (hint.Contains("attacking", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }) != null;

        // IsHostile: hint keywords, NPC/hostile textures, or red icon color
        bool isHostile = isAttackingMe || n.FindFirst(e => {
            var hint = (e.Node.GetDictString("_hint") ?? "").ToLowerInvariant();
            if (hint.Contains("hostile") || hint.Contains("threat") ||
                hint.Contains("attacking") || hint.Contains("criminal") ||
                hint.Contains("suspect") || hint.Contains("outlaw")) return true;
            var tex = (e.Node.GetDictString("_texturePath") ?? "").ToLowerInvariant();
            if (tex.Contains("hostile") || tex.Contains("npcfrigate") ||
                tex.Contains("npccruiser") || tex.Contains("npcbattleship") ||
                tex.Contains("npcdestroyer")) return true;
            var color = e.Node.GetDictColor("_bgColor") ?? e.Node.GetDictColor("_color");
            if (color != null && color.RPercent > 70 && color.GPercent < 30 && color.BPercent < 30) return true;
            return false;
        }) != null;

        return new OverviewEntry
        {
            UINode = n,
            CellsTexts = cellsTexts,
            Name = name,
            ObjectType = objectType,
            DistanceText = resolvedDistText,
            DistanceInMeters = resolvedDistText != null ? ParseDistanceText(resolvedDistText) : null,
            IsAttackingMe = isAttackingMe,
            IsHostile = isHostile,
            Texts = texts,
        };
    }

    /// <summary>
    /// Walks up to 4 levels deep collecting (text, absoluteX) for nodes that have their own text.
    /// Stops recursing into a node once it yields its own text (leaf-of-interest pattern).
    /// </summary>
    private static IEnumerable<(string Text, int X)> CollectTextNodesWithX(
        UITreeNodeWithDisplayRegion node, int depth = 0)
    {
        if (depth > 6) yield break;

        var setText = EveTextUtil.StripTags(node.Node.GetDictString("_setText"));
        if (setText != null) { yield return (setText, node.Region.X); yield break; }

        var text = EveTextUtil.StripTags(node.Node.GetDictString("_text"));
        if (text != null) { yield return (text, node.Region.X); yield break; }

        foreach (var child in node.Children)
            foreach (var pair in CollectTextNodesWithX(child, depth + 1))
                yield return pair;
    }

    private SelectedItemWindow? FindSelectedItemWindow(UITreeNodeWithDisplayRegion root)
    {
        var node = root.QueryFirst("@SelectedItemWnd") ?? root.QueryFirst("@ActiveItem");
        if (node == null) return null;

        return new SelectedItemWindow
        {
            UINode = node,
            ActionButtons = node.QueryAll("@ButtonIcon")
                .Concat(node.QueryAll("@ActionButton"))
                .Distinct()
                .ToList(),
        };
    }

    private DronesWindow? FindDronesWindow(UITreeNodeWithDisplayRegion root)
    {
        var node = root.QueryFirst("@DroneView") ?? root.QueryFirst("@DronesWindow");
        if (node == null) return null;

        return new DronesWindow
        {
            UINode = node,
            DronesInBay   = FindDronesGroup(node, "DroneGroupHeaderInBay",   "bay",   "droneBay"),
            DronesInSpace = FindDronesGroup(node, "DroneGroupHeaderInSpace",  "space", "droneSpace", "local"),
        };
    }

    private DronesGroup? FindDronesGroup(UITreeNodeWithDisplayRegion parent, string primaryTypeName, params string[] keywordFallback)
    {
        // Primary: match by PythonObjectTypeName — avoids self-matching on the parent DronesWindow
        // because FindFirst checks `this` before children and the parent's recursive text contains
        // descendant keywords (e.g. "bay"), causing it to return the window node instead of the header.
        var groupNode = parent.FindFirst(n =>
            string.Equals(n.Node.PythonObjectTypeName, primaryTypeName, StringComparison.OrdinalIgnoreCase));

        // Fallback: keyword search, but explicitly skip the parent node to avoid self-match.
        groupNode ??= parent.FindFirst(n =>
            !ReferenceEquals(n, parent) &&
            keywordFallback.Any(k => n.GetAllContainedDisplayTexts()
                .Any(t => t.Contains(k, StringComparison.OrdinalIgnoreCase))));

        if (groupNode == null) return null;

        // Read the header text from the node's own direct text fields, not recursively,
        // so we get "Drones in Bay (8)" and not the window-level "Drones" label.
        var headerText = groupNode.Node.GetDictString("_setText")
                      ?? groupNode.Node.GetDictString("_text")
                      ?? groupNode.GetAllContainedDisplayTexts().FirstOrDefault();
        int? current = null, max = null;

        if (headerText != null)
        {
            // Parse "Drones in Space (5/5)"
            var match = Regex.Match(headerText, @"\((\d+)/(\d+)\)");
            if (match.Success)
            {
                current = int.Parse(match.Groups[1].Value);
                max = int.Parse(match.Groups[2].Value);
            }
            else
            {
                // Parse "Drones in Bay (2)"
                match = Regex.Match(headerText, @"\((\d+)\)");
                if (match.Success)
                    current = int.Parse(match.Groups[1].Value);
            }
        }

        return new DronesGroup
        {
            UINode = groupNode,
            HeaderText = headerText,
            QuantityCurrent = current,
            QuantityMaximum = max,
            Drones = groupNode.QueryAll("@DroneEntry")
                .Concat(groupNode.QueryAll("@DroneSentry"))
                .Select(d => new DroneEntry
                {
                    UINode = d,
                    Name = d.GetAllContainedDisplayTexts().FirstOrDefault(),
                    HitpointsPercent = FindHitpoints(d),
                })
                .ToList(),
        };
    }

    private ProbeScannerWindow? FindProbeScannerWindow(UITreeNodeWithDisplayRegion root)
    {
        var node = root.FindFirst(n => IsType(n, "ProbeScannerWindow", "Scanner"));
        if (node == null) return null;
        return new ProbeScannerWindow { UINode = node };
    }

    private List<InventoryWindow> FindInventoryWindows(UITreeNodeWithDisplayRegion root)
    {
        // EVE's inventory windows: InventoryPrimary (main wnd), ActiveShipCargo, ShipCargo, etc.
        // Based on reference implementation, we look for these specific top-level types.
        var windows = root.QueryAll("@InventoryPrimary")
            .Concat(root.QueryAll("@ActiveShipCargo"))
            .Concat(root.QueryAll("@ShipCargo"))
            .Concat(root.QueryAll("@InventoryWindow"))
            .Distinct();

        // Fallback: any node with "Inventory" in its type and a capacity gauge
        if (!windows.Any())
        {
            windows = root.FindAll(n => 
                (n.Node.PythonObjectTypeName.Contains("Inventory", StringComparison.OrdinalIgnoreCase) ||
                 n.Node.PythonObjectTypeName.Contains("Cargo",     StringComparison.OrdinalIgnoreCase)) &&
                n.QueryFirst("@CapacityGauge") != null);
        }

        return windows.Select(n => {
            // Reference logic: look for specialized container nodes INSIDE the window
            var specializedContainer = n.FindFirst(c =>
                c.Node.PythonObjectTypeName.Contains("ShipGeneralMiningHold", StringComparison.OrdinalIgnoreCase) ||
                c.Node.PythonObjectTypeName.Contains("MiningHold",           StringComparison.OrdinalIgnoreCase) ||
                c.Node.PythonObjectTypeName.Contains("ShipDroneBay",          StringComparison.OrdinalIgnoreCase) ||
                c.Node.PythonObjectTypeName.Contains("ShipHangar",            StringComparison.OrdinalIgnoreCase) ||
                c.Node.PythonObjectTypeName.Contains("StationItems",          StringComparison.OrdinalIgnoreCase) ||
                c.Node.PythonObjectTypeName.Contains("ItemHangar",           StringComparison.OrdinalIgnoreCase) ||
                c.Node.PythonObjectTypeName.Contains("StructureItemHangar",   StringComparison.OrdinalIgnoreCase));
            
            var title = ExtractInventoryTitle(n) ?? (specializedContainer != null ? specializedContainer.Node.PythonObjectTypeName : null);
            var navEntries = ExtractNavEntries(n);
            var holdType = ClassifyHoldType(title);

            if (specializedContainer != null)
            {
                holdType = ClassifyHoldType(specializedContainer.Node.PythonObjectTypeName);
            }

            // Strategy 2: If still unknown, use the selected nav entry
            if (holdType == InventoryHoldType.Unknown)
            {
                var selected = navEntries.FirstOrDefault(e => e.IsSelected);
                if (selected != null) holdType = selected.HoldType;
            }

            return new InventoryWindow
            {
                UINode = n,
                SubCaptionLabelText = title,
                HoldType = holdType,
                NavEntries = navEntries,
                CapacityGauge = ExtractCapacityGauge(n),
                Items = n.QueryAll("@InvItem")
                    .Concat(n.QueryAll("@Item"))
                    .Where(i => i.Region.Width > 10 && i.Region.Height > 6)
                    .Select(i => new InventoryItem
                    {
                        UINode = i,
                        Name = i.GetAllContainedDisplayTexts().FirstOrDefault(t => t.Length >= 2 && t.Any(char.IsLetter)),
                        Quantity = ExtractItemQuantity(i),
                    })
                    .ToList(),
                ButtonToStackAll = n.QueryFirst("[_name=stackAllButton]") ?? 
                                   n.QueryFirst(":has-text('Stack All')"),
            };
        }).ToList();
    }

    /// <summary>
    /// Extracts the human-readable title of an inventory container
    /// (e.g. "Cargo Hold", "Ore Hold", "Item Hangar").
    /// EVE stores this in different places across versions:
    ///   • SubCaptionLabel node (_setText / _text)
    ///   • Caption label (_name == "captionLabel" or "subCaptionLabel")
    ///   • Any label whose text contains a known hold keyword
    /// </summary>
    private static string? ExtractInventoryTitle(UITreeNodeWithDisplayRegion invNode)
    {
        // Approach 1: explicit SubCaption type node
        var subCap = invNode.FindFirst(n =>
            n.Node.PythonObjectTypeName.Contains("SubCaption", StringComparison.OrdinalIgnoreCase));
        if (subCap != null)
        {
            var t = subCap.GetAllContainedDisplayTexts().FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(t)) return t;
        }

        // Approach 2: node named "captionLabel", "subCaptionLabel", "headerLabel" etc.
        var captionNode = invNode.FindFirst(n =>
        {
            var nm = n.Node.GetDictString("_name") ?? "";
            return nm.Contains("caption", StringComparison.OrdinalIgnoreCase)
                || nm.Contains("header",  StringComparison.OrdinalIgnoreCase)
                || nm.Contains("title",   StringComparison.OrdinalIgnoreCase);
        });
        if (captionNode != null)
        {
            var t = EveTextUtil.StripTags(captionNode.Node.GetDictString("_setText"))
                 ?? EveTextUtil.StripTags(captionNode.Node.GetDictString("_text"));
            if (!string.IsNullOrWhiteSpace(t)) return t;
        }

        // Approach 2.5: Any label in the right-side header area (if not in compact mode)
        var rightHeader = invNode.FindFirst(n =>
            n.Region.X > invNode.Region.X + invNode.Region.Width * 0.4 &&
            n.Region.Y < invNode.Region.Y + 60 &&
            (n.Node.PythonObjectTypeName.Contains("Label", StringComparison.OrdinalIgnoreCase) ||
             n.Node.PythonObjectTypeName.Contains("Text", StringComparison.OrdinalIgnoreCase)));
        if (rightHeader != null)
        {
             var t = EveTextUtil.StripTags(rightHeader.Node.GetDictString("_setText"))
                  ?? EveTextUtil.StripTags(rightHeader.Node.GetDictString("_text"));
             if (!string.IsNullOrWhiteSpace(t) && ClassifyHoldType(t) != InventoryHoldType.Unknown) return t;
        }

        // Approach 3: scan ONLY the top strip of the window for a hold keyword —
        // the left nav panel also contains hold names, so we must not scan the whole subtree.
        var keywords = new[]
        {
            "Cargo Hold", "Ore Hold", "Mining Hold", "Item Hangar",
            "Fleet Hangar", "Ship Hangar", "Fuel Bay", "Maintenance Bay",
            "Infrastructure Hold", "Ship Maintenance",
        };
        var titleStripMaxY = invNode.Region.Y + Math.Min(80, invNode.Region.Height / 5);
        var keyNode = invNode.FindFirst(n =>
        {
            if (n.Region.Y > titleStripMaxY) return false;
            var own = EveTextUtil.StripTags(n.Node.GetDictString("_setText"))
                   ?? EveTextUtil.StripTags(n.Node.GetDictString("_text"));
            return own != null && keywords.Any(k =>
                own.Contains(k, StringComparison.OrdinalIgnoreCase));
        });
        return keyNode != null
            ? (EveTextUtil.StripTags(keyNode.Node.GetDictString("_setText"))
               ?? EveTextUtil.StripTags(keyNode.Node.GetDictString("_text")))
            : null;
    }

    /// <summary>Classifies a hold title string into a typed enum.</summary>
    public static InventoryHoldType ClassifyHoldType(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return InventoryHoldType.Unknown;
        if (title.Contains("Cargo",          StringComparison.OrdinalIgnoreCase)) return InventoryHoldType.Cargo;
        if (title.Contains("Ore",            StringComparison.OrdinalIgnoreCase)) return InventoryHoldType.Mining;
        if (title.Contains("Mining",         StringComparison.OrdinalIgnoreCase)) return InventoryHoldType.Mining;
        if (title.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase)) return InventoryHoldType.Infrastructure;
        if (title.Contains("Maintenance",    StringComparison.OrdinalIgnoreCase)) return InventoryHoldType.ShipMaintenance;
        if (title.Contains("Ship Hangar",    StringComparison.OrdinalIgnoreCase)) return InventoryHoldType.ShipMaintenance;
        if (title.Contains("Fleet Hangar",   StringComparison.OrdinalIgnoreCase)) return InventoryHoldType.Fleet;
        if (title.Contains("Fuel",           StringComparison.OrdinalIgnoreCase)) return InventoryHoldType.Fuel;
        if (title.Contains("Item Hangar",    StringComparison.OrdinalIgnoreCase)) return InventoryHoldType.Item;
        return InventoryHoldType.Unknown;
    }

    /// <summary>
    /// Finds the clickable hold entries in the inventory window's left navigation tree.
    /// Each entry corresponds to a different hold (Cargo Hold, Ore Hold, etc.).
    /// </summary>
    private static List<InventoryNavEntry> ExtractNavEntries(UITreeNodeWithDisplayRegion invNode)
    {
        var holdKeywords = new (string Keyword, InventoryHoldType Type)[]
        {
            ("Cargo Hold",          InventoryHoldType.Cargo),
            ("Ore Hold",            InventoryHoldType.Mining),
            ("Mining Hold",         InventoryHoldType.Mining),
            ("Infrastructure Hold", InventoryHoldType.Infrastructure),
            ("Ship Maintenance",    InventoryHoldType.ShipMaintenance),
            ("Maintenance Bay",     InventoryHoldType.ShipMaintenance),
            ("Fleet Hangar",        InventoryHoldType.Fleet),
            ("Fuel Bay",            InventoryHoldType.Fuel),
            ("Item Hangar",         InventoryHoldType.Item),
        };

        // The nav panel occupies the left ~40% of the inventory window
        var navPanelRightEdge = invNode.Region.X + invNode.Region.Width * 0.42;

        var result = new List<InventoryNavEntry>();
        var seenTypes = new HashSet<InventoryHoldType>();

        // Find text-bearing nodes within the nav panel area
        foreach (var n in invNode.FindAll(n =>
            n.Region.Width is >= 50 and <= 400 &&
            n.Region.Height is >= 10 and <= 32 &&
            n.Region.X + n.Region.Width / 2 < navPanelRightEdge))
        {
            var own = EveTextUtil.StripTags(n.Node.GetDictString("_setText"))
                   ?? EveTextUtil.StripTags(n.Node.GetDictString("_text"));
            if (string.IsNullOrWhiteSpace(own)) continue;

            foreach (var (keyword, holdType) in holdKeywords)
            {
                if (own.Contains(keyword, StringComparison.OrdinalIgnoreCase) && seenTypes.Add(holdType))
                {
                    var isSelected = n.Node.GetDictBool("_selected") == true
                                  || n.Node.GetDictBool("selected")  == true
                                  || (n.Node.GetDictString("_state") ?? "")
                                         .Contains("selected", StringComparison.OrdinalIgnoreCase);

                    // Robust fallback for non-compact mode: check for SelectionIndicatorLine with high alpha
                    var sil = n.QueryFirst("@SelectionIndicatorLine");
                    if (sil != null)
                    {
                        var color = sil.Node.GetDictColor("_color");
                        if (color != null && color.APercent > 50) isSelected = true;
                    }

                    result.Add(new InventoryNavEntry
                    {
                        UINode     = n,
                        Label      = own.Trim(),
                        HoldType   = holdType,
                        IsSelected = isSelected,
                    });
                    break;
                }
            }
        }

        return result;
    }

    private static int? ExtractItemQuantity(UITreeNodeWithDisplayRegion item)
    {
        foreach (var text in item.GetAllContainedDisplayTexts())
        {
            var t = text.Replace(",", "").Replace(".", "").Trim();
            if (int.TryParse(t, out var q) && q > 0) return q;
        }
        return null;
    }

    private static InventoryCapacityGauge? ExtractCapacityGauge(UITreeNodeWithDisplayRegion invNode)
    {
        // Look for the capacity gauge node by type or by name
        var gaugeNode = invNode.FindFirst(n =>
            n.Node.PythonObjectTypeName.Contains("CapacityGauge", StringComparison.OrdinalIgnoreCase) ||
            n.Node.PythonObjectTypeName.Contains("Gauge",         StringComparison.OrdinalIgnoreCase) ||
            (n.Node.GetDictString("_name") ?? "")
                .Contains("capacity", StringComparison.OrdinalIgnoreCase));
        if (gaugeNode == null) return null;

        // Try all text nodes inside the gauge for the "used / max" pattern
        foreach (var text in gaugeNode.GetAllContainedDisplayTexts())
        {
            // Normalise: remove thousands-separator commas, strip m³ / m3 unit suffix
            var normalised = text
                .Replace("\u00A0", " ")   // non-breaking space
                .Replace("m\u00B3", "")   // m³
                .Replace("m3", "")
                .Trim();

            // Remove thousands commas only (keep decimal dot)
            // Strategy: replace commas that are followed by exactly 3 digits before / or end
            // Simple approach: just remove all commas
            normalised = normalised.Replace(",", "");

            var parts = normalised.Split('/');
            if (parts.Length < 2) continue;

            if (double.TryParse(parts[0].Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var used) &&
                double.TryParse(parts[1].Trim().Split(' ')[0],
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var max) &&
                max > 0)
            {
                return new InventoryCapacityGauge { Used = used, Maximum = max };
            }
        }
        return null;
    }

    private List<ChatWindowStack> FindChatWindowStacks(UITreeNodeWithDisplayRegion root)
    {
        return root.FindAll(n => IsType(n, "ChatWindowStack"))
            .Select(n => new ChatWindowStack
            {
                UINode = n,
                ChatWindows = n.FindAll(c => IsType(c, "Channel", "Chat"))
                    .Select(c => new ChatWindow
                    {
                        UINode = c,
                        Name = c.GetAllContainedDisplayTexts().FirstOrDefault(),
                    })
                    .ToList(),
            })
            .ToList();
    }

    private ModuleButtonTooltip? FindModuleButtonTooltip(UITreeNodeWithDisplayRegion root)
    {
        var node = root.FindFirst(n => IsType(n, "ModuleButtonTooltip", "TooltipPanel"));
        if (node == null) return null;

        var texts = node.GetAllContainedDisplayTexts().ToList();

        // "Range within 15 km" (Strip Miners) or "Optimal Range: 15 km" (regular lasers)
        var rangeText = texts.FirstOrDefault(t =>
            t.Contains("range", StringComparison.OrdinalIgnoreCase) &&
            EveConstants.DistanceRegex().IsMatch(t));

        // Separate label/value nodes: ["Optimal Range", "15 km"]
        if (rangeText == null)
        {
            var optIdx = texts.FindIndex(t => t.Contains("range", StringComparison.OrdinalIgnoreCase));
            if (optIdx >= 0 && optIdx + 1 < texts.Count && EveConstants.DistanceRegex().IsMatch(texts[optIdx + 1]))
                rangeText = texts[optIdx + 1];
        }

        return new ModuleButtonTooltip
        {
            UINode = node,
            ShortcutText = texts.FirstOrDefault(t => t.Contains("shortcut", StringComparison.OrdinalIgnoreCase)),
            OptimalRangeText = rangeText,
            OptimalRangeMeters = rangeText != null ? (int?)ParseDistanceText(rangeText) : null,
        };
    }

    private Neocom? FindNeocom(UITreeNodeWithDisplayRegion root)
    {
        var node = root.FindFirst(n => IsType(n, "Neocom"));
        if (node == null) return null;
        return new Neocom { UINode = node };
    }

    private List<MessageBox> FindMessageBoxes(UITreeNodeWithDisplayRegion root)
    {
        return root.FindAll(n => IsType(n, "MessageBox", "Modal"))
            .Select(n => new MessageBox
            {
                UINode = n,
                Buttons = n.FindAll(b => IsType(b, "Button")).ToList(),
                Texts = n.GetAllContainedDisplayTexts().ToList(),
            })
            .ToList();
    }

    private StationWindow? FindStationWindow(UITreeNodeWithDisplayRegion root)
    {
        var node = root.FindFirst(n => IsType(n, "LobbyWnd", "StationServices"));
        if (node == null) return null;

        return new StationWindow
        {
            UINode = node,
            UndockButton = FindUndockButton(node),
        };
    }

    private MiningScanResultsWindow? FindMiningScanResultsWindow(UITreeNodeWithDisplayRegion root)
    {
        var node = root.QueryFirst("@MiningScanResultsWindow") ?? root.QueryFirst("@SurveyScanView");
        if (node == null) return null;

        var entries = new List<MiningScanEntry>();
        
        // Find all nodes that look like they contain entry text (ListGroups or labels/entries)
        var allScanNodes = node.FindAll(n => 
            IsType(n, "ListGroup", "MiningScanEntry", "SurveyScanEntry") ||
            (n.Node.GetDictString("_name") ?? "").Contains("entry", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Region.Y)
            .ToList();

        double? currentGroupValue = null;

        for (int i = 0; i < allScanNodes.Count; i++)
        {
            var n = allScanNodes[i];
            var text = n.GetAllContainedDisplayTexts().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(text)) continue;

            bool isGroup = n.Node.PythonObjectTypeName.Contains("ListGroup", StringComparison.OrdinalIgnoreCase);
            
            if (isGroup)
            {
                var match = Regex.Match(text, @"^(.*?)\s+\[(\d+)\].*?([\d,.]+)\s*ISK", RegexOptions.IgnoreCase);
                
                // Robust expansion detection: if any subsequent nodes are NOT groups and have similar/larger Y, we are expanded.
                bool isExpanded = false;
                for (int j = i + 1; j < Math.Min(i + 10, allScanNodes.Count); j++)
                {
                    var next = allScanNodes[j];
                    if (!next.Node.PythonObjectTypeName.Contains("ListGroup", StringComparison.OrdinalIgnoreCase))
                    {
                        isExpanded = true;
                        break;
                    }
                    if (next.Region.Y > n.Region.Y + n.Region.Height + 50) break; // Too far down
                }

                currentGroupValue = null;
                if (match.Success)
                {
                    var valStr = match.Groups[3].Value.Replace(",", "");
                    if (double.TryParse(valStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val))
                        currentGroupValue = val;
                }

                entries.Add(new MiningScanEntry
                {
                    UINode = n,
                    OreName = match.Success ? match.Groups[1].Value.Trim() : text,
                    Quantity = match.Success ? int.Parse(match.Groups[2].Value) : null,
                    IsGroup = true,
                    IsExpanded = isExpanded,
                    IsLocked = n.FindFirst(c => (c.Node.GetDictString("_texturePath") ?? "").Contains("activeTarget.png")) != null,
                    ExpanderNode = n.QueryFirst("@GlowSprite") ?? n.QueryFirst("@Sprite"),
                    ValueText = text.Contains("ISK") ? text : null,
                    ValuePerM3 = currentGroupValue,
                });
            }
            else
            {
                // entryLabel example: "Scordite41,4696,220 m3675,000.00 ISK28 km"
                var match = Regex.Match(text, @"^([a-zA-Z\s\-]+?)([\d,.]+?)(?=[\d,.]+\s*m3)([\d,.]+\s*m3)([\d,.]+\s*ISK)([\d,.]+\s*(?:m|km|au))", RegexOptions.IgnoreCase);
                
                if (match.Success)
                {
                    // Parse volume (group 3: "6,220 m3") and ISK (group 4: "675,000.00 ISK")
                    // to compute actual ISK/m³ for this asteroid.
                    var volStr = Regex.Match(match.Groups[3].Value, @"[\d,.]+").Value.Replace(",", "");
                    var iskStr = Regex.Match(match.Groups[4].Value, @"[\d,.]+").Value.Replace(",", "");
                    double.TryParse(volStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var vol);
                    double.TryParse(iskStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var isk);
                    double? iskPerM3 = (vol > 0 && isk > 0) ? isk / vol : currentGroupValue;

                    entries.Add(new MiningScanEntry
                    {
                        UINode = n,
                        OreName = match.Groups[1].Value.Trim(),
                        Quantity = int.TryParse(match.Groups[2].Value.Replace(",", ""), out var q) ? q : null,
                        Volume = vol > 0 ? vol : null,
                        IsGroup = false,
                        IsLocked = n.FindFirst(c => (c.Node.GetDictString("_texturePath") ?? "").Contains("activeTarget.png")) != null,
                        DistanceInMeters = ParseDistanceText(match.Groups[5].Value),
                        ValueText = match.Groups[4].Value.Trim(),
                        ValuePerM3 = iskPerM3,
                    });
                }
                else if (text.Length > 5 && !text.Contains("ISK / m"))
                {
                    // Fallback for simple names
                    entries.Add(new MiningScanEntry
                    {
                        UINode = n,
                        OreName = text.Length > 20 ? text[..15].Trim() : text,
                        IsGroup = false,
                        IsLocked = n.FindFirst(c => (c.Node.GetDictString("_texturePath") ?? "").Contains("activeTarget.png")) != null,
                        ValuePerM3 = currentGroupValue,
                    });
                }
            }
        }

        return new MiningScanResultsWindow
        {
            UINode = node,
            ScanButton = node.QueryFirst("@Button:has-text('Scan')") ?? 
                         node.QueryFirst("[_name=buttonScan]") ??
                         node.QueryFirst("@Button"),
            Entries = entries,
        };
    }

    /// <summary>
    /// Finds the undock button in the station lobby. Prefers a Button-type container
    /// over a text-label child so that clicks land on the full clickable region.
    /// </summary>
    private static UITreeNodeWithDisplayRegion? FindUndockButton(UITreeNodeWithDisplayRegion lobby)
    {
        // 1. Prefer nodes whose type or _name explicitly says "undock" AND have non-zero size.
        var byName = lobby.FindFirst(n =>
            (n.Node.PythonObjectTypeName.Contains("Undock", StringComparison.OrdinalIgnoreCase)
             || (n.Node.GetDictString("_name") ?? "").Contains("undock", StringComparison.OrdinalIgnoreCase))
            && n.Region.Width > 0 && n.Region.Height > 0);
        if (byName != null) return byName;

        // 2. Find a Button-type parent that contains "undock" text.
        var byButton = lobby.FindFirst(n =>
            n.Node.PythonObjectTypeName.Contains("Button", StringComparison.OrdinalIgnoreCase)
            && n.Region.Width > 10 && n.Region.Height > 8
            && n.GetAllContainedDisplayTexts().Any(t =>
                t.Contains("undock", StringComparison.OrdinalIgnoreCase)));
        if (byButton != null) return byButton;

        // 3. Last resort: any node with "undock" text that has a usable size.
        return lobby.FindFirst(n =>
            n.Region.Width > 10 && n.Region.Height > 8
            && n.GetAllContainedDisplayTexts().Any(t =>
                t.Contains("undock", StringComparison.OrdinalIgnoreCase)));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static bool IsType(UITreeNodeWithDisplayRegion node, params string[] typeKeywords)
    {
        var typeName = node.Node.PythonObjectTypeName;
        var name = node.Node.GetDictString("_name") ?? "";

        return typeKeywords.Any(k =>
            typeName.Contains(k, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static double? ParseDistanceText(string text) => EveConstants.ParseDistanceMeters(text);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}
