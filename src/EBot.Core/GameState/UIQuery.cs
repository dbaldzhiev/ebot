using System.Text.RegularExpressions;

namespace EBot.Core.GameState;

/// <summary>
/// A CSS-like selector for the EVE UI tree.
/// Syntax:
///   @TypeName          - Match pythonObjectTypeName
///   [Prop=Value]       - Match a property in DictEntriesOfInterest
///   :has-text('...')   - Match any descendant with specific text
///   >                  - Direct child separator
///   (space)            - Descendant separator
/// </summary>
public sealed class UIQuery
{
    private readonly List<ISelectorStep> _steps;

    private UIQuery(List<ISelectorStep> steps)
    {
        _steps = steps;
    }

    public static UIQuery Parse(string selector)
    {
        var steps = new List<ISelectorStep>();
        var tokens = Regex.Split(selector, @"(\s+|>)").Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        bool directChild = false;
        foreach (var token in tokens)
        {
            if (token == ">")
            {
                directChild = true;
                continue;
            }

            var parts = ParseToken(token);
            parts.IsDirectChild = directChild;
            steps.Add(parts);
            directChild = false;
        }

        return new UIQuery(steps);
    }

    private static SelectorStep ParseToken(string token)
    {
        var step = new SelectorStep();
        
        // Type match: @TypeName (exact) or @*TypeName* (contains)
        var typeMatch = Regex.Match(token, @"@(\*?)([\w\d]+)(\*?)");
        if (typeMatch.Success)
        {
            step.TypeName = typeMatch.Groups[2].Value;
            step.TypeNameContains = typeMatch.Groups[1].Value == "*" || typeMatch.Groups[3].Value == "*";
        }

        // Prop match: [Key=Value] (exact) or [Key*=Value] (contains)
        var propMatches = Regex.Matches(token, @"\[([\w\d_]+)(\*?=)([^\]]+)\]");
        foreach (Match m in propMatches)
        {
            var key = m.Groups[1].Value;
            var op = m.Groups[2].Value;
            var val = m.Groups[3].Value;
            step.Properties.Add(key, (val, op == "*="));
        }

        // Text match: :has-text('...')
        var textMatch = Regex.Match(token, @":has-text\('([^']+)'\)");
        if (textMatch.Success) step.HasText = textMatch.Groups[1].Value;

        return step;
    }

    public UITreeNodeWithDisplayRegion? MatchFirst(UITreeNodeWithDisplayRegion root)
    {
        return MatchAll(root).FirstOrDefault();
    }

    public IEnumerable<UITreeNodeWithDisplayRegion> MatchAll(UITreeNodeWithDisplayRegion root)
    {
        IEnumerable<UITreeNodeWithDisplayRegion> currentNodes = [root];

        foreach (var step in _steps)
        {
            var nextNodes = new List<UITreeNodeWithDisplayRegion>();
            foreach (var node in currentNodes)
            {
                if (step.IsDirectChild)
                {
                    nextNodes.AddRange(node.Children.Where(step.Matches));
                }
                else
                {
                    nextNodes.AddRange(node.FindAll(step.Matches));
                }
            }
            currentNodes = nextNodes.Distinct();
            if (!currentNodes.Any()) break;
        }

        return currentNodes;
    }

    private interface ISelectorStep
    {
        bool IsDirectChild { get; set; }
        bool Matches(UITreeNodeWithDisplayRegion node);
    }

    private class SelectorStep : ISelectorStep
    {
        public string? TypeName { get; set; }
        public bool TypeNameContains { get; set; }
        public Dictionary<string, (string Value, bool Contains)> Properties { get; } = new();
        public string? HasText { get; set; }
        public bool IsDirectChild { get; set; }

        public bool Matches(UITreeNodeWithDisplayRegion node)
        {
            if (TypeName != null)
            {
                var actual = node.Node.PythonObjectTypeName;
                bool match = TypeNameContains 
                    ? actual.Contains(TypeName, StringComparison.OrdinalIgnoreCase)
                    : actual.Equals(TypeName, StringComparison.OrdinalIgnoreCase);
                if (!match) return false;
            }

            foreach (var prop in Properties)
            {
                var actual = node.Node.GetDictString(prop.Key);
                if (actual == null) return false;
                
                bool match = prop.Value.Contains
                    ? actual.Contains(prop.Value.Value, StringComparison.OrdinalIgnoreCase)
                    : actual.Equals(prop.Value.Value, StringComparison.OrdinalIgnoreCase);
                
                if (!match) return false;
            }

            if (HasText != null && !node.GetAllContainedDisplayTexts().Any(t => t.Contains(HasText, StringComparison.OrdinalIgnoreCase)))
                return false;

            return true;
        }
    }
}
