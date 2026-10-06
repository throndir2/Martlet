namespace Martlet.Gateway;

/// <summary>
/// The rule that a host becomes a public endpoint only once sign-in is set up on it (docs/NETWORK.md, "Sign-in comes
/// first"). Sign-in stays optional: a host with no outside addresses (and typed codes kept at home) needs none. Saving
/// outside addresses or allowing typed codes from outside is refused until sign-in has a usable method; if sign-in loses
/// its last usable method later, the addresses are kept and requests from outside home are refused (<see cref="Paused"/>)
/// until it is set up again or the addresses are removed.
/// </summary>
internal static class GatewayOutsideAccess
{
    /// <summary>The failure code (and audit code) for a request from outside home while outside access is paused.</summary>
    internal const string Paused = "outside.paused";

    /// <summary>Why sign-in isn't usable on a host with this signin.json (null: usable). Sign-in owns the rule
    /// (<see cref="GatewaySignInSettings"/>); this only adapts its read.</summary>
    internal static string? SignInBlockedReason(GatewaySignInDocument? document) => GatewaySignInSettings.BlockedReason(document);

    /// <summary>Why sign-in isn't usable on the running host (null: usable).</summary>
    internal static string? SignInBlockedReason(GatewaySignInService signIn) => signIn.BlockedReason();

    /// <summary>A blocked reason in words, for host output and logs.</summary>
    internal static string Describe(string? reason) => reason switch
    {
        null => "sign-in is set up",
        "signin.no_allowed_identity" => "sign-in has no allowed identity (add one, or set up an owner account with an authenticator)",
        _ => "sign-in isn't set up on this host (an owner account with an authenticator, or a provider with an allowed identity)"
    };

    /// <summary>Whether going from the current outside choices to the next ones makes the host more reachable from outside:
    /// a new outside address, or typed codes newly allowed from outside. Removing addresses, keeping them and treating every
    /// connection as outside never do.</summary>
    internal static bool Expands(IReadOnlyCollection<string> currentOutside, bool currentCodes, IReadOnlyCollection<string> nextOutside, bool nextCodes) =>
        nextOutside.Except(currentOutside, StringComparer.Ordinal).Any() || nextCodes && !currentCodes;
}
