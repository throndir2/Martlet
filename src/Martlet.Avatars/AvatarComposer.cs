using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Martlet.Core.Contracts;

namespace Martlet.Avatars;

public sealed record AspectCompatibility(AvatarAspect Aspect, string SourceId, CompatibilityAssessment Assessment);

public sealed record CompositionResult(
    bool Enabled, AvatarComposition? Composition, IReadOnlyList<CompatibilityIssue> Issues,
    IReadOnlyList<AspectCompatibility> Assessments, string? RecommendedSourceId)
{
    public bool IsValid => Composition is not null;
}

public sealed record ComposedFrame(FrameDisposition Disposition, IReadOnlyDictionary<string, double> Parameters);

public static class AvatarComposer
{
    public static CompositionResult Create(AvatarConfiguration configuration, ModelCapabilities? model,
        IReadOnlyList<SourceCapabilities>? sources)
    {
        configuration.Validate();
        if (!configuration.Enabled)
            return new(false, new AvatarComposition([]), [], [], null);

        ContractRules.Require(model is not null && sources is not null, "Enabled avatars require model and source capability metadata.");
        model!.Validate();
        AvatarValidation.Items(sources, 16);
        var sourceMap = new Dictionary<string, SourceCapabilities>(StringComparer.Ordinal);
        foreach (var source in sources!)
        {
            source.Validate();
            ContractRules.Require(sourceMap.TryAdd(source.SourceId, source), "Source identifiers cannot repeat.");
        }
        var issues = new List<CompatibilityIssue>();
        var assessments = new List<AspectCompatibility>();
        var routes = new List<AvatarRoute>();
        var failed = false;
        var requested = configuration.RequestedAspects.ToHashSet();
        var omitted = configuration.OmittedAspects.ToHashSet();
        var occupiedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extra in omitted.Concat(configuration.Assignments.Select(a => a.Aspect)).Distinct().Except(requested))
            Fail(CompatibilityIssueCode.UnrequestedAspect, extra);
        foreach (var aspect in requested)
        {
            var owners = configuration.Assignments.Where(a => a.Aspect == aspect).ToArray();
            if (omitted.Contains(aspect))
            {
                if (owners.Length != 0) Fail(CompatibilityIssueCode.OmittedAndAssigned, aspect);
                continue;
            }
            if (owners.Length != 1)
            {
                Fail(owners.Length == 0 ? CompatibilityIssueCode.MissingAssignment :
                    CompatibilityIssueCode.ConflictingSources, aspect);
                continue;
            }
            var owner = owners[0];
            if (!sourceMap.TryGetValue(owner.SourceId, out var source))
            {
                Fail(CompatibilityIssueCode.UnknownCapabilities, aspect);
                continue;
            }
            var profile = configuration.MappingProfiles.SingleOrDefault(p => p.Id == owner.MappingId);
            var assessment = CompatibilityEngine.Assess(source, model, aspect, profile);
            assessments.Add(new(aspect, source.SourceId, assessment));
            issues.AddRange(assessment.Issues);
            if (!assessment.Ready || assessment.Compatibility != Compatibility.Supported &&
                !(assessment.Compatibility == Compatibility.Reduced && owner.AcceptReduced))
            {
                failed = true;
                continue;
            }
            var mappings = profile!.Mappings.Where(m => AvatarChannels.Aspect(m.Source) == aspect).ToArray();
            foreach (var mapping in mappings)
                if (!occupiedTargets.Add(mapping.TargetParameterId)) Fail(CompatibilityIssueCode.OverlappingTarget, aspect);
            routes.Add(new(source.SourceId, source.Channels.ToFrozenSet(), Array.AsReadOnly(mappings)));
        }
        var recommended = !failed ? configuration.Assignments.Select(a => sourceMap.GetValueOrDefault(a.SourceId))
            .FirstOrDefault(s => s?.Backend == configuration.PreferredBackend)?.SourceId : null;
        return new(true, failed ? null : new AvatarComposition(routes), issues.AsReadOnly(),
            assessments.AsReadOnly(), recommended);

        void Fail(CompatibilityIssueCode code, AvatarAspect aspect)
        {
            failed = true;
            issues.Add(CompatibilityEngine.Issue(code, aspect));
        }
    }
}

internal sealed record AvatarRoute(
    string SourceId, FrozenSet<ChannelReference> AdvertisedChannels, IReadOnlyList<ChannelMapping> Mappings);

public sealed class AvatarComposition
{
    private static readonly IReadOnlyDictionary<string, double> EmptyParameters =
        new ReadOnlyDictionary<string, double>(new Dictionary<string, double>());
    private readonly IReadOnlyList<AvatarRoute> routes;

    internal AvatarComposition(IEnumerable<AvatarRoute> routes) => this.routes = Array.AsReadOnly(routes.ToArray());

    public ComposedFrame Compose(AvatarFrame frame, PlaybackFrameGate gate, PlaybackPosition? playback)
    {
        ArgumentNullException.ThrowIfNull(gate);
        frame.Validate();
        var selected = routes.Where(r => r.SourceId == frame.SourceId).ToArray();
        ContractRules.Require(selected.Length > 0, "This animation source owns no selected aspect.");
        var advertised = selected[0].AdvertisedChannels;
        ContractRules.Require(frame.Blendshapes.Keys.All(name => advertised.Contains(new() { Blendshape = name })) &&
            frame.Semantics.All(item => advertised.Contains(new() { Semantic = item.Channel })),
            "The frame contains channels absent from the selected source capabilities.");
        var parameters = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var mapping in selected.SelectMany(r => r.Mappings))
        {
            ContractRules.Require(frame.TryGetValue(mapping.Source, out var coefficient),
                "The frame is missing a mapped channel; incomplete frames cannot substitute neutral values.");
            var value = mapping.OutputMinimum + coefficient * (mapping.OutputMaximum - mapping.OutputMinimum);
            // Contain interpolation roundoff; coefficients and authored endpoints have already been validated.
            parameters.Add(mapping.TargetParameterId, Math.Clamp(value,
                Math.Min(mapping.OutputMinimum, mapping.OutputMaximum), Math.Max(mapping.OutputMinimum, mapping.OutputMaximum)));
        }
        var disposition = gate.TryAccept(frame, playback);
        return new(disposition, disposition == FrameDisposition.Accepted
            ? new ReadOnlyDictionary<string, double>(parameters) : EmptyParameters);
    }
}
