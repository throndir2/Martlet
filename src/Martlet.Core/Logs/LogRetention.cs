namespace Martlet.Core.Logs;

/// <summary>How long shared log lines are kept (docs/DIAGNOSTICS.md): a week on every host and in every desktop's copy of the
/// other computers' lines. The caps stop a computer whose clock is wrong, or a flood of lines, from growing a log without end:
/// a week at Martlet's usual rate (about 430 lines an hour for a household, up to about 1,000 while it talks a lot) is about
/// 72,000 to 170,000 lines, 20 to 45 MB on disk.</summary>
public static class LogRetention
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    public const int MaximumEntries = 250_000;

    /// <summary>The most lines kept by their estimated size in memory (<see cref="Size"/>), about 128 MB.</summary>
    public const long MaximumBytes = 128_000_000;

    /// <summary>A line's estimated size in memory: two bytes a character of its message plus 240 for the rest.</summary>
    public static long Size(LogRecord line) => line.Message.Length * 2L + 240;
}
