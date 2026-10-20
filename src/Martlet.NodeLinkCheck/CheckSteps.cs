namespace Martlet.NodeLinkCheck;

/// <summary>The steps of the node link check (default and live modes) and whether every one passed. Program prints
/// <see cref="Report"/> as one JSON object {passed, steps}.</summary>
internal sealed class CheckSteps
{
    private readonly List<object> steps = [];

    internal bool Passed { get; private set; } = true;

    internal object Report => new { passed = Passed, steps };

    internal void Step(string name, bool ok, string detail)
    {
        steps.Add(new { name, ok, detail });
        Passed &= ok;
    }
}
