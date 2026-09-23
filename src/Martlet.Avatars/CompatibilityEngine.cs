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
        CompatibilityIssueCode.UnknownCapabilities => "Inspect the selected source and model before claiming compatibility.",
        CompatibilityIssueCode.UnsupportedAspect => "The selected source or model does not expose this aspect.",
        CompatibilityIssueCode.MappingRequired => "Select an explicit mapping for this source and model.",
        CompatibilityIssueCode.PartialMapping => "This mapping covers only part of the source aspect; explicitly accept reduced fidelity or select another mapping.",
        CompatibilityIssueCode.MappingBindingMismatch => "The selected mapping belongs to a different source or model.",
        CompatibilityIssueCode.SourceChannelMissing => "The mapping requests a channel the selected source does not expose.",
        CompatibilityIssueCode.ModelParameterMissing => "The mapping targets a parameter absent from this model.",
        CompatibilityIssueCode.AspectMismatch => "The source channel and target parameter belong to different aspects.",
        CompatibilityIssueCode.MappingOutOfBounds => "Mapping output exceeds the authored model parameter bounds.",
        CompatibilityIssueCode.RuntimeNotReady => "The source and renderer must both be available; installation alone is insufficient.",
        CompatibilityIssueCode.MissingAssignment => "Choose one compatible source or explicitly omit this requested aspect.",
        CompatibilityIssueCode.ConflictingSources => "Choose one source for this aspect; multiple writers and arbitrary blending are unsupported.",
        CompatibilityIssueCode.UnrequestedAspect => "Assignments and omissions must belong to the requested aspects.",
        CompatibilityIssueCode.OmittedAndAssigned => "An aspect cannot be both assigned and intentionally omitted.",
        CompatibilityIssueCode.OverlappingTarget => "A model parameter can have only one selected writer.",
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
