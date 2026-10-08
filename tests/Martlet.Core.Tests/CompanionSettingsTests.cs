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
        var unchanged = companion.Update(first.Id, first.Name, first.Text);
        Assert.Same(companion, unchanged);

        var changed = companion.Update(first.Id, "Bird", "Listen before answering.");
        Assert.NotEqual(first.ConfigurationRevision, changed.ActivePersona.ConfigurationRevision);
        var second = changed.Add("Bird copy", changed.ActivePersona);
        Assert.Equal("Bird copy", second.ActivePersona.Name);
        Assert.NotEqual(changed.ActivePersona.Id, second.ActivePersona.Id);
        Assert.NotEqual(changed.ActivePersona.ConfigurationRevision, second.ActivePersona.ConfigurationRevision);
        Assert.Equal(changed.ActivePersona.Text, second.ActivePersona.Text);
        Assert.Equal(changed.ActivePersona.Id, second.Remove(second.ActivePersonaId).ActivePersonaId);

        Assert.Throws<ContractException>(() => changed.Add("bird"));
        Assert.Throws<ContractException>(() => changed.Update(first.Id, " Bird ", first.Text));
        Assert.Throws<ContractException>(() => changed.Update(first.Id, first.Name, new string('x', PersonaProfile.MaximumTextCharacters + 1)));
        Assert.Throws<ContractException>(() => changed.Update(first.Id, first.Name, "\uD800"));
        Assert.Throws<ContractException>(() => changed.Remove(first.Id));
    }

    [Fact]
    public async Task EarlierSavedResponseStylesStillLoadAndAreNotWrittenAgain()
    {
        var settings = CompanionSettings.Begin(null);
        var document = System.Text.Json.Nodes.JsonNode.Parse(ContractJson.Write(settings))!;
        var persona = document["companion"]!["personas"]![0]!.AsObject();
        Assert.False(persona.ContainsKey("styles"));
        // What an older Martlet saved for each persona: the response-style weights of the sliders it had.
        persona["styles"] = new System.Text.Json.Nodes.JsonObject
        {
            ["helpful"] = 40, ["sarcastic"] = 20, ["silly"] = 20, ["distracted"] = 10, ["playful_teasing"] = 10
        };
        var original = Encoding.UTF8.GetBytes(document.ToJsonString());
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Store.FilePath, original);

        var loaded = await Store.LoadAsync();
        Assert.Equal(SettingsLoadState.Loaded, loaded.State);
        Assert.Equal(settings.Companion!.ActivePersona, loaded.Settings!.Companion!.ActivePersona);
        Assert.Equal(ContractJson.Write(settings), ContractJson.Write(loaded.Settings));
        Assert.Equal(original, await File.ReadAllBytesAsync(Store.FilePath));

        // The next save writes the persona without them.
        var renamed = loaded.Settings with
        {
            Companion = loaded.Settings.Companion.Update(loaded.Settings.Companion.ActivePersonaId, "Bird", "Listen before answering.")
        };
        Assert.True((await Store.SaveAsync(renamed, loaded.Revision)).Saved);
        var saved = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllBytesAsync(Store.FilePath))!;
        Assert.False(saved["companion"]!["personas"]![0]!.AsObject().ContainsKey("styles"));
        Assert.Equal("Bird", (await Store.LoadAsync()).Settings!.Companion!.ActivePersona.Name);
    }

    [Fact]
    public async Task MaximumProfileCollectionFitsSettingsAndAggregateTextIsBounded()
    {
        var settings = CompanionSettings.Begin(null);
        var companion = settings.Companion!;
        var first = companion.ActivePersona;
        companion = companion.Update(first.Id, first.Name, new string('a', 1_024));
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
        var companion = settings.Companion.Update(persona.Id, persona.Name, new string('\u00e9', 8192));
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
    public void EarlierSavedCommasStopStillLoadsAndIsNotWrittenAgain()
    {
        var settings = CompanionSettings.Begin(null);
        var persona = settings.Companion!.ActivePersona;
        settings = settings with
        {
            Companion = settings.Companion.Update(persona.Id, persona.Name, persona.Text,
                SpeechBreaks.Default with { QuestionMarks = false })
        };
        var document = System.Text.Json.Nodes.JsonNode.Parse(ContractJson.Write(settings))!;
        var breaks = document["companion"]!["personas"]![0]!["breaks"]!.AsObject();
        Assert.False(breaks.ContainsKey("commas"));
        breaks["commas"] = false;

        var loaded = SettingsJson.Read(Encoding.UTF8.GetBytes(document.ToJsonString()));
        Assert.Equal(SpeechBreaks.Default with { QuestionMarks = false }, loaded.Companion!.ActivePersona.SpokenBreaks);
        Assert.Equal(ContractJson.Write(settings), ContractJson.Write(loaded));
        Assert.DoesNotContain("commas", Encoding.UTF8.GetString(ContractJson.Write(loaded)), StringComparison.Ordinal);

        breaks.Remove("question_marks");
        Assert.True(SettingsJson.Read(Encoding.UTF8.GetBytes(document.ToJsonString())).Companion!.ActivePersona.SpokenBreaks.IsDefault);
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
            Companion = original.Companion.Update(originalPersona.Id, "Original", "Original persona")
        };
        Assert.True((await Store.SaveAsync(original, null)).Saved);
        var versionThreeBackup = Path.Combine(directory, "v3.martlet-config");
        await Store.CreateConfigurationSnapshotAsync(versionThreeBackup);

        var currentLoad = await Store.LoadAsync();
        var currentPersona = currentLoad.Settings!.Companion!.ActivePersona;
        var changed = currentLoad.Settings with
        {
            Companion = currentLoad.Settings.Companion.Update(currentPersona.Id, "Changed", "Changed persona")
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
            Companion = v3.Companion!.Update(v3.Companion.ActivePersonaId, "Retained", "Keep this current persona.")
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
