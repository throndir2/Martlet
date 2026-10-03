using System.Text;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResponseStyleWeights : IContract
{
    public required int Helpful { get; init; }
    public required int Sarcastic { get; init; }
    public required int Silly { get; init; }
    public required int Distracted { get; init; }
    public required int PlayfulTeasing { get; init; }

    public static ResponseStyleWeights HelpfulOnly() => new()
    {
        Helpful = 100,
        Sarcastic = 0,
        Silly = 0,
        Distracted = 0,
        PlayfulTeasing = 0
    };

    public void Validate()
    {
        var values = new[] { Helpful, Sarcastic, Silly, Distracted, PlayfulTeasing };
        ContractRules.Require(values.All(value => value is >= 0 and <= 100),
            "Each response-style weight must be from 0 through 100.");
        ContractRules.Require(values.Any(value => value > 0),
            "At least one response-style weight must be greater than zero.");
    }
}

public enum ResponseStyle { Helpful, Sarcastic, Silly, Distracted, PlayfulTeasing }

public static class ResponseStyleSelector
{
    public static ResponseStyle Select(ResponseStyleWeights weights, Func<int, int> next)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(next);
        weights.Validate();
        var weighted = new[]
        {
            (ResponseStyle.Helpful, weights.Helpful),
            (ResponseStyle.Sarcastic, weights.Sarcastic),
            (ResponseStyle.Silly, weights.Silly),
            (ResponseStyle.Distracted, weights.Distracted),
            (ResponseStyle.PlayfulTeasing, weights.PlayfulTeasing)
        };
        var positive = weighted.Where(item => item.Item2 > 0).ToArray();
        if (positive.Length == 1)
            return positive[0].Item1;
        var total = positive.Sum(item => item.Item2);
        var selected = next(total);
        ContractRules.Require(selected >= 0 && selected < total, "The response-style selector returned an invalid sample.");
        foreach (var (style, weight) in positive)
        {
            if (selected < weight)
                return style;
            selected -= weight;
        }
        throw new ContractException(ErrorCode.InvalidContract, "The response-style selector could not select a style.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonaProfile : IContract
{
    public const int MaximumNameCharacters = 64;
    public const int MaximumTextCharacters = 8_192;
    public const int MaximumTextUtf8Bytes = 16_384;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public required Guid Id { get; init; }
    public required Guid ConfigurationRevision { get; init; }
    public required string Name { get; init; }
    public required string Text { get; init; }
    public required ResponseStyleWeights Styles { get; init; }

    public void Validate()
    {
        ContractRules.Require(Id != Guid.Empty && ConfigurationRevision != Guid.Empty,
            "A persona requires nonempty identity and configuration revisions.");
        ContractRules.Require(Name is { Length: > 0 and <= MaximumNameCharacters } &&
            Name == Name.Trim() && !Name.Any(char.IsControl),
            "Persona names must be 1-64 visible characters without leading or trailing whitespace.");
        ValidateText(Text);
        ContractRules.Require(Styles is not null, "Response-style weights are required.");
        Styles!.Validate();
    }

    public static void ValidateText(string text)
    {
        ContractRules.Text(text, MaximumTextCharacters);
        try
        {
            ContractRules.Require(StrictUtf8.GetByteCount(text) <= MaximumTextUtf8Bytes,
                "Persona text exceeds the 16 KiB UTF-8 limit.");
        }
        catch (EncoderFallbackException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "Persona text contains invalid Unicode.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompanionSettings : IContract
{
    public const int MaximumPersonas = 16;
    public const int MaximumAggregateTextCharacters = 16_384;
    public const int MaximumAggregateTextUtf8Bytes = 32_768;

    public required int SchemaVersion { get; init; }
    public required Guid ActivePersonaId { get; init; }
    public required IReadOnlyList<PersonaProfile> Personas { get; init; }

    [JsonIgnore]
    public PersonaProfile ActivePersona =>
        Personas.Single(persona => persona.Id == ActivePersonaId);

    public static CompanionSettings Create()
    {
        var persona = new PersonaProfile
        {
            Id = Guid.NewGuid(),
            ConfigurationRevision = Guid.NewGuid(),
            Name = "Martlet",
            Text = "Be a helpful conversational companion.",
            Styles = ResponseStyleWeights.HelpfulOnly()
        };
        return new() { SchemaVersion = 1, ActivePersonaId = persona.Id, Personas = [persona] };
    }

    public static AppSettings Begin(AppSettings? prior) =>
        AppSettings.UpgradeToCurrent(prior);

    public CompanionSettings Select(Guid id)
    {
        ContractRules.Require(Personas.Any(persona => persona.Id == id),
            "Select a persona from this profile.");
        return this with { ActivePersonaId = id };
    }

    public CompanionSettings Add(string name, PersonaProfile? source = null)
    {
        ContractRules.Require(Personas.Count < MaximumPersonas,
            $"At most {MaximumPersonas} persona profiles are supported.");
        var persona = new PersonaProfile
        {
            Id = Guid.NewGuid(),
            ConfigurationRevision = Guid.NewGuid(),
            Name = name,
            Text = source?.Text ?? "",
            Styles = source?.Styles ?? ResponseStyleWeights.HelpfulOnly()
        };
        persona.Validate();
        var updated = this with
        {
            ActivePersonaId = persona.Id,
            Personas = Personas.Append(persona).ToArray()
        };
        updated.Validate();
        return updated;
    }

    public CompanionSettings Update(Guid id, string name, string text, ResponseStyleWeights styles)
    {
        var prior = Personas.SingleOrDefault(persona => persona.Id == id);
        ContractRules.Require(prior is not null, "Select a persona from this profile.");
        var replacement = prior! with { Name = name, Text = text, Styles = styles };
        replacement.Validate();
        if (replacement.Name == prior.Name && replacement.Text == prior.Text && replacement.Styles == prior.Styles)
            return this;
        replacement = replacement with { ConfigurationRevision = Guid.NewGuid() };
        var updated = this with
        {
            Personas = Personas.Select(persona => persona.Id == id ? replacement : persona).ToArray()
        };
        updated.Validate();
        return updated;
    }

    public CompanionSettings Remove(Guid id)
    {
        ContractRules.Require(Personas.Count > 1, "Keep at least one persona profile.");
        ContractRules.Require(Personas.Any(persona => persona.Id == id),
            "Select a persona from this profile.");
        var personas = Personas.Where(persona => persona.Id != id).ToArray();
        var updated = this with
        {
            Personas = personas,
            ActivePersonaId = ActivePersonaId == id ? personas[0].Id : ActivePersonaId
        };
        updated.Validate();
        return updated;
    }

    public void Validate()
    {
        ContractRules.Require(SchemaVersion == 1, "Unsupported companion settings version.", ErrorCode.UnsupportedVersion);
        ContractRules.Require(Personas is { Count: >= 1 and <= MaximumPersonas },
            $"Companion settings require 1-{MaximumPersonas} persona profiles.");
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var persona in Personas!)
        {
            ContractRules.Require(persona is not null, "A persona profile cannot be null.");
            persona!.Validate();
            ContractRules.Require(ids.Add(persona.Id), "Persona profile identities must be unique.");
            ContractRules.Require(names.Add(persona.Name), "Persona profile names must be unique.");
        }
        ContractRules.Require(Personas.Sum(persona => persona.Text.Length) <= MaximumAggregateTextCharacters,
            "Persona texts exceed the combined 16,384-character settings limit.");
        try
        {
            ContractRules.Require(Personas.Sum(persona => StrictUtf8Length(persona.Text)) <= MaximumAggregateTextUtf8Bytes,
                "Persona texts exceed the combined 32 KiB UTF-8 settings limit.");
        }
        catch (EncoderFallbackException)
        {
            throw new ContractException(ErrorCode.InvalidContract, "Persona text contains invalid Unicode.");
        }
        ContractRules.Require(ids.Contains(ActivePersonaId), "The active persona must identify a saved profile.");
    }

    private static int StrictUtf8Length(string text) =>
        new UTF8Encoding(false, true).GetByteCount(text);
}

public enum PersonaTextFileOutcome { Imported, Exported, Invalid, Unavailable, DestinationExists }

public sealed record PersonaTextFileResult(PersonaTextFileOutcome Outcome, string? Text, string Summary)
{
    public bool Succeeded => Outcome is PersonaTextFileOutcome.Imported or PersonaTextFileOutcome.Exported;
}

public sealed record CharacterCardFileResult(PersonaTextFileOutcome Outcome, CharacterCard? Card, string Summary)
{
    public bool Succeeded => Outcome == PersonaTextFileOutcome.Imported && Card is not null;
}

public interface ICompanionSettingsService
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken token = default);
    Task<SetupSaveResult> SaveAsync(AppSettings settings, string? revision, CancellationToken token = default);
    /// <summary>Writes these personas into the newest saved settings, keeping everything else as it is now (other pages'
    /// choices and whatever sync brought in from your other computers), and tries again when another save lands in between.</summary>
    Task<SetupSaveResult> SaveCompanionAsync(CompanionSettings companion, CancellationToken token = default);
    Task<PersonaTextFileResult> ImportTextAsync(string path, CancellationToken token = default);
    Task<PersonaTextFileResult> ExportTextAsync(string path, string text, CancellationToken token = default);
    /// <summary>Reads a SillyTavern/Chub character card (PNG image, JSON or CHARX). Nothing is saved or sent anywhere.</summary>
    Task<CharacterCardFileResult> ImportCardAsync(string path, CancellationToken token = default);
}

public sealed class CompanionSettingsService(SettingsStore store) : ICompanionSettingsService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task<SettingsLoadResult> LoadAsync(CancellationToken token = default) =>
        store.LoadAsync(token);

    public async Task<SetupSaveResult> SaveAsync(AppSettings settings, string? revision, CancellationToken token = default)
    {
        var save = await store.SaveAsync(settings, revision, token);
        return new(save, settings);
    }

    public async Task<SetupSaveResult> SaveCompanionAsync(CompanionSettings companion, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(companion);
        companion.Validate();
        for (var attempt = 1; ; attempt++)
        {
            var loaded = await store.LoadAsync(token);
            if (loaded.Error is not null)
                return new(new(false, null, loaded.Error), CompanionSettings.Begin(null) with { Companion = companion });
            var merged = CompanionSettings.Begin(loaded.Settings) with { Companion = companion };
            var save = await store.SaveAsync(merged, loaded.Revision, token);
            if (save.Saved || save.Error?.Code != ErrorCode.SettingsConflict || attempt == 3)
                return new(save, merged);
        }
    }

    public async Task<PersonaTextFileResult> ImportTextAsync(string path, CancellationToken token = default)
    {
        try
        {
            path = RequireAbsolutePath(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = new byte[PersonaProfile.MaximumTextUtf8Bytes + 4];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(length), token);
                if (read == 0) break;
                length += read;
            }
            if (length > PersonaProfile.MaximumTextUtf8Bytes + 3 || input.ReadByte() != -1)
                return Invalid("Persona text exceeds the 16 KiB UTF-8 file limit.");
            var offset = length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            var text = StrictUtf8.GetString(bytes, offset, length - offset);
            PersonaProfile.ValidateText(text);
            return new(PersonaTextFileOutcome.Imported, text,
                "Persona text imported into the editor.");
        }
        catch (DecoderFallbackException) { return Invalid("Persona text must be valid UTF-8."); }
        catch (ContractException error) { return Invalid(error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(PersonaTextFileOutcome.Unavailable, null,
                "Persona text could not be read. Check the selected file, access and storage, then try again.");
        }
    }

    public async Task<PersonaTextFileResult> ExportTextAsync(string path, string text, CancellationToken token = default)
    {
        string? temporary = null;
        try
        {
            path = RequireAbsolutePath(path);
            PersonaProfile.ValidateText(text);
            var bytes = StrictUtf8.GetBytes(text);
            temporary = Path.Combine(Path.GetDirectoryName(path)!,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(bytes, token);
                    await output.FlushAsync(token);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, path);
                temporary = null;
            }
            finally
            {
                if (temporary is not null)
                    File.Delete(temporary);
            }
            return new(PersonaTextFileOutcome.Exported, null,
                "Persona text exported. Keep the file private.");
        }
        catch (ContractException error) { return Invalid(error.Message); }
        catch (IOException) when (File.Exists(path))
        {
            return new(PersonaTextFileOutcome.DestinationExists, null,
                "The export destination already exists. Choose a new file; Martlet did not overwrite it.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(PersonaTextFileOutcome.Unavailable, null,
                "Persona text could not be exported. Choose a writable new file and try again.");
        }
    }

    private static PersonaTextFileResult Invalid(string summary) =>
        new(PersonaTextFileOutcome.Invalid, null, summary);

    public Task<CharacterCardFileResult> ImportCardAsync(string path, CancellationToken token = default)
    {
        try
        {
            path = RequireAbsolutePath(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920);
            var card = CharacterCardReader.Read(input, token);
            return Task.FromResult(new CharacterCardFileResult(PersonaTextFileOutcome.Imported, card,
                $"Read \"{card.DisplayName}\"."));
        }
        catch (CharacterCardException error) { return Task.FromResult(InvalidCard(error.Message)); }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or DecoderFallbackException or FormatException)
        {
            return Task.FromResult(InvalidCard(CharacterCardReader.Damaged));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(new CharacterCardFileResult(PersonaTextFileOutcome.Unavailable, null,
                "The character card could not be read. Check the selected file and access, then try again."));
        }
    }

    private static CharacterCardFileResult InvalidCard(string summary) =>
        new(PersonaTextFileOutcome.Invalid, null, summary);

    private static string RequireAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Choose an absolute persona text path.", nameof(path));
        return Path.GetFullPath(path);
    }
}
