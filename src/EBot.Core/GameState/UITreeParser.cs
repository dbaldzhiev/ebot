using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace EBot.Core.GameState;

/// <summary>
/// Parses the raw Sanderling JSON memory reading into typed <see cref="ParsedUI"/> models.
/// </summary>
public sealed partial class UITreeParser
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
        };
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
        // Entries are exactly "MenuEntryView" at any descendant depth.
        return root.FindAll(n => n.Node.PythonObjectTypeName == "ContextMenu")
            .Select(n => new ContextMenu
            {
                UINode = n,
                Entries = n.FindAll(c => c.Node.PythonObjectTypeName == "MenuEntryView")
                    .Select(c => new ContextMenuEntry
                    {
                        UINode = c,
                        Text = c.GetAllContainedDisplayTexts().FirstOrDefault(),
                        IsHighlighted = c.Node.GetDictBool("_hilite") ?? false,
                    })
                    .ToList(),
            })
            .ToList();
    }

    private ShipUI? FindShipUI(UITreeNodeWithDisplayRegion root)
    {
        var node = root.FindFirst(n => IsType(n, "ShipUI"));
        if (node == null) return null;

        // Each ShipSlot contains a ModuleButton child — pair them for sprite-based state detection
        var slotNodes = node.FindAll(n =>
            n.Node.PythonObjectTypeName.Contains("ShipSlot", StringComparison.OrdinalIgnoreCase));
        var moduleButtons = slotNodes
            .Select(slot =>
            {
                var btn = slot.FindFirst(n =>
                    n.Node.PythonObjectTypeName.Contains("ModuleButton", StringComparison.OrdinalIgnoreCase)
                    && !ReferenceEquals(n, slot));
                return BuildModuleButton(btn ?? slot, slot);
            })
            .ToList();

        // Speed text: currentVelocityLabel or SpeedGauge label
        var speedText =
            node.FindFirst(n => string.Equals(n.Node.GetDictString("_name"),
                "currentVelocityLabel", StringComparison.OrdinalIgnoreCase))
                ?.GetAllContainedDisplayTexts().FirstOrDefault()
            ?? node.FindFirst(n =>
                n.Node.PythonObjectTypeName.Contains("SpeedGauge", StringComparison.OrdinalIgnoreCase))
                ?.GetAllContainedDisplayTexts().FirstOrDefault();

        return new ShipUI
        {
            UINode = node,
            Capacitor = FindCapacitor(node),
            HitpointsPercent = FindHitpoints(node),
            Indication = FindIndication(node),
            ModuleButtons = moduleButtons,
            ModuleButtonsRows = ClassifyModuleRows(moduleButtons, node),
            StopButton = node.FindFirst(n => IsType(n, "StopButton")),
            MaxSpeedButton = node.FindFirst(n =>
                n.Node.PythonObjectTypeName.Contains("MaxSpeedButton", StringComparison.OrdinalIgnoreCase)),
            SpeedText = speedText,
        };
    }

    private ShipUICapacitor? FindCapacitor(UITreeNodeWithDisplayRegion shipUI)
    {
        var capNode = shipUI.FindFirst(n => IsType(n, "CapacitorContainer", "Capacitor"));
        if (capNode == null) return null;

        var pmarks = capNode.FindAll(n => IsType(n, "Pmark")).ToList();
        int? levelPercent = null;

        if (pmarks.Count > 0)
        {
            // In EVE's capacitor ring the glowing (filled) cells render with low alpha;
            // dark (empty) cells have high alpha. Count cells with APercent > 25 as unlit.
            var unlit = pmarks.Count(p =>
            {
                var color = p.Node.GetDictColor("_color");
                return color != null && color.APercent > 25;
            });
            levelPercent = (int)((double)(pmarks.Count - unlit) / pmarks.Count * 100);
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
        var gauge = container.FindFirst(n =>
            string.Equals(n.Node.GetDictString("_name"), gaugeName, StringComparison.OrdinalIgnoreCase));
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
        var node = shipUI.FindFirst(n => IsType(n, "IndicationContainer", "Indication"));
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

        return new ShipUIModuleButton
        {
            UINode = slotNode,
            SlotNode = slotNode,
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

        var centerY = shipUI.Center.Y;
        var sorted = buttons.OrderBy(b => b.UINode.Region.Y).ToList();

        // Simple heuristic: split into thirds by Y position
        var top = sorted.Where(b => b.UINode.Center.Y < centerY - 30).ToList();
        var bottom = sorted.Where(b => b.UINode.Center.Y > centerY + 30).ToList();
        var middle = sorted.Except(top).Except(bottom).ToList();

        return new ShipUIModuleButtonRows { Top = top, Middle = middle, Bottom = bottom };
    }

    private List<Target> FindTargets(UITreeNodeWithDisplayRegion root)
    {
        return root.FindAll(n => IsType(n, "TargetInBar", "Target"))
            .Select(n => new Target
            {
                UINode = n,
                TextLabel = n.GetAllContainedDisplayTexts().FirstOrDefault(),
                HitpointsPercent = FindHitpoints(n),
                IsActiveTarget = n.Node.GetDictString("isActiveTarget") is "True" or "1",
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

        // System name: node is named "headerLabelSystemName" (contains "SystemName" or "labelsystemname")
        var sysNode = node.FindFirst(n =>
            (n.Node.GetDictString("_name") ?? "").Contains("SystemName", StringComparison.OrdinalIgnoreCase)
            || (n.Node.GetDictString("_name") ?? "").Contains("labelsystemname", StringComparison.OrdinalIgnoreCase));
        var systemName = sysNode?.GetAllContainedDisplayTexts().FirstOrDefault()
            ?? node.GetAllContainedDisplayTexts().FirstOrDefault();

        // Security status: node is named "headerLabelSecStatus" (contains "SecStatus")
        var secNode = node.FindFirst(n =>
            (n.Node.GetDictString("_name") ?? "").Contains("SecStatus", StringComparison.OrdinalIgnoreCase)
            || (n.Node.GetDictString("_name") ?? "").Contains("security", StringComparison.OrdinalIgnoreCase));
        var secText = secNode != null
            ? (EveTextUtil.StripTags(secNode.Node.GetDictString("_setText"))
               ?? EveTextUtil.StripTags(secNode.Node.GetDictString("_text")))
            : null;

        return new InfoPanelLocationInfo
        {
            UINode = node,
            SystemName = systemName,
            SecurityStatusText = secText,
        };
    }

    private List<OverviewWindow> FindOverviewWindows(UITreeNodeWithDisplayRegion root)
    {
        return root.FindAll(n =>
                n.Node.PythonObjectTypeName.Contains("OverView", StringComparison.OrdinalIgnoreCase))
            .Select(BuildOverviewWindow)
            .ToList();
    }

    private OverviewWindow BuildOverviewWindow(UITreeNodeWithDisplayRegion overviewNode)
    {
        // Extract column headers: find the dedicated header row container, collect (name, leftX) pairs
        var headers = new List<(string Name, int X)>();

        var headerRow = overviewNode.FindFirst(n =>
            n.Node.PythonObjectTypeName.Contains("Header", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n.Node.GetDictString("_name"), "headers", StringComparison.OrdinalIgnoreCase));

        if (headerRow != null)
        {
            foreach (var child in headerRow.Children)
            {
                var text = child.GetAllContainedDisplayTexts().FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(text))
                    headers.Add((text, child.Region.X));
            }
        }

        var entries = overviewNode
            .FindAll(n => n.Node.PythonObjectTypeName.Contains("OverviewScrollEntry",
                StringComparison.OrdinalIgnoreCase))
            .Select(e => BuildOverviewEntry(e, headers))
            .ToList();

        // Extract overview filter tabs (TabGroup → individual Tab/Button children)
        var tabs = new List<OverviewTab>();
        var tabGroup = overviewNode.FindFirst(n =>
            n.Node.PythonObjectTypeName.Contains("TabGroup", StringComparison.OrdinalIgnoreCase) ||
            n.Node.PythonObjectTypeName.Contains("OverviewTab", StringComparison.OrdinalIgnoreCase));
        if (tabGroup != null)
        {
            foreach (var tab in tabGroup.Children)
            {
                var name = tab.GetAllContainedDisplayTexts().FirstOrDefault();
                if (string.IsNullOrWhiteSpace(name)) continue;
                // "selected" or highlighted tab is the active one
                bool isActive = tab.Node.GetDictBool("_selected") == true
                    || tab.Node.GetDictBool("selected") == true
                    || (tab.Node.GetDictString("_state") ?? "").Contains("selected", StringComparison.OrdinalIgnoreCase);
                tabs.Add(new OverviewTab { UINode = tab, Name = name, IsActive = isActive });
            }
        }

        return new OverviewWindow
        {
            UINode = overviewNode,
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
        var distText = texts.FirstOrDefault(t => DistanceRegex().IsMatch(t));

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
                // Find nearest column header within ±40px
                var best = columnHeaders.MinBy(h => Math.Abs(h.X - cellX));
                if (best != default && Math.Abs(best.X - cellX) <= 40)
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
                .Where(p => !DistanceRegex().IsMatch(p.Text) && !string.IsNullOrWhiteSpace(p.Text))
                .OrderBy(p => p.X)
                .FirstOrDefault();
            name = leftmost.Text?.Trim();
        }

        cellsTexts.TryGetValue("Type", out var objectType);
        if (string.IsNullOrEmpty(objectType)) cellsTexts.TryGetValue("Ship Type", out objectType);
        if (string.IsNullOrEmpty(objectType)) cellsTexts.TryGetValue("Category", out objectType);

        cellsTexts.TryGetValue("Distance", out var cellDistText);
        var resolvedDistText = !string.IsNullOrEmpty(cellDistText) ? cellDistText : distText;

        return new OverviewEntry
        {
            UINode = n,
            CellsTexts = cellsTexts,
            Name = name,
            ObjectType = objectType,
            DistanceText = resolvedDistText,
            DistanceInMeters = resolvedDistText != null ? ParseDistanceText(resolvedDistText) : null,
            IsAttackingMe = n.FindFirst(e =>
                (e.Node.GetDictString("_hint") ?? "").Contains("attacking", StringComparison.OrdinalIgnoreCase)) != null,
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
        var node = root.FindFirst(n => IsType(n, "SelectedItemWnd", "ActiveItem"));
        if (node == null) return null;

        return new SelectedItemWindow
        {
            UINode = node,
            ActionButtons = node.FindAll(n => IsType(n, "ButtonIcon", "ActionButton")).ToList(),
        };
    }

    private DronesWindow? FindDronesWindow(UITreeNodeWithDisplayRegion root)
    {
        var node = root.FindFirst(n => IsType(n, "DroneView", "DronesWindow"));
        if (node == null) return null;

        return new DronesWindow
        {
            UINode = node,
            DronesInBay = FindDronesGroup(node, "bay", "droneBay"),
            DronesInSpace = FindDronesGroup(node, "space", "droneSpace", "local"),
        };
    }

    private DronesGroup? FindDronesGroup(UITreeNodeWithDisplayRegion parent, params string[] keywords)
    {
        var groupNode = parent.FindFirst(n =>
            keywords.Any(k => n.GetAllContainedDisplayTexts()
                .Any(t => t.Contains(k, StringComparison.OrdinalIgnoreCase))));
        if (groupNode == null) return null;

        return new DronesGroup
        {
            UINode = groupNode,
            HeaderText = groupNode.GetAllContainedDisplayTexts().FirstOrDefault(),
            Drones = groupNode.FindAll(n => IsType(n, "DroneEntry", "DroneSentry"))
                .Select(d => new DroneEntry
                {
                    UINode = d,
                    Name = d.GetAllContainedDisplayTexts().FirstOrDefault(),
                })
                .ToList(),
        };
    }

    private List<InventoryWindow> FindInventoryWindows(UITreeNodeWithDisplayRegion root)
    {
        return root.FindAll(n => IsType(n, "InventoryPrimary", "Inventory"))
            .Where(n => !IsType(n, "Item")) // filter out inventory items
            .Select(n => new InventoryWindow
            {
                UINode = n,
                SubCaptionLabelText = n.FindFirst(c => IsType(c, "SubCaption"))
                    ?.GetAllContainedDisplayTexts().FirstOrDefault(),
                CapacityGauge = ExtractCapacityGauge(n),
                Items = n.FindAll(i => IsType(i, "Item", "InvItem"))
                    .Select(i => new InventoryItem
                    {
                        UINode = i,
                        Name = i.GetAllContainedDisplayTexts().FirstOrDefault(),
                    })
                    .ToList(),
                ButtonToStackAll = n.FindFirst(b =>
                    b.GetAllContainedDisplayTexts().Any(t =>
                        t.Contains("stack", StringComparison.OrdinalIgnoreCase))),
            })
            .ToList();
    }

    private static InventoryCapacityGauge? ExtractCapacityGauge(UITreeNodeWithDisplayRegion invNode)
    {
        var gaugeNode = invNode.FindFirst(n => IsType(n, "CapacityGauge", "Gauge"));
        if (gaugeNode == null) return null;

        var text = gaugeNode.GetAllContainedDisplayTexts().FirstOrDefault();
        if (text == null) return null;

        // Format: "123.45 / 500.00 m³" or "123.45/500.00"
        var parts = text.Replace(",", "").Split('/');
        if (parts.Length < 2) return null;

        if (double.TryParse(parts[0].Trim(), out var used) &&
            double.TryParse(parts[1].Trim().Split(' ')[0], out var max))
        {
            return new InventoryCapacityGauge { Used = used, Maximum = max };
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
        var rangeText = texts.FirstOrDefault(t => t.Contains("m", StringComparison.OrdinalIgnoreCase)
            && DistanceRegex().IsMatch(t));

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

    private static double? ParseDistanceText(string text)
    {
        var match = DistanceRegex().Match(text);
        if (!match.Success || !double.TryParse(match.Groups[1].Value.Replace(",", ""), out var value))
            return null;

        var unit = match.Groups[2].Value.Trim().ToLowerInvariant();
        return unit switch
        {
            "m" => value,
            "km" => value * 1000,
            "au" => value * 149_597_870_700,
            _ => value,
        };
    }

    [GeneratedRegex(@"([\d,.]+)\s*(m|km|au)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DistanceRegex();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}
