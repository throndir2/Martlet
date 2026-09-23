using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Martlet.Core.Settings;
using Martlet.Updates;

namespace Martlet.Launcher.Tests;

internal sealed class LauncherFixture : IDisposable
{
    private readonly RSA signer = RSA.Create(3072);
    private readonly Dictionary<string, byte[]> files =
        new(StringComparer.Ordinal);
    private InstalledVersionFacts installed;
    internal Action? InstalledFactsRead { get; set; }
    private readonly List<PublisherExecutionGrant> executionGrants = [];
    internal LauncherPublisherPolicy PublisherPolicy { get; set; } =
        new("fixture-empty", DateTimeOffset.UtcNow.AddHours(1), []);
    internal PublisherExecutionGrant Grant(string version, LauncherExecutionPurpose purpose) =>
        executionGrants.Single(grant => grant.Version == version && grant.Purpose == purpose);

    internal string Root { get; } =
        Directory.CreateTempSubdirectory("Martlet.Launcher.Tests-").FullName;
    internal string ControlRoot => Path.Combine(Root, "control");
    internal string StagingRoot => Path.Combine(ControlRoot, "stages");
    internal string ActivationRoot => Path.Combine(Root, "activation");
    internal string LauncherRoot => Path.Combine(Root, "launcher");
    internal SettingsStore Settings { get; }
    internal LocalStagingEngine Staging { get; }
    internal LocalSelectionEngine Selection { get; }
    internal LocalActivationEngine? Activation { get; private set; }

    internal LauncherFixture()
    {
        Directory.CreateDirectory(StagingRoot);
        Directory.CreateDirectory(ActivationRoot);
        Directory.CreateDirectory(LauncherRoot);
        var installedRoot = Path.Combine(Root, "installed");
        Directory.CreateDirectory(installedRoot);
        Settings = new(Path.Combine(Root, "data"));
        var settings = SetupSettings.Begin(null);
        Assert.True(Settings.SaveAsync(settings, null)
            .GetAwaiter().GetResult().Saved);
        var loaded = Settings.LoadAsync().GetAwaiter().GetResult();
        installed = new(
            "0.1.0.0",
            "win-x64",
            installedRoot,
            Wire.Hash("fixture-installed"u8),
            loaded.Settings!.SchemaVersion,
            loaded.Revision!);
        Staging = new(
            StagingRoot,
            new UpdateTrustPolicy([signer.ExportSubjectPublicKeyInfo()]),
            () => { InstalledFactsRead?.Invoke(); return installed; });
        Selection = new(ControlRoot, Staging, Settings);
        LoadFixtureChild();
    }

    internal SelectionReceipt InitializeSelection() =>
        Selection.Initialize(installed);

    internal string Stage(
        string version,
        string activationMode = "initialized-exit",
        string launchMode = "initialized-exit")
    {
        files["Desktop/fixture-mode.txt"] = Encoding.UTF8.GetBytes(
            $"activation={activationMode}\nlaunch={launchMode}\n");
        var source = Path.Combine(Root, "sources", version);
        Directory.CreateDirectory(source);
        var archive = Path.Combine(source, "candidate.zip");
        var envelope = Path.Combine(source, "candidate.json");
        BuildPackage(version, archive, envelope);
        var destination = Path.Combine(StagingRoot, "version-" + version);
        var plan = Staging.Preview(archive, envelope, destination);
        foreach (var purpose in Enum.GetValues<LauncherExecutionPurpose>())
            executionGrants.Add(new(plan.SignerId, plan.Version, plan.ArchiveSha256, plan.ManifestSha256,
                plan.Files.Single(file => file.Path == "Desktop/Martlet.Desktop.exe").Sha256,
                new string('b', 40), false, purpose));
        PublisherPolicy = new("fixture-" + executionGrants.Count, DateTimeOffset.UtcNow.AddHours(1), executionGrants);
        Staging.Stage(plan, plan.Approve(
            plan.ArchiveSha256,
            plan.EnvelopeSha256,
            plan.Destination,
            plan.Installed));
        return destination;
    }

    internal SelectionReceipt Select(string stage)
    {
        var before = Selection.Inspect();
        var plan = Selection.PrepareActivation(
            stage, before.Revision, Snapshot());
        return Selection.CommitSelection(
            plan,
            plan.Approve(plan.TransactionId, plan.ExpectedRevision, plan.PlanDigest));
    }

    internal LocalActivationEngine ConfigureActivation(
        TimeSpan? timeout = null,
        Action<LauncherEvidencePoint, Guid, int?>? io = null,
        Func<LauncherPublisherPolicy>? publisherPolicy = null)
    {
        publisherPolicy ??= () => PublisherPolicy;
        if (io is null)
        {
            Activation = LauncherActivationComposition.Create(
                ActivationRoot, Selection, LauncherRoot, timeout, publisherPolicy);
            return Activation;
        }
        var probe = new LauncherActivationReadinessProbe(
            LauncherRoot, timeout, publisherPolicy)
        {
            Io = io
        };
        Activation = new LocalActivationEngine(
            ActivationRoot, Selection, probe);
        return Activation;
    }

    internal ActivationReceipt InitializeActivation()
    {
        var selection = Selection.Inspect();
        return (Activation ??
            throw new InvalidOperationException()).Initialize(selection.Revision);
    }

    internal ActivationReceipt ActivateCurrent()
    {
        var engine = Activation ??
            throw new InvalidOperationException();
        var before = engine.Inspect();
        var selected = Selection.Inspect();
        var plan = engine.PrepareActivation(
            before.Revision, selected.Revision);
        return engine.Activate(
            plan,
            plan.Approve(
                plan.TransactionId,
                plan.ExpectedActivationRevision,
                plan.ExpectedSelectionRevision,
                plan.ExpectedSettingsRevision,
                plan.PlanDigest));
    }

    internal string Snapshot()
    {
        var path = Path.Combine(
            ControlRoot, Guid.NewGuid().ToString("N") + ".martlet-config");
        Settings.CreateConfigurationSnapshotAsync(path)
            .GetAwaiter().GetResult();
        return path;
    }

    internal void ChangeSettings()
    {
        var current = Settings.LoadAsync().GetAwaiter().GetResult();
        var changed = current.Settings! with
        {
            Profile = current.Settings.Profile with
            {
                Kind = current.Settings.Profile.Kind == ProfileKind.Fixture
                    ? ProfileKind.Api
                    : ProfileKind.Fixture
            }

        };
        Assert.True(Settings.SaveAsync(changed, current.Revision)
            .GetAwaiter().GetResult().Saved);
        RefreshInstalled();
    }

    internal void ChangeInstalledImage() =>
        installed = installed with { InstallationRevision = Wire.Hash("changed-fixture-image"u8) };

    internal string PayloadFile(string stage, string relative) =>
        Path.Combine(stage, "payload",
            relative.Replace('/', Path.DirectorySeparatorChar));

    internal string[] Attempts() =>
        Directory.Exists(LauncherRoot)
            ? Directory.GetDirectories(
                LauncherRoot, "attempt-*", SearchOption.AllDirectories)
            : [];

    private void RefreshInstalled()
    {
        var loaded = Settings.LoadAsync().GetAwaiter().GetResult();
        installed = installed with
        {
            SettingsSchemaVersion = loaded.Settings!.SchemaVersion,
            SettingsRevision = loaded.Revision!
        };
    }

    private void LoadFixtureChild()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "fixture-child");
        Assert.True(Directory.Exists(source), source);
        foreach (var path in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                continue;
            files["Desktop/" + name] = File.ReadAllBytes(path);
        }
        Assert.Contains("Desktop/Martlet.Desktop.exe", files.Keys);
        Assert.Contains("Desktop/Martlet.Desktop.dll", files.Keys);
        Assert.Contains("Desktop/Martlet.Readiness.dll", files.Keys);
        files["Doctor/Martlet.Doctor.exe"] = "INERT"u8.ToArray();
        files["notices/INTERNAL.txt"] =
            "Authored inert launcher test fixture only."u8.ToArray();
    }

    private void BuildPackage(
        string version,
        string archive,
        string envelope)
    {
        files.Remove("manifest.json");
        files.Remove("SHA256SUMS.txt");
        var internalManifest = new InternalPayloadManifest
        {
            SchemaVersion = 1,
            Channel = "INTERNAL DEVELOPMENT ONLY - UNSIGNED",
            ApplicationVersion = version,
            Rid = "win-x64",
            SdkVersion = "10.0.401",
            RuntimeVersion = "10.0.12",
            SourceCommit = new string('b', 40),
            SourceDirty = false,
            Files = files.Select(pair => new PayloadFile
            {
                Path = pair.Key.Replace('/', '\\'),
                Bytes = pair.Value.Length,
                Sha256 = Wire.Hash(pair.Value)
            }).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray()
        };
        var internalBytes = Wire.Write(internalManifest);
        files["manifest.json"] = internalBytes;
        files["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(
            string.Concat(internalManifest.Files.Select(file =>
                $"{file.Sha256}  {file.Path}\n")) +
            $"{Wire.Hash(internalBytes)}  manifest.json\n");
        using (var output = new FileStream(
                   archive, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(
                   output, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var pair in files)
            {
                var entry = zip.CreateEntry(
                    pair.Key, CompressionLevel.NoCompression);
                using var sink = entry.Open();
                sink.Write(pair.Value);
            }
        }
        var manifest = new CandidateManifest
        {
            FormatVersion = 1,
            MinimumReaderFormat = 1,
            Application = "Martlet.Update",
            Algorithm = "RSA-PSS-SHA256",
            SignerId = Wire.Hash(signer.ExportSubjectPublicKeyInfo()),
            ApplicationVersion = version,
            Rid = "win-x64",
            SettingsMinimumReader = 1,
            SettingsMaximumReader = AppSettings.CurrentSchemaVersion,
            ArchiveBytes = new FileInfo(archive).Length,
            ArchiveSha256 = Wire.Hash(File.ReadAllBytes(archive)),
            Files = Inventory()
        };
        var manifestBytes = Wire.Write(manifest);
        var signed = new CandidateEnvelope
        {
            Manifest = manifestBytes,
            Signature = signer.SignData(
                manifestBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss)
        };
        File.WriteAllBytes(envelope, Wire.Write(signed));
    }

    private PayloadFile[] Inventory() =>
        files.Select(pair => new PayloadFile
        {
            Path = pair.Key,
            Bytes = pair.Value.Length,
            Sha256 = Wire.Hash(pair.Value)
        }).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        signer.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
