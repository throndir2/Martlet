using System.Text.RegularExpressions;
namespace Martlet.Host.Inventory;
internal static class EvidenceTextRules
{
    private static readonly Regex NumericVersion = Pattern(@"^[0-9]{1,4}(\.[0-9]{1,4}){1,3}\z");
    private static readonly Regex OsVersion = Pattern(@"^[0-9]{1,4}(\.[0-9]{1,4}){0,3}\z");
    private static readonly Regex GpuModel = Pattern(@"^(?:NVIDIA )?(?:(?:GeForce )?(?:RTX|GTX) [0-9]{3,4}(?: Ti| SUPER| Laptop GPU)?|(?:Tesla |Quadro )?[A-Z][0-9]{2,4}(?:-SXM[0-9])?(?:-[0-9]{1,3}GB)?(?: PCIe| SXM| SXM[0-9]| PCIE| NVL)?|(?:Quadro )?RTX (?:A[0-9]{3,4}|[0-9]{3,4})(?:,? Ada Generation)?|TITAN (?:V|RTX|X))\z");
    private static Regex Pattern(string text) => new(text, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static bool SafeVersion(string? version) => version is not null && NumericVersion.IsMatch(version);
    internal static bool SafeOsVersion(string? version) => version is not null && OsVersion.IsMatch(version);
    internal static bool SafeGpu(GpuData gpu) => gpu is not null && gpu.Model is { Length: > 0 and <= 96 } &&
        GpuModel.IsMatch(gpu.Model) && SafeVersion(gpu.DriverVersion) && gpu.MemoryMiB is > 0 and <= 1048576;

}
