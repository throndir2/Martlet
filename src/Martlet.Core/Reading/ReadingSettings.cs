using System.Text.Json;
using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Reading;

/// <summary>Where Martlet reads the text on your screen while it watches: Windows' own OCR on this PC (<see cref="ThisPc"/>, the
/// default: on the processor, nothing leaves the PC), Martlet's <c>ocr</c> host role (RapidOCR or PP-OCRv5) on this PC or another of your
/// computers (<see cref="Host"/>), or not at all (<see cref="Off"/>).</summary>
public enum ReadingPlace { ThisPc, Host, Off }

/// <summary>Companion › Reading on this PC (reading.json in the data folder; never shared, since which computer reads depends on
/// the computer you talk to). A missing or unreadable file means the default: Windows OCR on this PC.</summary>
public sealed record ReadingSettings
{
    public const string FileName = "reading.json";
    private const int MaxFileBytes = 4 * 1024;

    [JsonConverter(typeof(JsonStringEnumConverter<ReadingPlace>))]
    public ReadingPlace Place { get; init; }
    /// <summary>The paired computer whose Reading role reads (Place Host); null uses the first one that offers it.</summary>
    public string? HostId { get; init; }
    public DateTimeOffset? ChosenAt { get; init; }

    [JsonIgnore] public bool On => Place != ReadingPlace.Off;

    /// <summary>Where the text is read, in words.</summary>
    public string Describe() => Place switch
    {
        ReadingPlace.ThisPc => "Windows OCR on this PC",
        ReadingPlace.Host => HostId is null ? "Martlet's Reading role" : $"{HostId}'s Reading role",
        _ => "nowhere (off)"
    };

    /// <summary>Throws when the saved choice isn't usable as it is.</summary>
    public void Validate()
    {
        ContractRules.Defined(Place);
        ContractRules.Require(HostId is null or { Length: > 0 and <= 128 } && HostId?.Any(char.IsControl) != true,
            "The computer for reading is invalid.");
        ContractRules.Require(HostId is null || Place == ReadingPlace.Host, "Only the Reading role keeps a computer.");
    }

    public static ReadingSettings Load(string? directory) => Read(directory).Settings;

    /// <summary>The saved choice and the file's state: none, loaded or unreadable (both of which read as the default).</summary>
    public static (ReadingSettings Settings, string State) Read(string? directory)
    {
        if (directory is null) return (new(), "none");
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return (new(), "none");
            if (new FileInfo(path).Length > MaxFileBytes) return (new(), "unreadable");
            var loaded = JsonSerializer.Deserialize<ReadingSettings>(File.ReadAllText(path));
            if (loaded is null) return (new(), "unreadable");
            loaded.Validate();
            return (loaded, "loaded");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or
            ContractException or ArgumentException)
        {
            return (new(), "unreadable");
        }
    }

    /// <summary>Saves reading.json atomically; false when the data folder can't be written.</summary>
    public bool Save(string? directory)
    {
        if (directory is null) return false;
        Validate();
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = Path.Combine(directory, $"reading.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
