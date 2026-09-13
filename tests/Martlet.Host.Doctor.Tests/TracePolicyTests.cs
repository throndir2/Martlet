namespace Martlet.Host.Doctor.Tests;

public sealed class TracePolicyTests
{
    private const string Root = "101 execve(\"/pkg/martlet-host\", [\"/pkg/martlet-host\", \"--help\"], 0x123 /* 4 vars */) = 0";
    private static TracePolicy.Summary Validate(params string[] records) => TracePolicy.Validate(
        [Root, .. records, "101 exit_group(0) = ?", "101 +++ exited with 0 +++"], "/pkg/martlet-host", ["--help"], false);

    [Theory]
    [InlineData("101 utimensat(AT_FDCWD, \"/etc/target\", NULL, 0) = 0")]
    [InlineData("101 fchmod(3</etc/target>, 0777) = 0")]
    [InlineData("101 ftruncate(3</etc/target>, 0) = 0")]
    [InlineData("101 future_syscall(0) = 0")]
    [InlineData("101 read() = 0")]
    [InlineData("101 madvise(0x123, 4096, MADV_HWPOISON) = 0")]
    [InlineData("101 clone3({flags=CLONE_VM|CLONE_THREAD|CLONE_UNTRACED, exit_signal=0}, 88) = 102")]
    [InlineData("101 openat(AT_FDCWD, \"/etc/target\", O_WRONLY|O_TRUNC) = 3</etc/target>")]
    [InlineData("101 write(3</etc/target>, \"x\", 1) = 1")]
    [InlineData("101 write(3<pipe:[777]>, \"x\", 1) = 1")]
    [InlineData("101 socket(AF_INET, SOCK_STREAM, IPPROTO_IP) = 3")]
    [InlineData("101 mmap(NULL, 4096, PROT_READ|PROT_WRITE, MAP_SHARED, 3</etc/target>, 0) = 0x123")]
    [InlineData("101 kill(-1, SIGKILL) = 0")]
    [InlineData("101 ioctl(3, FIONBIO, [1]) = 0")]
    [InlineData("101 ptrace(PTRACE_ATTACH, 999, NULL, NULL) = 0")]
    [InlineData("unexpected diagnostic text")]
    [InlineData("101 read(0x3, 0x456, 0x1000) = UNRECOGNIZED")]
    [InlineData("999 getpid() = 999")]
    [InlineData("101 --- SIGCHLD {si_signo=SIGCHLD, si_pid=999} ---")]
    [InlineData("101 <... openat resumed>) = 3")]
    [InlineData("101 openat(AT_FDCWD, \"/etc/target\", O_RDONLY <unfinished ...>")]
    public void Mutation_unknown_unowned_and_incomplete_records_are_denied_without_executing_them(string record) =>
        Assert.Throws<InvalidDataException>(() => Validate(record));

    [Fact]
    public void Resumed_mutation_is_reconstructed_and_denied()
    {
        Assert.Throws<InvalidDataException>(() => Validate(
            "101 utimensat(AT_FDCWD, \"/etc/target\", <unfinished ...>",
            "101 <... utimensat resumed>NULL, 0) = 0"));
        Assert.Throws<InvalidDataException>(() => Validate(
            "101 ftruncate(3</etc/target>, <unfinished ...>",
            "101 <... fchmod resumed>0) = 0"));
    }

    [Fact]
    public void Own_threads_and_reads_can_interleave_before_the_clone_resume()
    {
        var summary = Validate(
            "101 clone3({flags=CLONE_VM|CLONE_THREAD, exit_signal=0}, 88 <unfinished ...>",
            "102 rseq(0x123, 32, 0, 0x53053053) = 0",
            "101 <... clone3 resumed>) = 102",
            "102 openat(AT_FDCWD, \"/proc/cpuinfo\", O_RDONLY|O_CLOEXEC <unfinished ...>",
            "101 pipe2([3<pipe:[777]>, 4<pipe:[777]>], O_CLOEXEC) = 0",
            "102 <... openat resumed>) = 5</proc/cpuinfo>",
            "102 read(0x5, 0x123, 0x1000) = 0x10",
            "101 write(4<pipe:[777]>, \"x\", 1) = 1",
            "102 write(1</owned/report>, \"hello\", 5) = 5",
            "102 exit(0) = ?",
            "102 +++ exited with 0 +++");
        Assert.Equal(2, summary.ProcessIds);
        Assert.Equal(1, summary.ExecAttempts);
    }

    [Fact]
    public void Truncation_duplicate_pending_and_wrong_resume_pid_are_not_ignored()
    {
        Assert.Throws<InvalidDataException>(() => Validate("101 write(1, \"truncated, 9) = 9"));
        Assert.Throws<InvalidDataException>(() => Validate(
            "101 read(3, <unfinished ...>", "101 read(4, <unfinished ...>"));
        Assert.Throws<InvalidDataException>(() => Validate(
            "101 read(3, <unfinished ...>", "102 <... read resumed>0x123, 1) = 1"));
    }

    [Fact]
    public void Root_arguments_and_unknown_architecture_controls_are_exact()
    {
        Assert.Throws<InvalidDataException>(() => TracePolicy.Validate([Root], "/pkg/martlet-host", ["doctor"], false));
        Assert.Throws<InvalidDataException>(() => Validate("101 arch_prctl(0x9999, 0x123) = 0"));
        Assert.Throws<InvalidDataException>(() => Validate("101 prlimit64(0, RLIMIT_NOFILE, {rlim_cur=4096}, NULL) = 0"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clone3_documented_input_output_struct_transition_is_not_a_descriptor_close(bool resumed)
    {
        const string input = "{flags=CLONE_VM|CLONE_THREAD|CLONE_PARENT_SETTID, parent_tid=0x123, exit_signal=0, stack=0x200, stack_size=0x1000}";
        var records = resumed
            ? new[] { "101 clone3(" + input + " <unfinished ...>", "102 rseq(0x123, 32, 0, 0x53053053) = 0", "101 <... clone3 resumed> => {parent_tid=[102]}, 88) = 102", "102 exit(0) = ?", "102 +++ exited with 0 +++" }
            : ["101 clone3(" + input + " => {parent_tid=[102]}, 88) = 102", "102 rseq(0x123, 32, 0, 0x53053053) = 0", "102 exit(0) = ?", "102 +++ exited with 0 +++"];
        Assert.Equal(2, Validate(records).ProcessIds);
    }

    [Theory]
    [InlineData("101 read({before=1} => {after=2}, 1, 1) = 1")]
    [InlineData("101 clone3({flags=CLONE_VM|CLONE_THREAD} > {parent_tid=[102]}, 88) = 102")]
    [InlineData("101 clone3({flags=CLONE_VM|CLONE_THREAD} => {unknown=1}, 88) = 102")]
    [InlineData("101 clone3({flags=CLONE_VM|CLONE_THREAD} => {parent_tid=[102]} => {parent_tid=[102]}, 88) = 102")]
    public void Struct_transition_support_does_not_accept_arbitrary_arrows_or_future_fields(string record) =>
        Assert.Throws<InvalidDataException>(() => Validate(record));

    [Fact]
    public void Inherited_or_closed_descriptors_cannot_be_rebound_into_allowed_stdout()
    {
        var unknown = Assert.Throws<InvalidDataException>(() => Validate(
            "101 dup2(9</private/target>, 1</owned/report>) = 1</private/target>",
            "101 write(1</private/target>, \"x\", 1) = 1"));
        Assert.Contains("unknown-inherited-descriptor", unknown.Message);
        var readOnly = Assert.Throws<InvalidDataException>(() => Validate(
            "101 openat(AT_FDCWD, \"/private/target\", O_RDONLY) = 3</private/target>",
            "101 dup2(3</private/target>, 1</owned/report>) = 1</private/target>",
            "101 write(1</private/target>, \"x\", 1) = 1"));
        Assert.Contains("write-origin", readOnly.Message);
        Assert.Throws<InvalidDataException>(() => Validate(
            "101 openat(AT_FDCWD, \"/private/target\", O_RDONLY) = 3</private/target>",
            "101 close(3</private/target>) = 0",
            "101 fcntl(3</private/target>, F_DUPFD, 1) = 1</private/target>",
            "101 write(1</private/target>, \"x\", 1) = 1"));
    }

    [Fact]
    public void Shared_file_read_mapping_is_denied_before_a_write_protection_promotion()
    {
        var error = Assert.Throws<InvalidDataException>(() => Validate(
            "101 openat(AT_FDCWD, \"/private/target\", O_RDONLY) = 3</private/target>",
            "101 mmap(NULL, 4096, PROT_READ, MAP_SHARED, 3</private/target>, 0) = 0x1000",
            "101 mprotect(0x1000, 4096, PROT_READ|PROT_WRITE) = 0"));
        Assert.Contains("shared-file-map", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Truncation_at_a_clean_call_boundary_is_not_complete_execution(bool read)
    {
        var records = read ? new[] { Root, "101 read(0, 0x1000, 1) = 1" } : [Root];
        Assert.Throws<InvalidDataException>(() => TracePolicy.Validate(records, "/pkg/martlet-host", ["--help"], false));
    }

    [Fact]
    public void Every_child_needs_terminal_evidence_even_when_exit_group_covers_its_intent()
    {
        string[] records =
        [
            Root, "101 clone3({flags=CLONE_VM|CLONE_THREAD|CLONE_FILES, exit_signal=0}, 88) = 102",
            "102 rseq(0x123, 32, 0, 0x53053053) = 0", "101 exit_group(0) = ?", "101 +++ exited with 0 +++"
        ];
        Assert.Throws<InvalidDataException>(() => TracePolicy.Validate(records, "/pkg/martlet-host", ["--help"], false));
        Assert.Equal(2, TracePolicy.Validate([.. records, "102 +++ exited with 0 +++"], "/pkg/martlet-host", ["--help"], false).ProcessIds);
        Assert.Throws<InvalidDataException>(() => TracePolicy.Validate(
            [.. records, "102 +++ exited with 0 +++"], "/pkg/martlet-host", ["--help"], false, 1));
    }

    [Fact]
    public void Child_stdout_can_inherit_a_proven_pipe_and_survive_parent_close_and_exec()
    {
        var start = BoundedCommandRunner.ApprovedStartInfo(CommandKind.PackageVersions);
        var argv = System.Text.Json.JsonSerializer.Serialize(new[] { start.FileName }.Concat(start.ArgumentList));
        string[] records =
        [
            Root,
            "101 pipe2([3<pipe:[777]>, 4<pipe:[777]>], O_CLOEXEC) = 0",
            "101 clone3({flags=CLONE_VM|CLONE_VFORK, exit_signal=SIGCHLD}, 88) = 102",
            "101 close(4<pipe:[777]>) = 0",
            "102 dup2(4<pipe:[777]>, 1</owned/report>) = 1<pipe:[777]>",
            $"102 execve(\"/usr/bin/dpkg-query\", {argv}, 0x123 /* 4 vars */) = 0",
            "102 write(1<pipe:[777]>, \"data\", 4) = 4",
            "101 read(0x3, 0x1000, 0x4) = 0x4",
            "102 exit_group(0) = ?", "102 +++ exited with 0 +++",
            "101 exit_group(0) = ?", "101 +++ exited with 0 +++"
        ];
        Assert.Equal(2, TracePolicy.Validate(records, "/pkg/martlet-host", ["--help"], true).ExecAttempts);
    }
}
