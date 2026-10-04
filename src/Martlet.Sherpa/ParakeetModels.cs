namespace Martlet.Sherpa;

/// <summary>One NVIDIA Parakeet speech-to-text model Martlet can download and run on this PC's processor through sherpa-onnx's
/// offline transducer (NeMo TDT, greedy search, 80 features). Its files come from Hugging Face at a pinned revision, each
/// checked for its exact size and SHA-256, into <c>models\&lt;Id&gt;</c> under the data folder's speech directory, with a
/// NOTICE file beside it.</summary>
public sealed class ParakeetModel
{
    internal ParakeetModel(string id, string name, string languages, bool englishOnly, string repository, string revision,
        (string File, long Bytes, string Sha256) encoder, (string File, long Bytes, string Sha256) decoder,
        (string File, long Bytes, string Sha256) joiner, (string File, long Bytes, string Sha256) tokens, string noticeFile, string notice)
    {
        Id = id;
        Name = name;
        Languages = languages;
        EnglishOnly = englishOnly;
        Repository = repository;
        Revision = revision;
        Encoder = encoder.File;
        Decoder = decoder.File;
        Joiner = joiner.File;
        Tokens = tokens.File;
        NoticeFile = noticeFile;
        Notice = notice;
        Downloads = [.. new[] { encoder, decoder, joiner, tokens }.Select(file =>
            new SherpaDownload(new($"https://huggingface.co/{repository}/resolve/{revision}/{file.File}"), file.Bytes, file.Sha256,
                [new($@"models\{id}\{file.File}", file.Bytes, file.Sha256)]))];
    }

    /// <summary>The model ID a Listening route carries (Martlet.Core's LocalSpeechSetup), also its folder name.</summary>
    public string Id { get; }
    /// <summary>The model's name: "Parakeet TDT 110M".</summary>
    public string Name { get; }
    /// <summary>What it understands: "English" or "25 European languages".</summary>
    public string Languages { get; }
    public bool EnglishOnly { get; }
    /// <summary>The Hugging Face repository and revision its files come from.</summary>
    public string Repository { get; }
    public string Revision { get; }
    internal string Encoder { get; }
    internal string Decoder { get; }
    internal string Joiner { get; }
    internal string Tokens { get; }
    /// <summary>The attribution and license file written beside the models (in <c>models\</c>).</summary>
    public string NoticeFile { get; }
    internal string Notice { get; }
    internal IReadOnlyList<SherpaDownload> Downloads { get; }

    /// <summary>The bytes downloaded to install it.</summary>
    public long DownloadBytes => Downloads.Sum(d => d.Bytes);

    public override string ToString() => $"{Name} ({Languages})";
}

/// <summary>The Parakeet models Martlet offers for listening on this PC, fastest first (measured with sherpa-onnx 1.13.8 on the
/// processor; see docs/VOICE_LATENCY.md): TDT 110M (English, the fastest), TDT 0.6B v2 (English, the most accurate) and TDT
/// 0.6B v3 (25 European languages, the first one Martlet offered).</summary>
public static class ParakeetModels
{
    public const string Tdt110mEnglishId = "parakeet-tdt-110m-en";
    public const string V2EnglishId = "parakeet-tdt-0.6b-v2-int8";
    public const string V3Id = "parakeet-tdt-0.6b-v3-int8";

    private const string CcBy = "License: Creative Commons Attribution 4.0 International (CC BY 4.0), https://creativecommons.org/licenses/by/4.0/\n";

    /// <summary>NVIDIA Parakeet TDT-CTC 110M's transducer (TDT) branch, exported to ONNX by sherpa-onnx (fp32: its int8 build
    /// is only published as a .tar.bz2 archive, and fp32 measured as fast on the processor). English only.</summary>
    public static ParakeetModel Tdt110mEnglish { get; } = new(Tdt110mEnglishId, "Parakeet TDT 110M", "English", englishOnly: true,
        "csukuangfj/sherpa-onnx-nemo-parakeet_tdt_transducer_110m-en-36000", "e9bea5a06247dc3f55319ff23d34b0328f2f5ddf",
        ("encoder.onnx", 456_050_698, "db260f1073c654c37dd65006885d1ee98ff16c22463b1ef992bbcabc29780a3f"),
        ("decoder.onnx", 15_753_086, "3da156bde41a04c94ef783e0bd92928e9974e08645b976a22d0c3e1063510249"),
        ("joiner.onnx", 5_596_854, "b603765c0724a0768c378a23326dabbeb9cfea932d260e4fcc14384fa5fd5aff"),
        ("tokens.txt", 9_953, "450e56bd2f036fe5b6aa821865838cc5aa9d8b0106134ce9a9ba0664abe6cd10"),
        "Parakeet-TDT-110M-NOTICE.txt",
        "NVIDIA Parakeet TDT-CTC 110M (its TDT transducer branch), https://huggingface.co/nvidia/parakeet-tdt_ctc-110m\n" + CcBy +
        "Exported to ONNX by the sherpa-onnx project (Apache-2.0): " +
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet_tdt_transducer_110m-en-36000 " +
        "(revision e9bea5a06247dc3f55319ff23d34b0328f2f5ddf)\n");

    /// <summary>NVIDIA Parakeet TDT 0.6B v2, converted to int8 ONNX by sherpa-onnx. English only.</summary>
    public static ParakeetModel V2English { get; } = new(V2EnglishId, "Parakeet TDT 0.6B v2", "English", englishOnly: true,
        "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "1ab9323565ddb038682214b292f588070a538ce2",
        ("encoder.int8.onnx", 652_184_296, "a32b12d17bbbc309d0686fbbcc2987b5e9b8333a7da83fa6b089f0a2acd651ab"),
        ("decoder.int8.onnx", 7_257_753, "b6bb64963457237b900e496ee9994b59294526439fbcc1fecf705b31a15c6b4e"),
        ("joiner.int8.onnx", 1_739_080, "7946164367946e7f9f29a122407c3252b680dbae9a51343eb2488d057c3c43d2"),
        ("tokens.txt", 9_384, "ec182b70dd42113aff6c5372c75cac58c952443eb22322f57bbd7f53977d497d"),
        "Parakeet-TDT-0.6B-v2-NOTICE.txt",
        "NVIDIA Parakeet TDT 0.6B v2, https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2\n" + CcBy +
        "Converted to int8 ONNX by the sherpa-onnx project (Apache-2.0): " +
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8 (revision 1ab9323565ddb038682214b292f588070a538ce2)\n");

    /// <summary>NVIDIA Parakeet TDT 0.6B v3, converted to int8 ONNX by sherpa-onnx: 25 European languages, detected by itself.</summary>
    public static ParakeetModel V3 { get; } = new(V3Id, "Parakeet TDT 0.6B v3", "25 European languages", englishOnly: false,
        "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "2bda32ec70b097a55adaa07d9a7173915b43cc78",
        ("encoder.int8.onnx", 652_184_281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
        ("decoder.int8.onnx", 11_845_275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
        ("joiner.int8.onnx", 6_355_277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
        ("tokens.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
        // Its NOTICE keeps the name it always had, so an existing download's stays current.
        "Parakeet-NOTICE.txt",
        "NVIDIA Parakeet TDT 0.6B v3, https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3\n" + CcBy +
        "Converted to int8 ONNX by the sherpa-onnx project (Apache-2.0): https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8\n");

    /// <summary>Every model, fastest first.</summary>
    public static IReadOnlyList<ParakeetModel> All { get; } = [Tdt110mEnglish, V2English, V3];

    /// <summary>The model with this ID, or null when this Martlet doesn't know it (a newer one chose it).</summary>
    public static ParakeetModel? Find(string? id) => All.FirstOrDefault(model => model.Id == id);

    /// <summary>The model with this ID. Throws <see cref="ArgumentException"/> for one this Martlet doesn't know.</summary>
    public static ParakeetModel Get(string id) => Find(id) ?? throw new ArgumentException("This Parakeet model isn't one Martlet knows.", nameof(id));
}
