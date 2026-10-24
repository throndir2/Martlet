using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Martlet.Core.Contracts;
using Martlet.Core.Planning;
using Martlet.Core.Settings;
using Martlet.HostClient;
using Martlet.Providers;

namespace Martlet.Desktop;

/// <summary>One finding of Diagnostics › Check your computers: <see cref="Problem"/> marks what makes Martlet slow or broken.</summary>
internal sealed record ComputerCheckLine(string HostId, bool Problem, string Text);

/// <summary>Diagnostics › Check your computers (docs/DIAGNOSTICS.md): for one paired host, how fast it answers, where its loaded
/// models sit (all on the graphics card, split with the processor, or on the processor) and one short test reply from each of its
/// Thinking models, judged by <see cref="ThinkingSpeed"/>. Every finding also goes to the desktop log, so a shared log shows it.
/// Only when the owner asks: it is never on a reply's path.</summary>
internal static class ComputerCheck
{
    /// <summary>A host that takes longer than this to say what it runs is slow on the network or busy.</summary>
    internal const int SlowAnswerMs = 2_000;
    internal const string TestInstructions = "This is a speed test. Answer exactly as asked, with nothing else.";
    internal const string TestQuestion = "Count from one to thirty in words, separated by commas.";
    private const int TestOutputTokens = 96;

    internal static async Task<IReadOnlyList<ComputerCheckLine>> RunAsync(PairedHost host, string dataDirectory, Action<ComputerCheckLine> found,
        CancellationToken token)
    {
        var lines = new List<ComputerCheckLine>();
        void Add(bool problem, string text)
        {
            var line = new ComputerCheckLine(host.HostId, problem, text);
            lines.Add(line);
            if (problem) ErrorLog.Warn("Check your computers: " + text);
            else ErrorLog.Info("Check your computers: " + text);
            found(line);
        }
        var name = host.HostId;
        HostConnection connection;
        try { connection = HostTextClient.Connect(WorkSharingRoster.TextTarget(host, SelfHostSetup.OllamaRouteId)); }
        catch (Exception error) when (error is HostTextException or InvalidOperationException)
        {
            Add(true, $"{name}: this PC can't read its pairing with {name}; pair it again in Devices › Add a computer.");
            return lines;
        }
        using (connection)
        {
            var watch = Stopwatch.StartNew();
            IReadOnlyList<HostRoute> routes;
            try { routes = await connection.ReadRoutesAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (HostConnectionException error)
            {
                Add(true, $"{name} answered {error.Code}, so Martlet can't check it.");
                return lines;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                Add(true, $"{name} isn't answering: check that it is on and on your network.");
                return lines;
            }
            var answered = (int)watch.ElapsedMilliseconds;
            Add(answered > SlowAnswerMs, answered > SlowAnswerMs
                ? $"{name} took {ThinkingSpeed.Seconds(answered)} to say what it runs: it is busy or the network is slow."
                : $"{name} answered in {answered} ms.");

            // Where its loaded models sit (the host's /api/ps), kept for the planner and Home as a host check keeps it.
            try
            {
                var (report, _) = await connection.ReadMachineReportAsync(token).ConfigureAwait(false);
                if (report?.Loaded is { Count: > 0 } loaded)
                {
                    HostsWindow.RecordLoaded(report, dataDirectory);
                    foreach (var model in loaded)
                        Add(ThinkingSpeed.Placement(model.Bytes, model.GraphicsBytes) != ModelPlacement.GraphicsCard,
                            ThinkingSpeed.DescribePlacement(model.Model, name, model.Bytes, model.GraphicsBytes));
                }
                else if (report is not null) Add(false, $"{name} has no model loaded now; it loads one at the next request.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (HostConnectionException error) when (error.Code == "request.invalid")
            {
                Add(false, $"{name} runs an older Martlet that doesn't say where its models sit; update it from the Devices map.");
            }
            catch (Exception error) when (error is HostConnectionException or HttpRequestException or IOException or OperationCanceledException)
            {
                Add(false, $"{name} didn't say where its models sit.");
            }

            var speeds = MeasuredFirstWords.Load(dataDirectory);
            foreach (var route in routes.Where(r => r.RouteId == SelfHostSetup.OllamaRouteId || SelfHostSetup.IsDeepThinkingRoute(r.RouteId)))
            {
                var usual = speeds.Models.Where(m => string.Equals(m.Host, MeasuredFirstWords.HostKey(host.Pairing.Origin), StringComparison.OrdinalIgnoreCase) &&
                    ThinkingSpeed.SameModel(m.Model, route.ModelId)).Select(m => m.UsualMs).FirstOrDefault();
                var (problem, text) = await TestAsync(connection, host, route, routes, usual, dataDirectory, token).ConfigureAwait(false);
                Add(problem, text);
            }
        }
        return lines;
    }

    /// <summary>One short test reply on <paramref name="route"/>: how soon its first words come and how fast it writes after.</summary>
    private static async Task<(bool Problem, string Text)> TestAsync(HostConnection connection, PairedHost host, HostRoute route,
        IReadOnlyList<HostRoute> routes, int? usualMs, string dataDirectory, CancellationToken token)
    {
        var what = SelfHostSetup.IsDeepThinkingRoute(route.RouteId) ? $"the Thinking pool's {route.ModelId}" : route.ModelId;
        var where = host.HostId;
        var ids = new CorrelationIds { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };
        var watch = Stopwatch.StartNew();
        long? first = null;
        var characters = 0;
        try
        {
            await foreach (var delta in connection.StreamChatAsync(route, ids, 1, DateTimeOffset.UtcNow.AddSeconds(90), TestInstructions, [],
                TestQuestion, 0, TestOutputTokens, 4_096, null, new GenerationSettings
                {
                    Reasoning = false,
                    // The window each route loads anyway, so the test never makes Ollama load the model again.
                    ContextTokens = route.RouteId == SelfHostSetup.OllamaRouteId
                        ? HostContextWindow.For(host.HostId, host.Pairing.Origin, route, routes, dataDirectory)
                        : GenerationSettings.DeepThinkingContextTokens
                }, token).ConfigureAwait(false))
            {
                if (delta.Length == 0) continue;
                first ??= watch.ElapsedMilliseconds;
                characters += delta.Length;
                if (characters > 2_000) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HostConnectionException error) when (error.Code is "job.busy" or "job.preempted")
        {
            return (false, $"{what} on {where} is busy with other work now, so it wasn't tested; check again in a moment.");
        }
        catch (HostConnectionException error) when (error.Code == "job.deadline")
        {
            return (true, $"{what} on {where} gave no answer within 90 s: it is far too slow or stuck. Restart the host's Ollama.");
        }
        catch (Exception error) when (error is HostConnectionException or HttpRequestException or IOException or OperationCanceledException)
        {
            return (true, $"{what} on {where} failed the test reply ({(error as HostConnectionException)?.Code ?? "no answer"}).");
        }
        if (first is not { } firstMs) return (true, $"{what} on {where} answered the test with no words.");
        var writing = watch.ElapsedMilliseconds - firstMs;
        double? rate = characters >= 40 && writing >= 300 ? characters / 4.0 / (writing / 1000.0) : null;
        return ThinkingSpeed.Judge(what, where, (int)firstMs, rate, usualMs);
    }
}
