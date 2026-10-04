using System.Globalization;
using System.Text;

namespace Martlet.Core.Installation;

/// <summary>What Martlet was doing when it closed to install its own update (the character showing, always listening), so
/// the restarted Martlet picks it up again instead of the update quietly switching them off: resume.txt in the updates folder,
/// written just before the update helper starts and read once at the next start. A note older than <see cref="MaximumAge"/>
/// (Martlet didn't restart after the install, and you started it yourself later) is ignored.</summary>
public static class AppUpdateResume
{
    public const string FileName = "resume.txt";
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(15);

    /// <summary>Writes the note, or removes an old one when there is nothing to pick up again.</summary>
    public static void Save(string directory, bool character, bool listening, DateTimeOffset now)
    {
        var path = Path.Combine(directory, FileName);
        if (!character && !listening)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(directory);
        File.WriteAllText(path,
            $"{(character ? 1 : 0)} {(listening ? 1 : 0)} {now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Removes the note (an install that didn't start leaves nothing to pick up).</summary>
    public static void Discard(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>The note, once (the file goes): null when there is none, it can't be read or it is older than
    /// <see cref="MaximumAge"/>.</summary>
    public static (bool Character, bool Listening)? Take(string directory, DateTimeOffset now)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        var parts = File.ReadAllText(path).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        File.Delete(path);
        if (parts is not [var character, var listening, var at] ||
            !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var written))
            return null;
        var age = now - written;
        if (age < TimeSpan.Zero || age > MaximumAge) return null;
        return (character == "1", listening == "1");
    }
}
