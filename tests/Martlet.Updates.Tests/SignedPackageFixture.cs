using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Martlet.Updates;

namespace Martlet.Updates.Tests;

public sealed class SigningKeys : IDisposable
{
    internal RSA Approved { get; } = RSA.Create(3072);
    internal RSA Stranger { get; } = RSA.Create(3072);
    public void Dispose() { Approved.Dispose(); Stranger.Dispose(); }
}

internal sealed class SignedPackageFixture : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("Martlet.Updates.Tests-").FullName;
    internal string StagingRoot => Path.Combine(Root, "staging");
    internal string Destination => Path.Combine(StagingRoot, "selected-version");
    internal string Archive => Path.Combine(Root, "candidate.zip");
    internal string Envelope => Path.Combine(Root, "candidate.json");
    internal string InstalledRoot => Path.Combine(Root, "installed");
    internal string ExecutedCanary => Path.Combine(Root, "PAYLOAD-EXECUTED");
    internal InstalledVersionFacts Current { get; set; }
    internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    internal SigningKeys Keys { get; }
    internal CandidateManifest Manifest { get; private set; } = null!;
    private readonly List<FileStream> privateSentinels = [];
    internal UpdateTrustPolicy Trust => new([Keys.Approved.ExportSubjectPublicKeyInfo()]);

    internal SignedPackageFixture(SigningKeys keys)
    {
        Keys = keys;
        Directory.CreateDirectory(StagingRoot);
        Directory.CreateDirectory(InstalledRoot);
        Current = new("0.1.0.0", "win-x64", InstalledRoot, Wire.Hash("installed"u8), 2, Wire.Hash("settings"u8));
        foreach (var name in new[] { "settings.json", "credential-vault", "models", "Martlet.Desktop.exe" })
        {
            var path = Path.Combine(InstalledRoot, name);
            File.WriteAllText(path, "PRIVATE-SENTINEL-" + name);
            privateSentinels.Add(new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        Files["Desktop/Martlet.Desktop.exe"] = "INERT TEST BYTES, NOT A QUALIFIED EXECUTABLE"u8.ToArray();
        Files["Doctor/Martlet.Doctor.exe"] = "INERT DOCTOR"u8.ToArray();
        Files["help/do-not-run.ps1"] = Encoding.UTF8.GetBytes($"[IO.File]::WriteAllText('{ExecutedCanary}', 'BAD')");
        Files["notices/INTERNAL.txt"] = "Ephemeral local test signer; NOT a publisher release."u8.ToArray();
        Build();
    }

    internal LocalStagingEngine Engine(Action<StagingIoPoint, CancellationToken>? io = null,
        StagingLimits? limits = null, Func<long>? availableBytes = null) =>
        new(StagingRoot, Trust, () => Current, limits) { Io = io, AvailableBytes = availableBytes };

    internal void Build(string version = "0.2.0.0")
    {
        Files.Remove("manifest.json");
        Files.Remove("SHA256SUMS.txt");
        var internalManifest = new InternalPayloadManifest
        {
            SchemaVersion = 1, Channel = "INTERNAL DEVELOPMENT ONLY - UNSIGNED",
            ApplicationVersion = version, Rid = "win-x64", SdkVersion = "10.0.401",
            RuntimeVersion = "10.0.9", SourceCommit = new('a', 40), SourceDirty = false,
            Files = Files.Select(f => new PayloadFile
            {
                Path = f.Key.Replace('/', '\\'), Bytes = f.Value.Length, Sha256 = Wire.Hash(f.Value)
            }).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray()
        };
        var manifestBytes = Wire.Write(internalManifest);
        Files["manifest.json"] = manifestBytes;
        Files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(
            string.Concat(internalManifest.Files.Select(f => $"{f.Sha256}  {f.Path}\n")) +
            $"{Wire.Hash(manifestBytes)}  manifest.json\n");
        WriteZip(Files.Select(f => new Entry(f.Key, f.Value)));
        Manifest = new()
        {
            FormatVersion = 1, MinimumReaderFormat = 1, Application = "Martlet.Update", Algorithm = "RSA-PSS-SHA256",
            SignerId = Wire.Hash(Keys.Approved.ExportSubjectPublicKeyInfo()), ApplicationVersion = version,
            Rid = "win-x64", SettingsMinimumReader = 1, SettingsMaximumReader = 2,
            ArchiveBytes = new FileInfo(Archive).Length, ArchiveSha256 = Wire.Hash(File.ReadAllBytes(Archive)),
            Files = Inventory()
        };
        Sign(Manifest);
    }

    internal sealed record Entry(string Name, byte[] Bytes, int? Attributes = null,
        CompressionLevel Compression = CompressionLevel.NoCompression);

    internal void WriteZip(IEnumerable<Entry> entries, bool descriptors = false)
    {
        using var file = new FileStream(Archive, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var sink = descriptors ? new NonSeekingWriter(file) : null;
        using var zip = new ZipArchive((Stream?)sink ?? file, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var entry in entries)
        {
            var zipped = zip.CreateEntry(entry.Name, entry.Compression);
            if (entry.Attributes is { } attributes) zipped.ExternalAttributes = attributes;
            using var stream = zipped.Open();
            stream.Write(entry.Bytes);
        }
    }

    internal PayloadFile[] Inventory() => Files.Select(f => new PayloadFile
    {
        Path = f.Key, Bytes = f.Value.Length, Sha256 = Wire.Hash(f.Value)
    }).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();

    internal void ResignArchive(bool updateInventory = false) => Sign(Manifest with
    {
        ArchiveBytes = new FileInfo(Archive).Length, ArchiveSha256 = Wire.Hash(File.ReadAllBytes(Archive)),
        Files = updateInventory ? Inventory() : Manifest.Files
    });

    internal void Sign(CandidateManifest manifest, RSA? signer = null)
    {
        Manifest = manifest;
        SignBytes(Wire.Write(manifest), signer);
    }

    internal void SignBytes(byte[] bytes, RSA? signer = null)
    {
        var envelope = new CandidateEnvelope
        {
            Manifest = bytes,
            Signature = (signer ?? Keys.Approved).SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)
        };
        File.WriteAllBytes(Envelope, Wire.Write(envelope));
    }

    internal StagingPlan Preview(LocalStagingEngine engine) => engine.Preview(Archive, Envelope, Destination);
    internal static StagingApproval Approve(StagingPlan plan) =>
        plan.Approve(plan.ArchiveSha256, plan.EnvelopeSha256, plan.Destination, plan.Installed);

    internal void AssertPrivateDataUnchanged()
    {
        Assert.False(File.Exists(ExecutedCanary));
        foreach (var sentinel in privateSentinels)
        {
            sentinel.Position = 0;
            using var reader = new StreamReader(sentinel, leaveOpen: true);
            Assert.Equal("PRIVATE-SENTINEL-" + Path.GetFileName(sentinel.Name), reader.ReadToEnd());
        }
    }

    public void Dispose()
    {
        foreach (var file in privateSentinels) file.Dispose();
        Directory.Delete(Root, recursive: true);
    }

    private sealed class NonSeekingWriter(Stream output) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
