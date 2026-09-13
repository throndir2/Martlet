using System.Globalization;
using System.Text.RegularExpressions;

namespace Martlet.Host.Doctor.Tests;

internal sealed class TraceDescriptors
{
    private enum Kind { Input, Output, ReadOnlyFile, PipeRead, PipeWrite, Event, Poll }
    private sealed class Origin(Kind kind, string? label)
    {
        public Kind Kind { get; } = kind;
        public string? Label { get; set; } = label;
    }
    private sealed record Descriptor(Origin Origin, bool CloseOnExec);
    private sealed class Process(int group, Dictionary<int, Descriptor> files)
    {
        public int Group { get; } = group;
        public Dictionary<int, Descriptor> Files { get; set; } = files;
    }
    private readonly Dictionary<int, Process> processes;

    public TraceDescriptors(int root) => processes = new()
    {
        [root] = new(root, new()
        {
            [0] = new(new(Kind.Input, null), false),
            [1] = new(new(Kind.Output, null), false),
            [2] = new(new(Kind.Output, null), false)
        })
    };

    public int Group(int pid) => processes.TryGetValue(pid, out var process) ? process.Group : throw Error("unowned-process");

    public void Apply(TracePolicy.Call call)
    {
        if (!processes.TryGetValue(call.Pid, out var process)) throw Error("descriptor-process-order");
        var a = call.Args;
        if (call.Name is "clone" or "clone3" or "fork" or "vfork")
        {
            if (!ResultFd(call.Result, out var child) || child == 0) return;
            var group = call.InputFlags.Contains("CLONE_THREAD") ? process.Group : child;
            var files = call.InputFlags.Contains("CLONE_FILES") ? process.Files : new(process.Files);
            if (!processes.TryAdd(child, new(group, files))) throw Error("duplicate-process");
            return;
        }
        if (call.Name == "execve" && call.Result == "0")
        {
            if (process.Group != call.Pid) throw Error("thread-exec");
            process.Files = process.Files.Where(p => !p.Value.CloseOnExec).ToDictionary(p => p.Key, p => p.Value);
            return;
        }
        switch (call.Name)
        {
            case "open": case "openat": case "openat2":
                if (call.Name != "open" && !a[0].StartsWith("AT_FDCWD", StringComparison.Ordinal)) Get(process, a[0]);
                if (ResultFd(call.Result, out var opened))
                    Set(process, opened, new(new(Kind.ReadOnlyFile, Label(call.Result)), string.Join(',', a).Contains("O_CLOEXEC", StringComparison.Ordinal)));
                break;
            case "close":
                Get(process, a[0]);
                if (call.Result == "0") process.Files.Remove(Number(a[0]));
                break;
            case "dup": case "dup2": case "dup3":
                var source = Get(process, a[0]);
                if (ResultFd(call.Result, out var duplicated))
                {
                    if (call.Name != "dup" && duplicated != Number(a[1])) throw Error("duplicate-result");
                    if (call.Name != "dup2" || duplicated != Number(a[0]))
                        Set(process, duplicated, new(source.Origin, call.Name == "dup3" && a[2] == "O_CLOEXEC"));
                }
                break;
            case "fcntl":
                var descriptor = Get(process, a[0]);
                if (a[1] is "F_DUPFD" or "F_DUPFD_CLOEXEC" && ResultFd(call.Result, out var duplicate))
                    Set(process, duplicate, new(descriptor.Origin, a[1] == "F_DUPFD_CLOEXEC"));
                else if (a[1] == "F_SETFD" && call.Result == "0")
                {
                    if (a.Length != 3 || a[2] is not ("0" or "FD_CLOEXEC")) throw Error("descriptor-flags");
                    process.Files[Number(a[0])] = descriptor with { CloseOnExec = a[2] == "FD_CLOEXEC" };
                }
                break;
            case "pipe": case "pipe2":
                if (call.Result != "0") break;
                var pair = Regex.Match(a[0], @"^\[(?<read>[0-9]+)<pipe:\[(?<inode>[0-9]+)\]>, (?<write>[0-9]+)<pipe:\[(?<other>[0-9]+)\]>\]$", RegexOptions.NonBacktracking);
                if (!pair.Success || pair.Groups["inode"].Value != pair.Groups["other"].Value) throw Error("pipe-origin");
                var closeOnExec = call.Name == "pipe2" && a[1].Contains("O_CLOEXEC", StringComparison.Ordinal);
                var label = "pipe:[" + pair.Groups["inode"].Value + "]";
                Set(process, Number(pair.Groups["read"].Value), new(new(Kind.PipeRead, label), closeOnExec));
                Set(process, Number(pair.Groups["write"].Value), new(new(Kind.PipeWrite, label), closeOnExec));
                break;
            case "eventfd": case "eventfd2": case "epoll_create": case "epoll_create1":
                if (ResultFd(call.Result, out var created))
                    Set(process, created, new(new(call.Name.StartsWith("eventfd", StringComparison.Ordinal) ? Kind.Event : Kind.Poll,
                        Label(call.Result)), string.Join(',', a).Contains("CLOEXEC", StringComparison.Ordinal)));
                break;
            case "write": case "writev":
                if (Get(process, a[0]).Origin.Kind is not (Kind.Output or Kind.PipeWrite or Kind.Event)) throw Error("write-origin");
                break;
            case "read": case "readv": case "pread64": case "preadv": case "preadv2":
            case "fstat": case "fstatfs": case "getdents": case "getdents64": case "lseek": case "ioctl": case "fchdir":
                Get(process, a[0]);
                break;
            case "newfstatat": case "statx": case "readlinkat": case "faccessat": case "faccessat2":
                if (!a[0].StartsWith("AT_FDCWD", StringComparison.Ordinal)) Get(process, a[0]);
                break;
            case "mmap": case "mmap2":
                var anonymous = a[3].Contains("MAP_ANONYMOUS", StringComparison.Ordinal);
                if (!anonymous)
                {
                    Get(process, a[4]);
                    if (a[3].Contains("MAP_SHARED", StringComparison.Ordinal)) throw Error("shared-file-map");
                }
                else if (a[4] != "-1") throw Error("anonymous-map-descriptor");
                break;
            case "epoll_ctl":
                Get(process, a[0]); Get(process, a[2]);
                break;
            case "epoll_wait": case "epoll_pwait": case "epoll_pwait2":
                Get(process, a[0]);
                break;
        }
    }

    private static Descriptor Get(Process process, string reference)
    {
        if (!process.Files.TryGetValue(Number(reference), out var descriptor)) throw Error("unknown-inherited-descriptor");
        var label = Label(reference);
        if (label is not null)
        {
            if (descriptor.Origin.Label is null) descriptor.Origin.Label = label;
            else if (descriptor.Origin.Label != label) throw Error("descriptor-destination-mismatch");
        }
        return descriptor;
    }
    private static void Set(Process process, int fd, Descriptor value)
    {
        if (process.Files.Count >= 4096 && !process.Files.ContainsKey(fd)) throw Error("descriptor-limit");
        process.Files[fd] = value;
    }
    private static string? Label(string value)
    {
        var begin = value.IndexOf('<');
        if (begin < 0) return null;
        var end = value.LastIndexOf('>');
        if (end < begin) throw Error("descriptor-label");
        return value[(begin + 1)..end];
    }
    private static int Number(string value)
    {
        value = value.Split('<')[0];
        if (!(value.StartsWith("0x", StringComparison.Ordinal)
            ? int.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fd)
            : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out fd)) || fd < 0)
            throw Error("descriptor-number");
        return fd;
    }
    private static bool ResultFd(string result, out int fd) =>
        int.TryParse(result.Split(' ', '<')[0], NumberStyles.None, CultureInfo.InvariantCulture, out fd) && fd >= 0;
    private static InvalidDataException Error(string reason) => new("TRACE_POLICY:" + reason);
}
