using System.Text.Json;
using EBot.Core.GameState;
using Xunit;

namespace EBot.Tests;

public class UIQueryTests
{
    private readonly UITreeNodeWithDisplayRegion _root;

    public UIQueryTests()
    {
        var path = FindFirstFrameJson();
        var json = File.ReadAllText(path);
        var raw = JsonSerializer.Deserialize<UITreeNode>(json)!;
        _root = BuildAnnotatedTree(raw, null);
    }

    private static string FindFirstFrameJson()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var framesDir = Path.Combine(dir, "logs", "frames");
            if (Directory.Exists(framesDir))
            {
                var file = Directory.GetFiles(framesDir, "*.json").FirstOrDefault();
                if (file != null) return file;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException("Could not find any frame JSON in logs/frames");
    }

    private UITreeNodeWithDisplayRegion BuildAnnotatedTree(UITreeNode raw, DisplayRegion? parent)
    {
        var region = new DisplayRegion();
        var annotated = new UITreeNodeWithDisplayRegion { Node = raw, Region = region };
        if (raw.Children != null)
        {
            foreach (var child in raw.Children)
                annotated.Children.Add(BuildAnnotatedTree(child, region));
        }
        return annotated;
    }

    [Fact]
    public void Can_Match_ByType()
    {
        var match = _root.QueryFirst("@ShipUI");
        Assert.NotNull(match);
        Assert.Equal("ShipUI", match.Node.PythonObjectTypeName);
    }

    [Fact]
    public void Can_Match_ByProperty()
    {
        // Many nodes have _display=true
        var match = _root.QueryFirst("[_display=true]");
        Assert.NotNull(match);
    }

    [Fact]
    public void Can_Match_Hierarchy()
    {
        // SpeedGauge is inside ShipUI
        var match = _root.QueryFirst("@ShipUI @SpeedGauge");
        Assert.NotNull(match);
    }
}
