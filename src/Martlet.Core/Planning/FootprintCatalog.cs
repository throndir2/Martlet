namespace Martlet.Core.Planning;

/// <summary>Every way Martlet knows to do each component, with its footprint. Pure data; <see cref="Default"/> is the seed in
/// FootprintCatalog.Seed.cs. Tests and callers can build their own catalog from any options.</summary>
public sealed partial class FootprintCatalog
{
    private readonly ComponentOption[] options;

    public FootprintCatalog(IEnumerable<ComponentOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.ToArray();
        var duplicate = this.options.GroupBy(o => o.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Option id '{duplicate.Key}' appears more than once.", nameof(options));
    }

    public static FootprintCatalog Default { get; } = new(SeedOptions());

    /// <summary>The option for the way to speak that isn't a voice engine: OpenAI's voice (it needs a saved key).</summary>
    public const string OpenAiVoiceId = "hosted:openai-tts";

    /// <summary>The voice engine Martlet falls back to when no computer has room for the owner's engine: Chatterbox Nano, on a
    /// graphics card with 4 GB or more, else on the processor (about 8 threads). It is a host role, so it needs the Martlet
    /// host service in Docker.</summary>
    public const string FallbackVoiceKind = "chatterbox-nano";

    public IReadOnlyList<ComponentOption> Options => options;

    public IReadOnlyList<ComponentOption> For(PlanComponent component) => options.Where(o => o.Component == component).ToArray();

    public ComponentOption? Find(string id) => options.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal));

    /// <summary>The option for <paramref name="component"/> that runs <paramref name="modelId"/> (a model or engine id), or null.</summary>
    public ComponentOption? FindModel(PlanComponent component, string modelId) =>
        options.FirstOrDefault(o => o.Component == component && string.Equals(o.ModelId, modelId, StringComparison.OrdinalIgnoreCase)) ??
        options.FirstOrDefault(o => o.Component == component && string.Equals(o.Id, modelId, StringComparison.OrdinalIgnoreCase));
}
