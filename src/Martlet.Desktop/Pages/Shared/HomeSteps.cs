namespace Martlet.Desktop;

/// <summary>One step of a numbered step list on Home or the host dashboard: its title, what it says, whether it is done or
/// optional, its buttons and its "?" help.</summary>
internal sealed record HomeStep(string Id, string Title, string Detail, bool Done, bool Optional, IReadOnlyList<StepCommand> Commands,
    StepHelp? Help = null);

/// <summary>A step's "?" after its title (<see cref="HelpTip.For"/>): what the card says and the wiki section it links to.</summary>
internal sealed record StepHelp(string Topic, string Text, string Id, string? Wiki);

/// <summary>A step's button. <paramref name="Enabled"/> false greys it out while a run already does what it would do (its
/// label then says so, such as "Starting Docker Desktop..."). <paramref name="Id"/> names the button in its automation ID,
/// Step-&lt;step&gt;-&lt;Id&gt;; without it, the button's place in the step names it (Step-&lt;step&gt;-0).</summary>
internal sealed record StepCommand(string Label, Action Run, bool Primary = false, bool Enabled = true, string? Id = null);
