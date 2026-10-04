using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>Built-in Audio2Face mouth mapping for models without a reviewed user mapping.</summary>
internal static class Audio2FaceAutoMapping
{
    internal const string MappingId = "auto-mouth";

    internal static AvatarConfiguration? Create(RendererCapabilities capabilities, AvatarRenderer renderer, string sourceId)
    {
        var mouth = capabilities.Parameters.Where(p => p.Aspects.Contains(nameof(AvatarAspect.Mouth), StringComparer.Ordinal)).ToArray();
        RendererParameter? Find(string id) => mouth.FirstOrDefault(p => p.Id == id);
        var mappings = new List<ChannelMapping>();
        void Add(string channel, RendererParameter? target)
        {
            if (target is null || mappings.Any(m => m.TargetParameterId == target.Id)) return;
            mappings.Add(new()
            {
                Source = new() { Blendshape = channel }, TargetParameterId = target.Id,
                OutputMinimum = target.Neutral, OutputMaximum = target.Neutral < target.Maximum ? target.Maximum : target.Minimum
            });
        }
        if (renderer == AvatarRenderer.Live2D)
        {
            // The authored LipSync group (reported with only the Mouth aspect) is the mouth-open control.
            Add("jawOpen", mouth.FirstOrDefault(p => p.Aspects is [nameof(AvatarAspect.Mouth)]) ?? Find("ParamMouthOpenY"));
            Add("mouthSmileLeft", Find("ParamMouthForm"));
        }
        else
        {
            // VRM's vowel shapes: the jaw for aa, rounded lips for oh and ou, spread lips for ee and (less) ih.
            Add("jawOpen", Find("aa"));
            Add("mouthFunnel", Find("oh"));
            Add("mouthPucker", Find("ou"));
            Add("mouthSmileLeft", Find("ee"));
            Add("mouthStretchLeft", Find("ih"));
        }
        if (mappings.Count == 0) return null;
        return new()
        {
            Version = ContractVersion.Current, Enabled = true, PreferredBackend = AvatarBackend.Audio2Face,
            RequestedAspects = [AvatarAspect.Mouth], OmittedAspects = [],
            Assignments = [new() { Aspect = AvatarAspect.Mouth, SourceId = sourceId, MappingId = MappingId, AcceptReduced = true }],
            MappingProfiles = [new() { Id = MappingId, SourceId = sourceId, ModelId = capabilities.ModelId, Mappings = mappings }]
        };
    }
}
