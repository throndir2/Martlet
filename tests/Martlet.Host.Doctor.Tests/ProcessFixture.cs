namespace Martlet.Host.Doctor.Tests;

// This executable belongs only to tests. It performs synthetic pipe/delay work, never host inventory.
internal static class ProcessFixture
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--verify-trace", var trace, var binary, var packages, .. var arguments] && packages is "packages" or "no-packages")
        {
            try
            {
                if (new FileInfo(trace).Length > 8 * 1024 * 1024) throw new InvalidDataException("TRACE_POLICY:trace-size");
                var summary = TracePolicy.Validate(File.ReadLines(trace), binary, arguments, packages == "packages");
                Console.WriteLine($"TRACE_POLICY: accepted {summary.Calls} calls, {summary.ProcessIds} owned PIDs, {summary.ExecAttempts} exact exec attempts.");
                return 0;
            }
            catch (InvalidDataException ex) { Console.Error.WriteLine(ex.Message); return 1; }
            catch (IOException) { Console.Error.WriteLine("TRACE_POLICY:trace-io"); return 1; }
            catch (UnauthorizedAccessException) { Console.Error.WriteLine("TRACE_POLICY:trace-access"); return 1; }
        }
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
