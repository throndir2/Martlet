namespace Martlet.VoiceActivity;

internal enum ModelElementType { Other, Float32, Int64 }

internal sealed record ModelTensorSchema(
    string Name, bool PlainTensor, ModelElementType ElementType, int[] Dimensions, string[] Symbols);

internal static class ModelSchema
{
    internal static void Validate(
        IReadOnlyList<ModelTensorSchema> inputs, IReadOnlyList<ModelTensorSchema> outputs, int overridableInitializers)
    {
        if (inputs is null || outputs is null)
            throw new VoiceActivityException(VoiceActivityFailureCode.ModelSchemaMismatch);
        Require(inputs.Count == 3 && outputs.Count == 2 && overridableInitializers == 0);
        Tensor(inputs, "input", ModelElementType.Float32, [-1, -1], ["batch", "sequence"]);
        Tensor(inputs, "sr", ModelElementType.Int64, [], []);
        Tensor(inputs, "state", ModelElementType.Float32, [2, -1, 128], ["", "batch", ""]);
        Tensor(outputs, "output", ModelElementType.Float32, [-1, 1], ["batch", ""]);
        Tensor(outputs, "stateN", ModelElementType.Float32, [-1, -1, -1],
            ["AddstateN_dim_0", "batch", "AddstateN_dim_2"]);
    }

    internal static void ValidateWindow(ReadOnlySpan<float> window)
    {
        VadCheck.Require(window.Length == VoiceActivityOptions.WindowSamples);
        foreach (var value in window)
            VadCheck.Require(float.IsFinite(value) && value is >= -1 and <= 1);
    }

    internal static float ValidateResult(
        ReadOnlySpan<long> scoreShape, ReadOnlySpan<float> scores,
        ReadOnlySpan<long> stateShape, ReadOnlySpan<float> state)
    {
        VadCheck.Require(scoreShape.SequenceEqual([1L, 1L]) && scores.Length == 1 &&
            stateShape.SequenceEqual([2L, 1L, 128L]) && state.Length == 256,
            VoiceActivityFailureCode.InvalidScore);
        VadCheck.Require(float.IsFinite(scores[0]) && scores[0] is >= 0 and <= 1,
            VoiceActivityFailureCode.InvalidScore);
        foreach (var value in state)
            VadCheck.Require(float.IsFinite(value), VoiceActivityFailureCode.InvalidScore);
        return scores[0];
    }

    private static void Tensor(IReadOnlyList<ModelTensorSchema> tensors, string name,
        ModelElementType elementType, int[] dimensions, string[] symbols)
    {
        ModelTensorSchema? found = null;
        foreach (var tensor in tensors)
        {
            if (tensor is null) throw new VoiceActivityException(VoiceActivityFailureCode.ModelSchemaMismatch);
            if (!string.Equals(tensor.Name, name, StringComparison.Ordinal)) continue;
            Require(found is null);
            found = tensor;
        }
        if (found is null) throw new VoiceActivityException(VoiceActivityFailureCode.ModelSchemaMismatch);
        if (found.Dimensions is null || found.Symbols is null)
            throw new VoiceActivityException(VoiceActivityFailureCode.ModelSchemaMismatch);
        Require(found.PlainTensor && found.ElementType == elementType &&
            found.Dimensions.AsSpan().SequenceEqual(dimensions) &&
            found.Symbols.SequenceEqual(symbols, StringComparer.Ordinal));
    }

    private static void Require(bool condition) =>
        VadCheck.Require(condition, VoiceActivityFailureCode.ModelSchemaMismatch);
}
