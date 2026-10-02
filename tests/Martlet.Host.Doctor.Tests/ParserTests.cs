using System.Globalization;
using Martlet.Host.Doctor;

namespace Martlet.Host.Doctor.Tests;

public sealed class ParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not cgroup data")]
    [InlineData("0::$(bad)")]
    public void Malformed_context_cannot_pass_as_a_host(string cgroup) =>
        Assert.Equal(FindingCode.HOST_MALFORMED, Parsers.Context(KernelFlavor.Generic, cgroup, false).Code);

    [Theory]
    [InlineData("ID=\"$(touch /tmp/pwn)\"\nVERSION_ID=24.04")]
    [InlineData("ID=ubuntu\nID=ubuntu\nVERSION_ID=24.04")]
    [InlineData("ID=ubuntu\nVERSION_ID=\"24.04\";curl attacker")]
    [InlineData("ID=ubuntu\nVERSION_ID='24.04")]
    [InlineData("ID=ubuntu\nVERSION_ID=24.04\nVERSION_ID=26.04")]
    public void Os_release_is_data_not_shell(string input)
    {
        var runtime = FixtureCatalog.Create("inventory").Runtime;
        Assert.Equal(FindingCode.HOST_MALFORMED, Parsers.Platform(runtime, input, "6.8.0-99-generic").Code);
    }

    [Theory]
    [InlineData("debian", "13.0", Distro.Debian)]
    [InlineData("fedora", "44.0", Distro.Other)]
    [InlineData("ubuntu", "22.04", Distro.Ubuntu)]
    public void Other_distribution_or_version_is_unqualified_not_a_binary_incompatibility(string id, string version, Distro distro)
    {
        var result = Parsers.Platform(FixtureCatalog.Create("inventory").Runtime, $"ID={id}\nVERSION_ID={version}", "6.8.0-99-generic");
        Assert.Equal(FindingCode.HOST_UNQUALIFIED_PLATFORM, result.Code);
        Assert.Equal(distro, result.Value!.Distribution);
    }

    [Fact]
    public void Wsl_and_kernel_custom_suffixes_do_not_become_host_identifiers()
    {
        var result = Parsers.Platform(FixtureCatalog.Create("inventory").Runtime, "ID=ubuntu\nVERSION_ID=24.04", "6.6.87.2-microsoft-standard-WSL2");
        Assert.Equal(FindingCode.HOST_UNQUALIFIED_PLATFORM, result.Code);
        Assert.Equal(KernelFlavor.Wsl, result.Value!.KernelFlavor);
        Assert.DoesNotContain("microsoft", result.Value.KernelRelease);
    }

    [Theory]
    [InlineData("MemTotal: 1 MB\nMemAvailable: 1 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB")]
    [InlineData("MemTotal: -1 kB\nMemAvailable: 0 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB")]
    [InlineData("MemTotal: 1 kB\nMemAvailable: 2 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB")]
    [InlineData("MemTotal: 9223372036854775807 kB\nMemAvailable: 0 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB")]
    [InlineData("MemTotal: 1,024 kB\nMemAvailable: 0 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB")]
    [InlineData("MemTotal: 1024 kB\nMemTotal: 1024 kB\nMemAvailable: 0 kB\nSwapTotal: 0 kB\nSwapFree: 0 kB")]
    public void Memory_rejects_invalid_units_overflow_locale_duplicates_and_inconsistency(string input) =>
        Assert.Equal(FindingCode.HOST_MALFORMED, Parsers.Memory(input).Code);

    [Fact]
    public void Cpu_flags_are_intersection_not_an_invented_all_core_capability()
    {
        var cpu = Parsers.Cpu("processor:0\nflags:sse4_2 avx avx2\nprocessor:1\nflags:sse4_2 avx");
        Assert.Equal(FindingCode.HOST_OBSERVED, cpu.Code);
        Assert.False(cpu.Value!.Avx2);
        Assert.True(cpu.Value.Sse42);
    }

    [Theory]
    [InlineData("\"NVIDIA RTX 6000, Ada Generation\", 570.124.06, 49140\n", FindingCode.HOST_OBSERVED)]
    [InlineData("NVIDIA A100-SXM4-40GB, 570.124.06, 40960\n", FindingCode.HOST_OBSERVED)]
    [InlineData("NVIDIA RTX 4090, 570.124.06, 24,564\n", FindingCode.HOST_MALFORMED)]
    [InlineData("NVIDIA RTX 4090, 570.124.06, 24564 MiB\n", FindingCode.HOST_MALFORMED)]
    [InlineData("NVIDIA RTX 4090, 570.124.06, [N/A]\n", FindingCode.HOST_UNSUPPORTED_RESPONSE)]
    [InlineData("NVIDIA secret-host-UUID, 570.124.06, 24564\n", FindingCode.HOST_MALFORMED)]
    [InlineData("\"NVIDIA RTX 4090, 570.124.06, 24564\n", FindingCode.HOST_MALFORMED)]
    [InlineData("\"NVIDIA RTX 4090\"oops, 570.124.06, 24564\n", FindingCode.HOST_MALFORMED)]
    [InlineData("NVIDIA RTX 4090, $(secret), 24564\n", FindingCode.HOST_MALFORMED)]
    [InlineData("NVIDIA RTX 4090, 570.124.06, -10\n", FindingCode.HOST_MALFORMED)]
    public void Gpu_csv_is_bounded_strict_and_not_a_cuda_compatibility_claim(string input, FindingCode code) =>
        Assert.Equal(code, Parsers.Nvidia(new(ReadStatus.Success, input, 0, FixtureCatalog.Timestamp, 1)).Code);

    [Theory]
    [InlineData(4, FindingCode.HOST_PERMISSION_DENIED)]
    [InlineData(6, FindingCode.HOST_GPU_NOT_VISIBLE)]
    [InlineData(9, FindingCode.HOST_DRIVER_UNAVAILABLE)]
    [InlineData(12, FindingCode.HOST_DRIVER_UNAVAILABLE)]
    [InlineData(13, FindingCode.HOST_UNSUPPORTED_RESPONSE)]
    [InlineData(255, FindingCode.HOST_COMMAND_FAILED)]
    public void Nvidia_exit_codes_are_not_swallowed(int exit, FindingCode code) =>
        Assert.Equal(code, Parsers.Nvidia(new(ReadStatus.Success, "private native output", exit, FixtureCatalog.Timestamp, 1)).Code);

    [Fact]
    public void Gpu_count_is_capped_and_no_partial_list_is_accepted()
    {
        var text = string.Concat(Enumerable.Repeat("NVIDIA RTX 4090, 570.124.06, 24564\n", 17));
        Assert.Equal(FindingCode.HOST_OUTPUT_LIMIT, Parsers.Nvidia(new(ReadStatus.Success, text, 0, FixtureCatalog.Timestamp, 1)).Code);
    }

    [Fact]
    public void Package_query_exit_one_preserves_known_installed_metadata_without_executing_plugin()
    {
        var result = Parsers.Packages(new(ReadStatus.Success, "docker-ce:amd64\t5:28.0.4-1~ubuntu.24.04~noble\tinstalled\n", 1, FixtureCatalog.Timestamp, 1));
        Assert.Equal(FindingCode.HOST_OBSERVED, result.Code);
        Assert.Equal("28.0.4", result.Value!.Versions["docker-ce"]);
        Assert.Single(result.Value.Versions);
    }

    [Theory]
    [InlineData("arbitrary-command\t1.2.3\tinstalled\n", 0, FindingCode.HOST_MALFORMED)]
    [InlineData("docker-ce\t$(secret)\tinstalled\n", 0, FindingCode.HOST_MALFORMED)]
    [InlineData("docker-ce\t28.0.4\thalf-configured\n", 0, FindingCode.HOST_INCOMPLETE)]
    [InlineData("docker-ce\t28.0.4\tinstalled\n", 2, FindingCode.HOST_COMMAND_FAILED)]
    public void Package_responses_validate_name_version_state_and_exit(string input, int exit, FindingCode code) =>
        Assert.Equal(code, Parsers.Packages(new(ReadStatus.Success, input, exit, FixtureCatalog.Timestamp, 1)).Code);

    [Fact]
    public void Docker_group_observation_is_root_equivalent_but_never_daemon_evidence()
    {
        var result = Parsers.DockerAccess("Uid:\t1000 1000 1000 1000\nGid:\t1000 1000 1000 1000\nGroups:\t1000 999",
            "docker:x:999:someone-private\n", BinaryPresence.Present);
        Assert.Equal(FindingCode.HOST_DOCKER_PRIVILEGE, result.Code);
        Assert.True(result.Value!.DockerGroupMember);
        Assert.Null(result.Value.DaemonAccess);
        Assert.False(result.Value.DaemonContacted);
    }

    [Fact]
    public void Port_queries_do_not_need_to_bind_and_any_local_occupancy_is_conservative()
    {
        var result = Parsers.Port(7443, "sl local_address rem_address st\n0: 0100007F:1D13 00000000:0000 01\n",
            "sl local_address rem_address st\n", "sl local_address rem_address st\n", "sl local_address rem_address st\n");
        Assert.Equal(FindingCode.HOST_PORT_IN_USE, result.Code);
        Assert.False(result.Value!.GatewayAvailabilityEstablished);
        Assert.Equal(FindingCode.HOST_MALFORMED, Parsers.Port(7443, "sl local_address\n0: broken\n").Code);
    }

    [Fact]
    public void Parsing_does_not_depend_on_machine_locale()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(FindingCode.HOST_OBSERVED, Parsers.Nvidia(new(ReadStatus.Success,
                "NVIDIA RTX 4090, 570.124.06, 24564\n", 0, FixtureCatalog.Timestamp, 1)).Code);
            Assert.Contains("4.00 GiB", ReportFormatter.Human(ContractTests.Report("inventory", DoctorScope.Inventory)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
