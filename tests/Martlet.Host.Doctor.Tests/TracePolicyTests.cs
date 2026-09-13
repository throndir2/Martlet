namespace Martlet.Host.Doctor.Tests;

public sealed class TracePolicyTests
{
    private const string Root = "101 execve(\"/pkg/martlet-host\", [\"/pkg/martlet-host\", \"--help\"], 0x123 /* 4 vars */) = 0";
    private static TracePolicy.Summary Validate(params string[] records) => TracePolicy.Validate([Root, .. records], "/pkg/martlet-host", ["--help"], false);

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
            "101 exit_group(0) = ?");
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
}
