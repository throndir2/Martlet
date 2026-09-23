using System.Buffers.Binary;
using System.Text;

namespace Martlet.LocalStt.Tests;

public sealed class PackagePeInspectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bounded_regular_and_delay_import_tables_are_reported_without_loading(bool delay)
    {
        var bytes = ImportedPe("ggml.dll", delay);
        Assert.Equal(["ggml.dll"], PortableExecutableInspector.Verify(bytes, bytes.Length, false));
        PortableExecutableInspector.VerifyDependencyGraph(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["whisper-cli.exe"] = ["ggml.dll", "KERNEL32.dll"],
            ["ggml.dll"] = []
        });
    }

    [Theory]
    [InlineData("missing.dll")]
    [InlineData("vcruntime140.dll")]
    public void Missing_non_system_dependencies_fail_closed(string dependency) =>
        Assert.Throws<LocalSttProvisioningException>(() =>
            PortableExecutableInspector.VerifyDependencyGraph(new Dictionary<string, string[]>
            {
                ["whisper-cli.exe"] = [dependency]
            }));

    [Fact]
    public void Cycles_are_bounded_and_rejected_instead_of_recursing_forever() =>
        Assert.Throws<LocalSttProvisioningException>(() =>
            PortableExecutableInspector.VerifyDependencyGraph(new Dictionary<string, string[]>
            {
                ["whisper-cli.exe"] = ["a.dll"],
                ["a.dll"] = ["b.dll"],
                ["b.dll"] = ["a.dll"]
            }));

    [Theory]
    [InlineData("rva-overflow")]
    [InlineData("raw-overflow")]
    [InlineData("unterminated-descriptors")]
    [InlineData("unterminated-thunks")]
    [InlineData("name-outside")]
    [InlineData("name-control")]
    [InlineData("name-traversal")]
    [InlineData("invalid-ordinal")]
    [InlineData("directory-count")]
    [InlineData("delay-va")]
    public void Malformed_PE_imports_are_refused(string kind)
    {
        var bytes = ImportedPe("ggml.dll", kind == "delay-va");
        switch (kind)
        {
            case "rva-overflow": Put(bytes, 0x188 + 12, uint.MaxValue); break;
            case "raw-overflow": Put(bytes, 0x188 + 20, uint.MaxValue); break;
            case "unterminated-descriptors": Put(bytes, 0x98 + 116 + 8, 20); break;
            case "unterminated-thunks": bytes.AsSpan(640).Fill(0xff); break;
            case "name-outside": Put(bytes, 512 + 12, 0xfffffff0); break;
            case "name-control": bytes[768] = 1; break;
            case "name-traversal": "../x.dll\0"u8.CopyTo(bytes.AsSpan(768)); break;
            case "invalid-ordinal": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(640), 0x8000000000010000); break;
            case "directory-count": Put(bytes, 0x98 + 108, uint.MaxValue); break;
            case "delay-va": Put(bytes, 512, 0); break;
        }
        var error = Assert.Throws<LocalSttProvisioningException>(() =>
            PortableExecutableInspector.Verify(bytes, bytes.Length, false));
        Assert.Equal(LocalSttProvisioningFailure.UnsupportedBinary, error.Failure);
    }

    [Fact]
    public void Overlapping_sections_are_rejected_before_mapping_imports()
    {
        var bytes = ImportedPe("ggml.dll", false);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x86), 2);
        bytes.AsSpan(0x188, 40).CopyTo(bytes.AsSpan(0x188 + 40));
        Assert.Throws<LocalSttProvisioningException>(() =>
            PortableExecutableInspector.Verify(bytes, bytes.Length, false));
    }

    private static byte[] ImportedPe(string dependency, bool delay)
    {
        var bytes = ImportFixture.Pe(false, 0x8664, 0);
        var index = delay ? 13 : 1;
        Put(bytes, 0x98 + 112 + index * 8, 0x1000);
        Put(bytes, 0x98 + 116 + index * 8, delay ? 64u : 40u);
        if (delay) Put(bytes, 512, 1);
        Put(bytes, 512 + (delay ? 4 : 12), 0x1100);
        Put(bytes, 512 + (delay ? 16 : 0), 0x1080);
        Put(bytes, 512 + (delay ? 12 : 16), delay ? 0x10c0u : 0x1080u);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(640), 0x1140);
        if (delay)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(704), 0x140001200);
        Encoding.ASCII.GetBytes(dependency + "\0").CopyTo(bytes, 768);
        "fixture_import\0"u8.CopyTo(bytes.AsSpan(834));
        return bytes;
    }

    private static void Put(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
