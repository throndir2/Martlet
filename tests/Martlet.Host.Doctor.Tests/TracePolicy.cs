using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Martlet.Host.Doctor.Tests;

// Build-only verifier: no host operations. Unknown strace syntax/syscalls deliberately fail closed.
internal static class TracePolicy
{
    internal sealed record Summary(int Calls, int ProcessIds, int ExecAttempts);
    private sealed record Call(int Pid, string Name, string[] Args, string Result);
    private static readonly Regex Prefix = Rx(@"^(?<pid>[1-9][0-9]*)\s+(?<body>.+)$");
    private static readonly Regex Complete = Rx(@"^(?<name>[a-z][a-z0-9_]*)\((?<args>.*)\)\s+=\s+(?<result>.+)$");
    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Dictionary<string, int> ReadOnly = new(StringComparer.Ordinal)
    {
        ["read"] = 3, ["readv"] = 3, ["pread64"] = 4, ["preadv"] = 4, ["preadv2"] = 5, ["close"] = 1,
        ["fstat"] = 2, ["stat"] = 2, ["lstat"] = 2, ["newfstatat"] = 4, ["statx"] = 5,
        ["statfs"] = 2, ["fstatfs"] = 2, ["lseek"] = 3, ["readlink"] = 3, ["readlinkat"] = 4,
        ["access"] = 2, ["faccessat"] = 3, ["faccessat2"] = 4, ["getcwd"] = 2, ["getdents"] = 3, ["getdents64"] = 3,
        ["uname"] = 1, ["sysinfo"] = 1, ["getpid"] = 0, ["gettid"] = 0, ["getppid"] = 0,
        ["getuid"] = 0, ["geteuid"] = 0, ["getgid"] = 0, ["getegid"] = 0, ["getresuid"] = 3, ["getresgid"] = 3,
        ["getgroups"] = 2, ["getrlimit"] = 2, ["getrusage"] = 2, ["times"] = 1, ["time"] = 1,
        ["gettimeofday"] = 2, ["clock_gettime"] = 2, ["clock_getres"] = 2, ["sched_getaffinity"] = 3,
        ["getrandom"] = 3, ["sched_getparam"] = 2, ["sched_getscheduler"] = 1
    };
    private static readonly Dictionary<string, int> ProcessLocal = new(StringComparer.Ordinal)
    {
        ["brk"] = 1, ["munmap"] = 2, ["mprotect"] = 3, ["mincore"] = 3,
        ["rt_sigaction"] = 4, ["rt_sigprocmask"] = 4, ["rt_sigreturn"] = 1, ["sigaltstack"] = 2,
        ["set_tid_address"] = 1, ["set_robust_list"] = 2, ["rseq"] = 4, ["sched_yield"] = 0,
        ["clock_nanosleep"] = 4, ["nanosleep"] = 2, ["restart_syscall"] = 1,
        ["pipe"] = 1, ["pipe2"] = 2, ["eventfd"] = 1, ["eventfd2"] = 2, ["epoll_create"] = 1,
        ["epoll_create1"] = 1, ["epoll_ctl"] = 4, ["epoll_wait"] = 4, ["epoll_pwait"] = 6, ["epoll_pwait2"] = 6,
        ["poll"] = 3, ["ppoll"] = 5, ["select"] = 5, ["pselect6"] = 6,
        ["dup"] = 1, ["dup2"] = 2, ["dup3"] = 3, ["chdir"] = 1, ["fchdir"] = 1, ["umask"] = 1,
        ["exit"] = 1, ["exit_group"] = 1
    };

    public static Summary Validate(IEnumerable<string> lines, string binary, IReadOnlyList<string> arguments, bool packages)
    {
        var calls = new List<Call>();
        var pending = new Dictionary<int, (string Name, string Text)>();
        var events = new List<(int Pid, string Text)>();
        var lineCount = 0;
        foreach (var line in lines)
        {
            if (++lineCount > 20000 || line.Length > 16384) Fail("trace-size");
            var prefix = Prefix.Match(line);
            var pid = 0;
            if (!prefix.Success || !int.TryParse(prefix.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out pid))
                Fail("record-prefix");
            var body = prefix.Groups["body"].Value;
            if (body.StartsWith("--- ", StringComparison.Ordinal) || body.StartsWith("+++ ", StringComparison.Ordinal))
            {
                events.Add((pid, body));
                continue;
            }
            var resumed = Rx(@"^<\.\.\. (?<name>[a-z][a-z0-9_]*) resumed>(?<suffix>.*)$").Match(body);
            if (resumed.Success)
            {
                if (!pending.Remove(pid, out var saved) || saved.Name != resumed.Groups["name"].Value) Fail("unmatched-resume");
                body = saved.Text + resumed.Groups["suffix"].Value;
            }
            else if (pending.ContainsKey(pid)) Fail("missing-resume");
            if (body.EndsWith(" <unfinished ...>", StringComparison.Ordinal))
            {
                body = body[..^17];
                var name = Rx(@"^(?<name>[a-z][a-z0-9_]*)\(").Match(body);
                if (!name.Success || !pending.TryAdd(pid, (name.Groups["name"].Value, body))) Fail("invalid-unfinished");
                continue;
            }
            var call = Complete.Match(body);
            if (!call.Success) Fail("call-syntax");
            if (call.Groups["name"].Value.Length > 64) Fail("syscall-name");
            var result = call.Groups["result"].Value;
            if (!Rx(@"^(?:-?[0-9]+|0x[0-9a-f]+)(?:<[^<>]*>)?(?: .*)?$|^\?(?: ERESTART[A-Z]+ .*)?$").IsMatch(result))
                Fail("result-syntax");
            var syscall = call.Groups["name"].Value;
            try { calls.Add(new(pid, syscall, Split(call.Groups["args"].Value), result)); }
            catch (InvalidDataException ex) { throw new InvalidDataException(ex.Message + ":" + syscall); }
        }
        if (pending.Count != 0 || calls.Count == 0) Fail("incomplete-trace");
        if (calls[0].Name != "execve") Fail("missing-root-exec");
        var root = calls[0].Pid;
        var parents = new Dictionary<int, int>();
        foreach (var call in calls.Where(c => c.Name is "clone" or "clone3" or "fork" or "vfork"))
        {
            if (!PositiveResult(call.Result, out var child)) continue;
            if (child == root || child == call.Pid || !parents.TryAdd(child, call.Pid) || parents.Count > 128) Fail("process-tree");
        }
        bool Owned(int pid)
        {
            var visited = new HashSet<int>();
            while (pid != root)
            {
                if (!visited.Add(pid) || !parents.TryGetValue(pid, out pid)) return false;
            }
            return true;
        }
        foreach (var call in calls) if (!Owned(call.Pid)) Fail("unowned-pid");
        var pipes = calls.Where(c => c.Name is "pipe" or "pipe2" && c.Result == "0")
            .SelectMany(c => Regex.Matches(string.Join(',', c.Args), @"pipe:\[([0-9]+)\]").Select(m => m.Groups[1].Value)).ToHashSet();
        var eventFds = calls.Where(c => c.Name is "eventfd" or "eventfd2" && PositiveResult(c.Result, out _))
            .Select(c => c.Result.Split('<')[0]).ToHashSet();
        var execs = 0; var processSpawns = 0;
        foreach (var call in calls)
        {
            var a = call.Args;
            switch (call.Name)
            {
                case "execve":
                    if (a.Length != 3 || ++execs > (packages ? 2 : 1)) Fail("exec-count");
                    var exe = CString(a[0]);
                    if (execs == 1)
                    {
                        if (call.Pid != root || call.Result != "0" || exe != binary ||
                            !Argv(a[1]).SequenceEqual(new[] { binary }.Concat(arguments))) Fail("root-command");
                    }
                    else if (!packages || exe != "/usr/bin/dpkg-query" || !Argv(a[1]).SequenceEqual(new[]
                    {
                        "/usr/bin/dpkg-query", "--admindir=/var/lib/dpkg", "--show",
                        "--showformat=${binary:Package}\\t${Version}\\t${db:Status-Status}\\n",
                        "docker-ce", "docker.io", "docker-compose-plugin", "docker-compose-v2",
                        "nvidia-container-toolkit", "nvidia-container-toolkit-base"
                    })) Fail("child-command");
                    break;
                case "open": case "openat": case "openat2":
                    if (a.Length != (call.Name == "open" ? 2 : call.Name == "openat" ? 3 : 4)) Fail("open-arity");
                    var flags = call.Name == "open" ? a.ElementAtOrDefault(1) : a.ElementAtOrDefault(2);
                    if (flags is null) Fail("open-syntax");
                    if (call.Name == "openat2")
                    {
                        var match = Rx(@"^\{flags=(?<flags>[A-Z0-9_|]+)(?:, mode=0[0-7]*)?(?:, resolve=[A-Z0-9_|]+)?\}$").Match(flags);
                        if (!match.Success) Fail("openat2-syntax");
                        flags = match.Groups["flags"].Value;
                    }
                    var allowedFlags = new[] { "O_RDONLY", "O_CLOEXEC", "O_DIRECTORY", "O_NOFOLLOW", "O_NONBLOCK", "O_LARGEFILE", "O_PATH", "O_NOCTTY" };
                    if (!flags.Split('|').Contains("O_RDONLY") && !flags.Split('|').Contains("O_PATH") ||
                        flags.Split('|').Any(f => !allowedFlags.Contains(f))) Fail("writable-or-unknown-open");
                    break;
                case "write": case "writev":
                    if (a.Length != 3) Fail("write-syntax");
                    var fd = Rx(@"^(?<fd>[0-9]+)(?:<(?<kind>.+)>)?$").Match(a[0]);
                    if (!fd.Success) Fail("write-descriptor");
                    var kind = fd.Groups["kind"].Value;
                    var pipe = Rx(@"^pipe:\[(?<inode>[0-9]+)\]$").Match(kind);
                    if (fd.Groups["fd"].Value is not ("1" or "2") &&
                        !(pipe.Success && pipes.Contains(pipe.Groups["inode"].Value)) &&
                        !(kind == "anon_inode:[eventfd]" && eventFds.Contains(fd.Groups["fd"].Value))) Fail("non-output-write");
                    break;
                case "mmap": case "mmap2":
                    if (a.Length != 6 || a[3].Contains("MAP_SHARED", StringComparison.Ordinal) &&
                        a[2].Contains("PROT_WRITE", StringComparison.Ordinal) && !a[3].Contains("MAP_ANONYMOUS", StringComparison.Ordinal))
                        Fail("writable-shared-map");
                    break;
                case "clone": case "clone3": case "fork": case "vfork":
                    if (call.Name is "clone" or "clone3")
                    {
                        var match = Rx(@"\bflags=(?<flags>[A-Z0-9_|]+)(?:,|\}|$)").Match(string.Join(',', a));
                        var allowed = new[] { "0", "SIGCHLD", "CLONE_VM", "CLONE_VFORK", "CLONE_THREAD", "CLONE_SIGHAND", "CLONE_FILES",
                            "CLONE_FS", "CLONE_SYSVSEM", "CLONE_SETTLS", "CLONE_PARENT_SETTID", "CLONE_CHILD_CLEARTID", "CLONE_CHILD_SETTID",
                            "CLONE_PIDFD", "CLONE_CLEAR_SIGHAND" };
                        if (!match.Success || match.Groups["flags"].Value.Split('|').Any(flag => !allowed.Contains(flag))) Fail("clone-flags");
                    }
                    else if (a.Length != 0) Fail("fork-arity");
                    var thread = string.Join(',', a).Contains("CLONE_THREAD", StringComparison.Ordinal);
                    if (!thread && PositiveResult(call.Result, out _) && (!packages || ++processSpawns > 1)) Fail("unexpected-process-spawn");
                    break;
                case "fcntl":
                    if (a.Length is < 2 or > 3 || a[1] is not ("F_GETFD" or "F_GETFL" or "F_GETPIPE_SZ" or "F_GETLK" or "F_SETFD" or "F_SETFL" or "F_DUPFD" or "F_DUPFD_CLOEXEC"))
                        Fail("file-control");
                    break;
                case "ioctl":
                    if (a.Length != 3 || a[1] is not ("TCGETS" or "TIOCGWINSZ" or "FIONREAD")) Fail("io-control");
                    break;
                case "prlimit64":
                    if (a.Length != 4 || a[0] != "0" || a[2] != "NULL") Fail("resource-change");
                    break;
                case "arch_prctl":
                    if (a.Length != 2 || a[0] is not ("ARCH_SET_FS" or "ARCH_GET_FS" or "ARCH_GET_GS" or "ARCH_SHSTK_STATUS"))
                        Fail("architecture-control");
                    break;
                case "prctl":
                    if (a.Length is < 1 or > 5 || a[0] is not ("PR_SET_NAME" or "PR_GET_NAME" or "PR_GET_SECCOMP" or "PR_GET_NO_NEW_PRIVS"))
                        Fail("process-control");
                    break;
                case "madvise":
                    if (a.Length != 3 || a[2] is not ("MADV_DONTNEED" or "MADV_FREE" or "MADV_WILLNEED" or "MADV_DONTDUMP" or "MADV_HUGEPAGE" or "MADV_NOHUGEPAGE"))
                        Fail("memory-advice");
                    break;
                case "membarrier":
                    if (a.Length is < 2 or > 3 || a[0] is not ("MEMBARRIER_CMD_QUERY" or "MEMBARRIER_CMD_PRIVATE_EXPEDITED" or "MEMBARRIER_CMD_REGISTER_PRIVATE_EXPEDITED"))
                        Fail("memory-barrier");
                    break;
                case "futex": case "futex_waitv":
                    if (a.Length < 2) Fail("sync-syntax");
                    break;
                case "wait4": case "waitid":
                    if (a.Length != (call.Name == "wait4" ? 4 : 5)) Fail("wait-arity");
                    break;
                case "kill": case "tkill": case "tgkill":
                    var targets = a.Take(call.Name == "tgkill" ? 2 : 1);
                    if (a.Length != (call.Name == "tgkill" ? 3 : 2) ||
                        targets.Any(target => !int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || !Owned(id)))
                        Fail("unowned-signal");
                    break;
                default:
                    if (!ReadOnly.TryGetValue(call.Name, out var arity) && !ProcessLocal.TryGetValue(call.Name, out arity))
                        Fail("unapproved-syscall-" + call.Name);
                    if (a.Length != arity) Fail("call-arity-" + call.Name);
                    break;
            }
        }
        foreach (var item in events)
        {
            if (!Owned(item.Pid)) Fail("unowned-event");
            if (Rx(@"^\+\+\+ exited with [0-9]{1,3} \+\+\+$").IsMatch(item.Text)) continue;
            var signal = Rx(@"^--- (?:SIGCHLD|SIGRT_[0-9]+|SIGURG) \{.*si_pid=(?<sender>[1-9][0-9]*).*\} ---$").Match(item.Text);
            if (!signal.Success || !int.TryParse(signal.Groups["sender"].Value, out var sender) || !Owned(sender)) Fail("unexpected-event");
        }
        if (execs != (packages ? 2 : 1)) Fail("missing-exec");
        return new(calls.Count, parents.Count + 1, execs);
    }

    private static bool PositiveResult(string result, out int value) =>
        int.TryParse(result.Split(' ', '<')[0], NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static string CString(string value)
    {
        if (!value.StartsWith('"') || !value.EndsWith('"')) Fail("string-syntax");
        try { return JsonSerializer.Deserialize<string>(value) ?? throw new InvalidDataException("TRACE_POLICY:string-null"); }
        catch (JsonException) { throw new InvalidDataException("TRACE_POLICY:string-escape"); }
    }
    private static string[] Argv(string value)
    {
        if (!value.StartsWith('[') || !value.EndsWith(']')) Fail("argv-syntax");
        return Split(value[1..^1]).Select(CString).ToArray();
    }
    private static string[] Split(string input)
    {
        if (input.Length == 0) return [];
        var result = new List<string>();
        var stack = new Stack<char>();
        var quote = false; var start = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (quote)
            {
                if (c == '\\') { if (++i == input.Length) Fail("argument-escape"); }
                else if (c == '"') quote = false;
                continue;
            }
            if (c == '"') quote = true;
            else if (c is '(' or '[' or '{' or '<') stack.Push(c);
            else if (c is ')' or ']' or '}' or '>')
            {
                if (stack.Count == 0) Fail("argument-nesting-empty-" + c);
                var opening = stack.Pop();
                if ((opening, c) is not (('(', ')') or ('[', ']') or ('{', '}') or ('<', '>')))
                    Fail("argument-nesting-" + opening + c);
            }
            else if (c == ',' && stack.Count == 0) { result.Add(input[start..i].Trim()); start = i + 1; }
        }
        if (quote || stack.Count != 0) Fail("argument-truncated");
        result.Add(input[start..].Trim());
        if (result.Any(s => s.Length == 0)) Fail("argument-empty");
        return result.ToArray();
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string reason) => throw new InvalidDataException("TRACE_POLICY:" + reason);
}
