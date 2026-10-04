namespace Martlet.Core.Nodes;

/// <summary>The shell that runs martlet-host on a native Ubuntu host over SSH. The engine runs from the Martlet source in
/// ~/Martlet: a git checkout the host clones and updates itself, or, on a computer without internet access, a copy Martlet
/// unpacked there from files this PC sent (<see cref="HostSupply"/>; the engine then gets MARTLET_SUPPLY and builds from
/// those files). The text is POSIX sh on one line without double quotes or percent signs, so it also fits the Windows
/// command line of Martlet's ssh.exe fallback. Whatever can't be had stops at once with a "Stopped: ..." line and exit 1;
/// it never goes on to run an engine that isn't there.</summary>
public static class HostCheckout
{
    public const string Repository = "https://github.com/throndir2/Martlet.git";
    public const string Engine = "~/Martlet/deploy/host/martlet-host";

    /// <summary>Prints internet=yes, internet=no or internet=unknown: whether this computer opens a connection to GitHub
    /// over HTTPS (name lookup included) within 8 seconds.</summary>
    public const string InternetProbe =
        "if command -v bash >/dev/null 2>&1 && command -v timeout >/dev/null 2>&1; then " +
        "timeout 8 bash -c 'exec 3<>/dev/tcp/github.com/443' >/dev/null 2>&1 && echo internet=yes || echo internet=no; " +
        "else echo internet=unknown; fi";

    /// <summary>Makes sure git and a git checkout in ~/Martlet exist. A copy that came from this PC (or anything else that
    /// isn't a checkout) moves to ~/.cache/martlet/source.previous.</summary>
    internal const string Clone =
        "command -v git >/dev/null 2>&1 || { sudo apt-get update -q && sudo apt-get install -y git; } </dev/null || true; " +
        "command -v git >/dev/null 2>&1 || { echo 'Stopped: git is not installed here and could not be installed (sudo apt-get install git). " +
        "If this computer has no internet access, set it up from Martlet, which sends what it needs from your PC.' >&2; exit 1; }; " +
        "if [ ! -d ~/Martlet/.git ]; then mkdir -p ~/.cache/martlet && rm -rf ~/.cache/martlet/clone && " +
        "{ git clone -q --depth 1 " + Repository + " ~/.cache/martlet/clone </dev/null || " +
        "{ echo 'Stopped: could not download Martlet from " + Repository + ".' >&2; exit 1; }; } && " +
        "if [ -e ~/Martlet ]; then rm -rf ~/.cache/martlet/source.previous && mv ~/Martlet ~/.cache/martlet/source.previous; fi && " +
        "mv ~/.cache/martlet/clone ~/Martlet; fi; ";

    internal const string Present =
        "test -x " + Engine + " || { echo 'Stopped: there is no Martlet engine in ~/Martlet (deploy/host/martlet-host). " +
        "Set this computer up again from Martlet.' >&2; exit 1; }; ";

    /// <summary>The command for one engine run.</summary>
    /// <param name="arguments">martlet-host's arguments, for example "--yes setup".</param>
    /// <param name="environment">Variables for the engine, each followed by a space (for example "MARTLET_HOST_ADDRESS=192.168.1.20 ").</param>
    /// <param name="version">This desktop's Martlet version: an update checks out its release tag (falling back to main).</param>
    /// <param name="refresh">Setup and update first make ~/Martlet a current checkout; other commands use the engine that
    /// is there and only clone when there is none.</param>
    /// <param name="update">Check out <paramref name="version"/>'s tag instead of pulling.</param>
    /// <param name="supplied">The host has no internet access and this PC sent its files: run the unpacked copy with
    /// MARTLET_SUPPLY and touch neither git nor the network.</param>
    /// <param name="closeStdin">Keep stdin for the engine (Martlet's SSH runner passes answers there).</param>
    public static string Command(string arguments, string environment, string version, bool refresh, bool update, bool supplied,
        bool closeStdin)
    {
        if (supplied) return Present + $"MARTLET_SUPPLY=$HOME/{HostSupply.RemoteDirectory} {environment}{Engine} {arguments}";
        var quiet = closeStdin ? " </dev/null" : "";
        var checkout = refresh ? Clone : $"if [ ! -x {Engine} ]; then {Clone}fi; ";
        var pull = update
            ? $"{{ git -C ~/Martlet fetch -q --depth 1 origin tag v{version}{quiet} && " +
              $"git -C ~/Martlet -c advice.detachedHead=false checkout -q v{version}; }} || " +
              $"{{ git -C ~/Martlet checkout -q main && git -C ~/Martlet pull --ff-only -q{quiet}; }} || true; "
            : $"{{ [ -d ~/Martlet/.git ] && git -C ~/Martlet pull --ff-only -q{quiet}; }} || true; ";
        return checkout + pull + Present + $"{environment}{Engine} {arguments}";
    }
}
