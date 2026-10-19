using System.Windows.Automation;
using Martlet.Core.Text;

namespace Martlet.Mcp;

/// <summary>ui_long_texts: the texts on the attached Martlet's open windows that have more than one sentence. The 1.0 rule for
/// UI text (issue #992) is one short sentence on the page, with the rest behind a "?" (HelpTip) and its wiki page.</summary>
internal sealed partial class DesktopAutomation
{
    internal const int MaximumLongTexts = 200;

    /// <summary>Each text (a text block, a check box's or a radio button's label) that shows now and has two or more sentences:
    /// its window, its automation ID (or, without one, the nearest container's as near), how many sentences it has and the text.
    /// Left out: lists, tables, trees and text boxes (where your own content shows), help cards (they may have two short
    /// sentences), tooltips, and controls whose automation ID ui_snapshot does not read.</summary>
    internal object LongTexts()
    {
        var windows = ConnectedWindows();
        var request = new CacheRequest { TreeScope = TreeScope.Subtree, TreeFilter = Automation.ControlViewCondition };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        var found = new List<Dictionary<string, object?>>();
        var total = 0;
        foreach (var window in windows)
        {
            AutomationElement root;
            using (request.Activate()) root = window.GetUpdatedCache(request);
            var title = window.Current.Name;
            Walk(root, null);

            void Walk(AutomationElement element, string? near)
            {
                var type = element.Cached.ControlType;
                var id = element.Cached.AutomationId;
                if (Skipped.Contains(type) || id.StartsWith("Help-", StringComparison.Ordinal) && id.EndsWith("-Card", StringComparison.Ordinal))
                    return;
                if (id.Length > 0 && (type == ControlType.Text || type == ControlType.CheckBox || type == ControlType.RadioButton) &&
                    !AutomationPolicy.IsSafeValue(id))
                    return;
                if ((type == ControlType.Text || type == ControlType.CheckBox || type == ControlType.RadioButton) &&
                    element.Cached.Name is { Length: > 0 } text && Sentences.Count(text) is var count && count > 1)
                {
                    total++;
                    if (found.Count < MaximumLongTexts)
                        found.Add(new Dictionary<string, object?>
                        {
                            ["window"] = title,
                            ["id"] = id.Length > 0 ? id : null,
                            ["near"] = id.Length > 0 ? null : near,
                            ["kind"] = type.ProgrammaticName,
                            ["sentences"] = count,
                            ["text"] = AutomationPolicy.Redact(text)
                        });
                }
                var inside = id.Length > 0 ? id : near;
                foreach (AutomationElement child in element.CachedChildren) Walk(child, inside);
            }
        }
        return new { processId, count = total, texts = found.ToArray() };
    }

    /// <summary>Where your own content shows (messages, memories, logs, typed text) or what only shows on pointing.</summary>
    private static readonly HashSet<ControlType> Skipped =
    [
        ControlType.List, ControlType.ListItem, ControlType.DataGrid, ControlType.DataItem, ControlType.Tree, ControlType.TreeItem,
        ControlType.Table, ControlType.Edit, ControlType.Document, ControlType.ToolTip
    ];
}
