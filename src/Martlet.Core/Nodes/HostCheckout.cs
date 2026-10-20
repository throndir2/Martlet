namespace Martlet.Core.Nodes;

/// <summary>The shell that runs martlet-host on a native Linux host over SSH, and the parts that find Docker and build the
/// host image on a Docker host over SSH. The engine runs from the Martlet source in
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

    /// <summary>Puts a Docker that only a login shell finds on PATH. A NAS (TerraMaster TOS, Synology DSM, QNAP) adds its
    /// Docker to PATH in /etc/profile, which the plain shell of an SSH command doesn't read.</summary>
    public const string DockerPath =
        "command -v docker >/dev/null 2>&1 || PATH=$PATH:$(sh -lc 'echo $PATH' </dev/null 2>/dev/null | tail -n 1); ";

    /// <summary>Prints which NAS this computer is (TerraMaster, Synology, QNAP, Unraid or TrueNAS), or nothing. A NAS can
    /// name a plain distribution in /etc/os-release and have systemd (TerraMaster TOS 6 says Ubuntu 22.04), so each is
    /// found by a file or command that only its system has. Martlet sets up a NAS only in Docker.</summary>
    public const string NasProbe =
        "if [ -d /etc/tos ] || [ -d /usr/tos ]; then echo TerraMaster; " +
        "elif [ -f /etc.defaults/VERSION ] || [ -d /usr/syno ]; then echo Synology; " +
        "elif [ -f /etc/config/uLinux.conf ]; then echo QNAP; " +
        "elif [ -f /etc/unraid-version ]; then echo Unraid; " +
        "elif command -v midclt >/dev/null 2>&1; then echo TrueNAS; fi";

    /// <summary>Sets $D to the docker command of a Docker host: docker, or sudo with Docker's full path (sudo's own PATH
    /// may not have a NAS's Docker) when this account can't use Docker directly. <paramref name="batch"/>: sudo never
    /// asks for a password (-n).</summary>
    public static string Docker(bool batch) =>
        DockerPath + $"D=docker; docker info >/dev/null 2>&1 || D='{(batch ? "sudo -n" : "sudo")} '$(command -v docker || echo docker); ";

    /// <summary>Builds <paramref name="image"/> on a Docker host when it isn't there yet, from this desktop's release tag
    /// (falling back to main): from the git repository when the host has git, otherwise (a NAS rarely has git) from GitHub's
    /// source archive, unpacked into a temporary folder that is removed afterwards. <paramref name="docker"/> is the docker
    /// command (<c>$D</c>; see <see cref="Docker"/>).</summary>
    public static string DockerImage(string docker, string image, string version)
    {
        var build = $"{docker} build -t {image} -f deploy/host/Dockerfile {Repository}#";
        var archive = Repository[..^".git".Length] + "/archive/refs/";
        return $"{docker} image inspect {image} >/dev/null 2>&1 || " +
            $"{{ command -v git >/dev/null 2>&1 && {{ {build}v{version} || {build}main; }}; }} || " +
            $"{{ echo 'Building the host from the source archive of Martlet {version}.'; S=$(mktemp -d) && " +
            $"{{ {{ curl -fsSL {archive}tags/v{version}.tar.gz || curl -fsSL {archive}heads/main.tar.gz; }} | " +
            $"tar -xzf - -C $S --strip-components=1 && {docker} build -t {image} -f $S/deploy/host/Dockerfile $S; " +
            "R=$?; rm -rf $S; [ $R = 0 ]; }; }";
    }

    /// <summary>Makes sure git and a git checkout in ~/Martlet exist. A copy that came from this PC (or anything else that
    /// isn't a checkout) moves to ~/.cache/martlet/source.previous.</summary>
    internal const string Clone =
        "command -v git >/dev/null 2>&1 || { if command -v apt-get >/dev/null 2>&1; then sudo apt-get update -q && sudo apt-get install -y git; " +
        "elif command -v dnf >/dev/null 2>&1; then sudo dnf install -y git; elif command -v yum >/dev/null 2>&1; then sudo yum install -y git; " +
        "elif command -v zypper >/dev/null 2>&1; then sudo zypper --non-interactive install git; " +
        "elif command -v pacman >/dev/null 2>&1; then sudo pacman -Sy --noconfirm --needed git; fi; } </dev/null || true; " +
        "command -v git >/dev/null 2>&1 || { echo 'Stopped: git is not installed here and the system package manager could not install it; " +
        "without internet access, set this computer up from Martlet, which sends what it needs from your PC.' >&2; exit 1; }; " +
        "if [ ! -d ~/Martlet/.git ]; then mkdir -p ~/.cache/martlet && rm -rf ~/.cache/martlet/clone && " +
        "{ git clone -q --depth 1 " + Repository + " ~/.cache/martlet/clone </dev/null || " +
        "{ echo 'Stopped: could not download Martlet from " + Repository + ".' >&2; exit 1; }; } && " +
        "if [ -e ~/Martlet ]; then rm -rf ~/.cache/martlet/source.previous && mv ~/Martlet ~/.cache/martlet/source.previous; fi && " +
        "mv ~/.cache/martlet/clone ~/Martlet; fi; ";

    internal const string Present =
        "test -x " + Engine + " || { echo 'Stopped: there is no Martlet engine in ~/Martlet (deploy/host/martlet-host), so " +
        "set this computer up again from Martlet.' >&2; exit 1; }; ";

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
    /// <remarks>Several engine runs may start on one computer at once (installs side by side, from one or several desktops):
    /// they take turns at the checkout (flock on ~/.cache/martlet/checkout.lock, up to ten minutes), so two git commands
    /// never collide in ~/Martlet, and then run their engines side by side.</remarks>
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
        return CheckoutTurn + checkout + pull + CheckoutTurnEnd + Present + $"{environment}{Engine} {arguments}";
    }

    /// <summary>The checkout's turn: a subshell that holds the lock while it clones or pulls (without flock, or after ten
    /// minutes, it goes ahead anyway); a stop inside it (exit 1) stops the whole command.</summary>
    internal const string CheckoutTurn = "mkdir -p ~/.cache/martlet && ( { command -v flock >/dev/null 2>&1 && flock -w 600 9; }; ";

    internal const string CheckoutTurnEnd = ") 9>>$HOME/.cache/martlet/checkout.lock || exit 1; ";
}
