using System.Diagnostics.CodeAnalysis;

namespace Martlet.Core.Sync;

/// <summary>The ID of a memory space (docs/ACCOUNTS.md, "Memory spaces"): <c>household</c>, <c>account-&lt;32 hex&gt;</c> or
/// <c>character-&lt;32 hex&gt;</c>, with the account or character ID as 32 lowercase hex digits (<see cref="Guid"/> format "N").
/// Hosts keep one <see cref="SharedMemories"/> document per space.</summary>
public static class MemorySpaceId
{
    public const string Household = "household";
    public const string AccountPrefix = "account-";
    public const string CharacterPrefix = "character-";
    public const int MaximumLength = 42;

    public static string Account(Guid accountId) => AccountPrefix + accountId.ToString("N");

    public static string Character(Guid characterId) => CharacterPrefix + characterId.ToString("N");

    /// <summary>Whether <paramref name="value"/> matches <c>^(household|account-[0-9a-f]{32}|character-[0-9a-f]{32})$</c>.</summary>
    public static bool IsValid([NotNullWhen(true)] string? value) =>
        value == Household || Hex(value, AccountPrefix) || Hex(value, CharacterPrefix);

    private static bool Hex(string? value, string prefix) =>
        value is not null && value.Length == prefix.Length + 32 && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}
