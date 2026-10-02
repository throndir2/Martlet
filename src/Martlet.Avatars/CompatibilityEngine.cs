using Martlet.Core.Contracts;

namespace Martlet.Avatars;

public enum CompatibilityIssueCode
{
    UnknownCapabilities, UnsupportedAspect, MappingRequired, PartialMapping, MappingBindingMismatch,
    SourceChannelMissing, ModelParameterMissing, AspectMismatch, MappingOutOfBounds, RuntimeNotReady,
    MissingAssignment, ConflictingSources, UnrequestedAspect, OmittedAndAssigned, OverlappingTarget
}

public enum RemedyKind { CheckCapabilities, CheckRuntime, SelectSource, SelectMapping, OmitAspect, AcceptReducedMapping }

public sealed record CompatibilityIssue(
    CompatibilityIssueCode Code, AvatarAspect Aspect, IReadOnlyList<RemedyKind> Remedies)
{
    public string ActionId => Code switch
    {
        CompatibilityIssueCode.RuntimeNotReady => "avatar.check_runtime",
        CompatibilityIssueCode.UnknownCapabilities => "avatar.check_capabilities",
        CompatibilityIssueCode.ConflictingSources or CompatibilityIssueCode.OverlappingTarget => "avatar.choose_owner",
        _ => "avatar.review_mapping"
    };

    public string Summary => Code switch
    {
        CompatibilityIssueCode.UnknownCapabilities => "Inspect the model controls first.",
        CompatibilityIssueCode.UnsupportedAspect => "This model does not support this movement.",
        CompatibilityIssueCode.MappingRequired => "Choose a mapping for this movement.",
        CompatibilityIssueCode.PartialMapping => "This mapping only covers part of the movement. Accept it or choose another.",
        CompatibilityIssueCode.MappingBindingMismatch => "This mapping belongs to another source or model.",
        CompatibilityIssueCode.SourceChannelMissing => "The selected source is missing a needed channel.",
        CompatibilityIssueCode.ModelParameterMissing => "The model is missing a needed control.",
        CompatibilityIssueCode.AspectMismatch => "This mapping connects the wrong movement type.",
        CompatibilityIssueCode.MappingOutOfBounds => "This mapping moves a control outside its allowed range.",
        CompatibilityIssueCode.RuntimeNotReady => "Start the source and renderer, then try again.",
        CompatibilityIssueCode.MissingAssignment => "Choose a compatible source or omit this movement.",
        CompatibilityIssueCode.ConflictingSources => "Choose one source for this movement.",
        CompatibilityIssueCode.UnrequestedAspect => "This assignment is not part of the current request.",
        CompatibilityIssueCode.OmittedAndAssigned => "A movement cannot be both omitted and assigned.",
        CompatibilityIssueCode.OverlappingTarget => "A model control can have only one source.",
        _ => throw new ArgumentOutOfRangeException()
    };
}

public sealed record CompatibilityAssessment(
    Compatibility Compatibility,
    RuntimeReadiness SourceReadiness,
    RuntimeReadiness ModelReadiness,
    IReadOnlyList<CompatibilityIssue> Issues)
{
    public bool Ready => SourceReadiness == RuntimeReadiness.Available && ModelReadiness == RuntimeReadiness.Available;
}

public static class CompatibilityEngine
{
    public static CompatibilityAssessment Assess(
        SourceCapabilities source, ModelCapabilities model, AvatarAspect aspect, MappingProfile? profile)
    {
        source.Validate();
        model.Validate();
        ContractRules.Defined(aspect);
        profile?.Validate();
        var issues = new List<CompatibilityIssue>();
        var support = Evaluate(source, model, aspect, profile, issues);
        if (source.Readiness != RuntimeReadiness.Available || model.Readiness != RuntimeReadiness.Available)
            issues.Add(Issue(CompatibilityIssueCode.RuntimeNotReady, aspect));
        return new(support, source.Readiness, model.Readiness, issues.AsReadOnly());
    }

    private static Compatibility Evaluate(SourceCapabilities source, ModelCapabilities model,
        AvatarAspect aspect, MappingProfile? profile, List<CompatibilityIssue> issues)
    {
        if (!source.ChannelsKnown || !model.MetadataKnown)
        {
            issues.Add(Issue(CompatibilityIssueCode.UnknownCapabilities, aspect));
            return Compatibility.Unknown;
        }
        var available = source.Channels.Where(c => AvatarChannels.Aspect(c) == aspect).ToHashSet();
        if (available.Count == 0 || !model.Parameters.Any(p => p.Aspect == aspect))
        {
            issues.Add(Issue(CompatibilityIssueCode.UnsupportedAspect, aspect));
            return Compatibility.Unsupported;
        }
        if (profile is null)
        {
            issues.Add(Issue(CompatibilityIssueCode.MappingRequired, aspect));
            return Compatibility.RequiresMapping;
        }
        if (profile.SourceId != source.SourceId || profile.ModelId != model.ModelId)
        {
            issues.Add(Issue(CompatibilityIssueCode.MappingBindingMismatch, aspect));
            return Compatibility.Unsupported;
        }
        var mappings = profile.Mappings.Where(m => AvatarChannels.Aspect(m.Source) == aspect).ToArray();
        if (mappings.Length == 0)
        {
            issues.Add(Issue(CompatibilityIssueCode.MappingRequired, aspect));
            return Compatibility.RequiresMapping;
        }
        foreach (var mapping in mappings)
        {
            if (!available.Contains(mapping.Source))
                issues.Add(Issue(CompatibilityIssueCode.SourceChannelMissing, aspect));
            var target = model.Parameters.SingleOrDefault(p => p.Id == mapping.TargetParameterId);
            if (target is null) issues.Add(Issue(CompatibilityIssueCode.ModelParameterMissing, aspect));
            else if (target.Aspect != aspect) issues.Add(Issue(CompatibilityIssueCode.AspectMismatch, aspect));
            else if (Math.Min(mapping.OutputMinimum, mapping.OutputMaximum) < target.Minimum ||
                Math.Max(mapping.OutputMinimum, mapping.OutputMaximum) > target.Maximum)
                issues.Add(Issue(CompatibilityIssueCode.MappingOutOfBounds, aspect));
        }
        if (issues.Count != 0) return Compatibility.Unsupported;
        if (!available.SetEquals(mappings.Select(m => m.Source)))
        {
            issues.Add(Issue(CompatibilityIssueCode.PartialMapping, aspect));
            return Compatibility.Reduced;
        }
        return Compatibility.Supported;
    }

    internal static CompatibilityIssue Issue(CompatibilityIssueCode code, AvatarAspect aspect) => new(code, aspect,
        Array.AsReadOnly(code switch
        {
            CompatibilityIssueCode.RuntimeNotReady => new[] { RemedyKind.CheckRuntime, RemedyKind.OmitAspect },
            CompatibilityIssueCode.UnknownCapabilities => [RemedyKind.CheckCapabilities, RemedyKind.SelectSource, RemedyKind.OmitAspect],
            CompatibilityIssueCode.PartialMapping => [RemedyKind.AcceptReducedMapping, RemedyKind.SelectMapping, RemedyKind.OmitAspect],
            CompatibilityIssueCode.UnsupportedAspect => [RemedyKind.SelectSource, RemedyKind.OmitAspect],
            CompatibilityIssueCode.ConflictingSources or CompatibilityIssueCode.OverlappingTarget =>
                [RemedyKind.SelectSource, RemedyKind.OmitAspect],
            _ => [RemedyKind.SelectMapping, RemedyKind.SelectSource, RemedyKind.OmitAspect]
        }));
}
