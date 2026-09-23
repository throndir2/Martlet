using System.Text;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;

namespace Martlet.Core.Tests;

public sealed class CompanionSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Martlet.Companion.Tests", Guid.NewGuid().ToString("N"));
    private SettingsStore Store => new(directory);

    [Fact]
    public async Task VersionTwoMigratesAtomicallyAndPreservesExistingConfiguration()
    {
        var versionThree = SetupSettings.SelectRoute(SetupSettings.Begin(null), SetupRole.Llm, "model-1", null);
        var versionTwo = versionThree with
        {
            SchemaVersion = 2,
            Setup = versionThree.Setup!.DowngradeOpenAiForHistoricalSettings(),
            Companion = null,
            Memory = null
        };
        versionTwo.Validate();
        var saved = await Store.SaveAsync(versionTwo, null);
        var original = await File.ReadAllBytesAsync(Store.FilePath);

        var loaded = await Store.LoadAsync();
        var draft = CompanionSettings.Begin(loaded.Settings);
        Assert.Equal(AppSettings.CurrentSchemaVersion, draft.SchemaVersion);
        Assert.Equal(versionTwo.Profile.Id, draft.Profile.Id);
        Assert.Equal(versionTwo.Profile.Kind, draft.Profile.Kind);
        Assert.Equal(versionTwo.Profile.Credentials, draft.Profile.Credentials);
        Assert.Equal(versionTwo.Setup!.Checkpoint, draft.Setup!.Checkpoint);
        Assert.Equal(versionTwo.Setup.Routes.Select(route => route.ModelId),
            draft.Setup.Routes.Select(route => route.ModelId));
        Assert.All(draft.Setup.Routes, route => Assert.Equal(SetupRouteType.OpenAi, route.RouteType));
        Assert.Equal(versionTwo.Setup.PendingRemovals, draft.Setup.PendingRemovals);
        Assert.Equal("Martlet", draft.Companion!.ActivePersona.Name);
        Assert.Equal(ResponseStyleWeights.HelpfulOnly(), draft.Companion.ActivePersona.Styles);
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));

        var migrated = await Store.SaveAsync(draft, loaded.Revision);
        Assert.True(migrated.Saved);
        Assert.Equal(2, migrated.MigratedFromSchemaVersion);
        Assert.False(migrated.MigratedFromVersion1);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory, migrated.SnapshotFileName!)));
        Assert.Equal(ContractJson.Write(draft), ContractJson.Write((await Store.LoadAsync()).Settings!));
    }

    [Fact]
    public void ProfilesAreBoundedUniqueAndRevisioned()
    {
        var companion = CompanionSettings.Create();
        var first = companion.ActivePersona;
        var unchanged = companion.Update(first.Id, first.Name, first.Text, first.Styles);
        Assert.Same(companion, unchanged);

        var styles = first.Styles with { Helpful = 40, Sarcastic = 20, Silly = 20, Distracted = 10, PlayfulTeasing = 10 };
        var changed = companion.Update(first.Id, "Bird", "Listen before answering.", styles);
        Assert.NotEqual(first.ConfigurationRevision, changed.ActivePersona.ConfigurationRevision);
        Assert.Equal(styles, changed.ActivePersona.Styles);
        var second = changed.Add("Bird copy", changed.ActivePersona);
        Assert.Equal("Bird copy", second.ActivePersona.Name);
        Assert.NotEqual(changed.ActivePersona.Id, second.ActivePersona.Id);
        Assert.NotEqual(changed.ActivePersona.ConfigurationRevision, second.ActivePersona.ConfigurationRevision);
        Assert.Equal(changed.ActivePersona.Text, second.ActivePersona.Text);
        Assert.Equal(changed.ActivePersona.Styles, second.ActivePersona.Styles);
        Assert.Equal(changed.ActivePersona.Id, second.Remove(second.ActivePersonaId).ActivePersonaId);

        Assert.Throws<ContractException>(() => changed.Add("bird"));
        Assert.Throws<ContractException>(() => (styles with { Helpful = 0, Sarcastic = 0, Silly = 0, Distracted = 0, PlayfulTeasing = 0 }).Validate());
        Assert.Throws<ContractException>(() => (styles with { Helpful = 101 }).Validate());
        Assert.Throws<ContractException>(() => changed.Update(first.Id, " Bird ", first.Text, styles));
        Assert.Throws<ContractException>(() => changed.Update(first.Id, first.Name, new string('x', PersonaProfile.MaximumTextCharacters + 1), styles));
        Assert.Throws<ContractException>(() => changed.Update(first.Id, first.Name, "\uD800", styles));
        Assert.Throws<ContractException>(() => changed.Remove(first.Id));
    }

    [Fact]
    public void ResponseStyleSelectionHonorsWeightsAndIsDeterministic()
    {
        var one = new ResponseStyleWeights
        {
            Helpful = 0, Sarcastic = 0, Silly = 0, Distracted = 0, PlayfulTeasing = 100
        };
        Assert.Equal(ResponseStyle.PlayfulTeasing,
            ResponseStyleSelector.Select(one, _ => throw new InvalidOperationException("A sole style needs no sample.")));

        var mixed = new ResponseStyleWeights
        {
            Helpful = 40, Sarcastic = 20, Silly = 15, Distracted = 10, PlayfulTeasing = 15
        };
        var random = new Random(19092026);
        var counts = Enum.GetValues<ResponseStyle>().ToDictionary(style => style, _ => 0);
        for (var index = 0; index < 10_000; index++)
            counts[ResponseStyleSelector.Select(mixed, random.Next)]++;

        Assert.InRange(counts[ResponseStyle.Helpful], 3_800, 4_200);
        Assert.InRange(counts[ResponseStyle.Sarcastic], 1_800, 2_200);
        Assert.InRange(counts[ResponseStyle.Silly], 1_300, 1_700);
        Assert.InRange(counts[ResponseStyle.Distracted], 800, 1_200);
        Assert.InRange(counts[ResponseStyle.PlayfulTeasing], 1_300, 1_700);
        Assert.Throws<ContractException>(() => ResponseStyleSelector.Select(mixed, total => total));
    }

    [Fact]
    public async Task MaximumProfileCollectionFitsSettingsAndAggregateTextIsBounded()
    {
        var settings = CompanionSettings.Begin(null);
        var companion = settings.Companion!;
        var first = companion.ActivePersona;
        companion = companion.Update(first.Id, first.Name, new string('a', 1_024), first.Styles);
        while (companion.Personas.Count < CompanionSettings.MaximumPersonas)
            companion = companion.Add($"Persona {companion.Personas.Count + 1}",
                companion.ActivePersona with { Text = new string('a', 1_024) });

        settings = settings with { Companion = companion };
        var saved = await Store.SaveAsync(settings, null);
        Assert.True(saved.Saved);
        Assert.InRange((await File.ReadAllBytesAsync(Store.FilePath)).Length, 1, AppSettings.MaxFileBytes);

        var overflow = companion.ActivePersona with { Text = new string('a', 1_025) };
        Assert.Throws<ContractException>(() =>
            (companion with
            {
                Personas = companion.Personas.Select(persona =>
                    persona.Id == overflow.Id ? overflow : persona).ToArray()
            }).Validate());
    }

    [Fact]
    public async Task MaximumUtf8PersonasPersistWithoutDuplicatingTheActiveProfile()
    {
        var settings = CompanionSettings.Begin(null);
        var persona = settings.Companion!.ActivePersona;
        var companion = settings.Companion.Update(persona.Id, persona.Name, new string('\u00e9', 8192), persona.Styles);
        companion = companion.Add("Second", companion.ActivePersona);
        settings = settings with { Companion = companion };
        var saved = await Store.SaveAsync(settings, null);
        Assert.True(saved.Saved);
        var bytes = await File.ReadAllBytesAsync(Store.FilePath);
        Assert.DoesNotContain("\"active_persona\":", Encoding.UTF8.GetString(bytes));
        Assert.Equal(ContractJson.Write(settings), ContractJson.Write((await Store.LoadAsync()).Settings!));
        Assert.Equal(32_768, settings.Companion.Personas.Sum(item => Encoding.UTF8.GetByteCount(item.Text)));
    }

    [Fact]
    public async Task EarlierVersionThreeDerivedPersonaFieldStillLoadsWithoutRewriting()
    {
        var settings = CompanionSettings.Begin(null);
        var document = System.Text.Json.Nodes.JsonNode.Parse(ContractJson.Write(settings))!;
        document["companion"]!["active_persona"] = document["companion"]!["personas"]![0]!.DeepClone();
        var original = Encoding.UTF8.GetBytes(document.ToJsonString());
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Store.FilePath, original);
        var loaded = await Store.LoadAsync();
        Assert.Equal(SettingsLoadState.Loaded, loaded.State);
        Assert.Equal(ContractJson.Write(settings), ContractJson.Write(loaded.Settings!));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));
    }

    [Fact]
    public async Task StoreRejectsPersonaMutationWithoutFreshRevision()
    {
        var settings = CompanionSettings.Begin(null);
        var saved = await Store.SaveAsync(settings, null);
        var persona = settings.Companion!.ActivePersona;
        var bypass = settings with
        {
            Companion = settings.Companion with
            {
                Personas = [persona with { Text = "Changed without a revision." }]
            }
        };
        var rejected = await Store.SaveAsync(bypass, saved.Revision);
        Assert.False(rejected.Saved);
        Assert.Equal(ErrorCode.InvalidContract, rejected.Error!.Code);
        Assert.Equal(ContractJson.Write(settings), ContractJson.Write((await Store.LoadAsync()).Settings!));
    }

    [Fact]
    public void JsonRejectsUnknownAndFutureCompanionShapesWithoutLeakingContent()
    {
        var settings = CompanionSettings.Begin(null);
        var json = Encoding.UTF8.GetString(ContractJson.Write(settings));
        var unknown = json.Replace("\"active_persona_id\":", "\"private_canary\":\"PRIVATE-CONTENT\",\"active_persona_id\":", StringComparison.Ordinal);
        var malformed = Assert.Throws<ContractException>(() => SettingsJson.Read(Encoding.UTF8.GetBytes(unknown)));
        Assert.DoesNotContain("PRIVATE-CONTENT", malformed.Message);
        var companion = json.IndexOf("\"companion\"", StringComparison.Ordinal);
        var version = json.IndexOf("\"schema_version\": 1", companion, StringComparison.Ordinal);
        var future = json[..version] + "\"schema_version\": 99" + json[(version + "\"schema_version\": 1".Length)..];
        Assert.Equal(ErrorCode.UnsupportedVersion,
            Assert.Throws<ContractException>(() => SettingsJson.Read(Encoding.UTF8.GetBytes(future))).Code);
    }

    [Fact]
    public async Task TextImportExportIsBoundedStrictAndNeverOverwrites()
    {
        Directory.CreateDirectory(directory);
        var service = new CompanionSettingsService(Store);
        var source = Path.Combine(directory, "persona.txt");
        var output = Path.Combine(directory, "export.txt");
        await File.WriteAllTextAsync(source, "Helpful\nbut occasionally silly.", new UTF8Encoding(true));

        var imported = await service.ImportTextAsync(source);
        Assert.Equal(PersonaTextFileOutcome.Imported, imported.Outcome);
        Assert.Equal("Helpful\nbut occasionally silly.", imported.Text);
        var exported = await service.ExportTextAsync(output, imported.Text!);
        Assert.Equal(PersonaTextFileOutcome.Exported, exported.Outcome);
        Assert.Equal(imported.Text, await File.ReadAllTextAsync(output, new UTF8Encoding(false, true)));
        Assert.Equal(PersonaTextFileOutcome.DestinationExists,
            (await service.ExportTextAsync(output, "replacement")).Outcome);
        Assert.Equal(imported.Text, await File.ReadAllTextAsync(output));

        await File.WriteAllBytesAsync(source, [0xC3, 0x28]);
        Assert.Equal(PersonaTextFileOutcome.Invalid, (await service.ImportTextAsync(source)).Outcome);
        await File.WriteAllBytesAsync(source, new byte[PersonaProfile.MaximumTextUtf8Bytes + 4]);
        Assert.Equal(PersonaTextFileOutcome.Invalid, (await service.ImportTextAsync(source)).Outcome);
        Assert.Equal(PersonaTextFileOutcome.Unavailable,
            (await service.ImportTextAsync(Path.Combine(directory, "missing.txt"))).Outcome);

        var canceledOutput = Path.Combine(directory, "canceled.txt");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExportTextAsync(canceledOutput, new string('x', PersonaProfile.MaximumTextCharacters), canceled.Token));
        Assert.False(File.Exists(canceledOutput));
        Assert.Empty(Directory.GetFiles(directory, ".canceled.txt.*.tmp"));
    }

    [Fact]
    public async Task RecoveryRestoresVersionThreePersonasAndOlderSnapshotsPreserveCurrentPersonas()
    {
        var original = CompanionSettings.Begin(null);
        var originalPersona = original.Companion!.ActivePersona;
        original = original with
        {
            Companion = original.Companion.Update(originalPersona.Id, "Original", "Original persona",
                originalPersona.Styles with { Helpful = 60, Silly = 40 })
        };
        Assert.True((await Store.SaveAsync(original, null)).Saved);
        var versionThreeBackup = Path.Combine(directory, "v3.martlet-config");
        await Store.CreateConfigurationSnapshotAsync(versionThreeBackup);

        var currentLoad = await Store.LoadAsync();
        var currentPersona = currentLoad.Settings!.Companion!.ActivePersona;
        var changed = currentLoad.Settings with
        {
            Companion = currentLoad.Settings.Companion.Update(currentPersona.Id, "Changed", "Changed persona",
                currentPersona.Styles with { Helpful = 20, Sarcastic = 80 })
        };
        Assert.True((await Store.SaveAsync(changed, currentLoad.Revision)).Saved);
        var restoreV3 = await Store.PreviewConfigurationRestoreAsync(versionThreeBackup);
        await Store.RestoreConfigurationAsync(restoreV3,
            restoreV3.Approve(restoreV3.SnapshotDigest, restoreV3.Destination, restoreV3.ExpectedRevision));
        Assert.Equal("Original persona", (await Store.LoadAsync()).Settings!.Companion!.ActivePersona.Text);

        var v3 = (await Store.LoadAsync()).Settings!;
        var versionTwo = v3 with
        {
            SchemaVersion = 2,
            Setup = v3.Setup!.DowngradeOpenAiForHistoricalSettings(),
            Companion = null,
            Memory = null
        };
        var versionTwoBytes = ContractJson.Write(versionTwo);
        var versionTwoBackup = Path.Combine(directory, "v2.martlet-config");
        await File.WriteAllBytesAsync(versionTwoBackup, ConfigurationSnapshot.Create(versionTwoBytes));
        var retained = v3 with
        {
            Companion = v3.Companion!.Update(v3.Companion.ActivePersonaId, "Retained", "Keep this current persona.",
                v3.Companion.ActivePersona.Styles)
        };
        var retainedSave = await Store.SaveAsync(retained, (await Store.LoadAsync()).Revision);
        Assert.True(retainedSave.Saved);
        var restoreV2 = await Store.PreviewConfigurationRestoreAsync(versionTwoBackup);
        await Store.RestoreConfigurationAsync(restoreV2,
            restoreV2.Approve(restoreV2.SnapshotDigest, restoreV2.Destination, restoreV2.ExpectedRevision));
        Assert.Equal("Keep this current persona.", (await Store.LoadAsync()).Settings!.Companion!.ActivePersona.Text);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
