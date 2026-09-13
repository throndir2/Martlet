namespace Martlet.Host.Doctor.Tests;

// This executable belongs only to tests. It performs synthetic pipe/delay work, never host inventory.
internal static class ProcessFixture
{
    public static async Task<int> Main(string[] args)
    {
        if (args is not ["--process-fixture", var mode]) return 3;
        switch (mode)
        {
            case "echo": await Console.Out.WriteAsync("synthetic response\n"); return 0;
            case "nonzero": await Console.Error.WriteAsync("SECRET_CANARY_PRIVATE_NATIVE_ERROR"); return 37;
            case "flood-stdout": await Console.Out.WriteAsync(new string('x', 65536)); await Task.Delay(30000); return 0;
            case "flood-stderr": await Console.Error.WriteAsync(new string('s', 65536)); await Task.Delay(30000); return 0;
            case "invalid-utf8": await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xff, 0xfe }); return 0;
            case "sleep": await Console.Out.WriteAsync(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); await Console.Out.FlushAsync(); await Task.Delay(30000); return 0;
            case "delayed-echo": await Task.Delay(100); await Console.Out.WriteAsync("late success"); return 0;
            default: return 3;
        }
    }
}
