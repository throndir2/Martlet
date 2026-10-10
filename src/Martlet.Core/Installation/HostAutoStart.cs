using System.Text.RegularExpressions;

namespace Martlet.Core.Installation;

/// <summary>What Martlet does by itself when this PC becomes a host PC, or starts as one: nothing, or start Docker Desktop
/// first, then bring the host service's roles up and warm (martlet-host warm).</summary>
public enum HostAutoStartStep { Skip, StartDockerThenWarm, Warm }

/// <summary>The decision and the sentence the host dashboard shows for it.</summary>
public sealed record HostAutoStartPlan(HostAutoStartStep Step, string Reason);

/// <summary>Decides whether a host PC brings its roles up by itself. It does only where nothing has to be installed or
/// approved: Docker Desktop is installed, Windows is ready for it, the host service is set up on this PC and runs roles
/// (as the last read of it found, or, before Martlet has read it, because this PC is paired with its own host service).</summary>
public static class HostAutoStart
{
    public static HostAutoStartPlan Decide(LocalHostServiceStage stage, IReadOnlyList<string>? roles, IReadOnlyList<string>? remembered,
        bool pairedWithOwnHost, bool windowsBlocked)
    {
        var known = roles ?? remembered;
        var hasRoles = known is { Count: > 0 } || known is null && pairedWithOwnHost;
        var names = known is { Count: > 0 } ? $" ({string.Join(", ", known)})" : "";
        return stage switch
        {
            LocalHostServiceStage.DockerMissing => new(HostAutoStartStep.Skip,
                "Docker Desktop isn't installed, so nothing started by itself. The Docker Desktop step installs it."),
            LocalHostServiceStage.NotSetUp => new(HostAutoStartStep.Skip,
                "The host service isn't set up on this PC yet, so there is nothing to start. Set up host service does it."),
            _ when !hasRoles => new(HostAutoStartStep.Skip, known is null
                ? "Martlet hasn't seen roles in this PC's host service yet, so it didn't start Docker Desktop by itself."
                : "This PC's host service runs no roles yet, so there is nothing to warm up. Add roles below."),
            LocalHostServiceStage.DockerNotRunning when windowsBlocked => new(HostAutoStartStep.Skip,
                "Windows isn't ready for Docker Desktop, so Martlet didn't start it by itself. The Docker Desktop step shows what to do."),
            LocalHostServiceStage.DockerNotRunning => new(HostAutoStartStep.StartDockerThenWarm,
                $"Starting Docker Desktop, then this host's roles{names}, and loading their models."),
            _ => new(HostAutoStartStep.Warm, $"Starting this host's roles{names} and loading their models.")
        };
    }
}

/// <summary>The roles the last read of this PC's own host service found (this-pc-host-roles.txt in the PC folder,
/// <see cref="PcFolder"/>), so a host PC knows what to start before Docker Desktop runs and the host service can be read. The
/// file can name the Windows user (by SID) whose Docker Desktop Martlet read it in: Docker Desktop keeps its containers for
/// the Windows user who runs it, so another Windows user of this PC doesn't start or forget them.</summary>
public static partial class ThisPcHostRoles
{
    public const string FileName = "this-pc-host-roles.txt";
    private const string UserLine = "# windows user: ";

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,31}\z")]
    private static partial Regex RolePattern();

    [GeneratedRegex(@"\AS-1-[0-9]{1,14}(-[0-9]{1,10}){1,15}\z")]
    private static partial Regex SidPattern();

    /// <summary>The Windows user (SID) whose Docker Desktop Martlet last read the roles in, or null when the file names none.</summary>
    public static string? WindowsUser(string directory)
    {
        try
        {
            var first = File.ReadLines(Path.Combine(directory, FileName)).FirstOrDefault();
            var user = first is not null && first.StartsWith(UserLine, StringComparison.Ordinal) ? first[UserLine.Length..].Trim() : null;
            return user is not null && SidPattern().IsMatch(user) ? user : null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The remembered roles, or null when Martlet never read this PC's host service (or forgot it).</summary>
    public static IReadOnlyList<string>? Load(string directory)
    {
        try
        {
            return File.ReadAllLines(Path.Combine(directory, FileName)).Select(line => line.Trim())
                .Where(line => RolePattern().IsMatch(line)).ToArray();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Saves the roles, and the Windows user (SID) whose Docker Desktop runs them when given.</summary>
    public static void Save(string directory, IReadOnlyList<string> roles, string? windowsUser = null)
    {
        var valid = roles.Where(role => RolePattern().IsMatch(role)).Order(StringComparer.Ordinal).ToArray();
        var user = windowsUser is not null && SidPattern().IsMatch(windowsUser) ? windowsUser : null;
        if (Load(directory) is { } now && now.SequenceEqual(valid, StringComparer.Ordinal) && WindowsUser(directory) == user) return;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"this-pc-host-roles.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(temporary, user is null ? valid : [UserLine + user, .. valid]);
            File.Move(temporary, Path.Combine(directory, FileName), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Forget(string directory) => File.Delete(Path.Combine(directory, FileName));
}
