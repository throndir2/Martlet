using System.IO;
using System.Text.Json;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;

namespace Martlet.Desktop;

/// <summary>
/// The work Companion › Reading (<see cref="ReadingPage"/> in Pages\Reading) shows, which stays in the window (rule 4 of
/// Pages\README.md): its state (the picked models, setup failures, Read my screen now), checking Windows OCR, saving the Reading
/// list (<see cref="ReadingList"/>, pools-local.json, this PC's own; Devices uses it too), setting up the Reading role with the
/// same martlet-host flow as every role, and Read my screen now. MainWindow.ReadingPage.cs gives the page its services.
/// </summary>
public partial class MainWindow
{
    internal const string ReadingThisPc = "this-pc";

    /// <summary>What setting the Reading role up with <paramref name="model"/> downloads and sends, in one sentence for its
    /// confirmation; the wiki's Vision#set-up-the-reading-role has every package, model and licence. Another computer
    /// (<paramref name="elsewhere"/>) gets screenshots while Martlet watches.</summary>
    internal static string ReadingTerms(OptionalExtras.ReadingRoleModel model, bool elsewhere) =>
        $"It runs on {(model.NeedsNvidia ? "an NVIDIA graphics card (driver 580 or newer)" : "the processor")} and downloads " +
        $"{model.Download} of open packages and models (Apache-2.0 and MIT{(model.NeedsNvidia ? ", with NVIDIA's CUDA libraries" : "")})" +
        (elsewhere ? ", and while Martlet watches, this PC sends it screenshots that are read in memory and not kept." : ".");

    /// <summary>The model each place's pills show (place: <see cref="ReadingThisPc"/> or a host ID), until it is set up there.</summary>
    private readonly Dictionary<string, string> readingModels = new(StringComparer.Ordinal);
    /// <summary>Why the last setup failed, by place.</summary>
    private readonly Dictionary<string, string> readingFailures = new(StringComparer.Ordinal);
    private string? readingPending;
    private string? readingTestState;
    private string? readingTestText;
    private bool readingTesting;
    private bool? readingWindows;
    private bool readingWindowsBusy;

    private async Task CheckWindowsReadingAsync()
    {
        readingWindowsBusy = true;
        try { readingWindows = await Task.Run(() => WindowsScreenTextReader.Available); }
        finally { readingWindowsBusy = false; }
        if (!closing && openTab == CompanionTab.Reading && !tabEdited) RenderTab();
    }

    // ---------- the Reading list ----------

    /// <summary>Saves <paramref name="next"/> in the Reading list (by its key); watching follows it at its next screenshot.</summary>
    private bool SaveReadingList(Func<PoolList, PoolList> change, string done)
    {
        if (store is null) return false;
        ReadingList.Ensure(store.DataDirectory);
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.Reading, change(ReadingList.Load(store.DataDirectory))))
        {
            ActionText.Text = "Couldn't save the Reading list on this PC.";
            return false;
        }
        ErrorLog.Info("Pools: " + done);
        ActionText.Text = done;
        // Rebuilt after the click's own event finishes, so the control that changed isn't replaced under it.
        Dispatcher.InvokeAsync(() =>
        {
            if (!closing && openTab == CompanionTab.Reading) RenderTab();
        });
        return true;
    }

    private void SaveReadingMember(PoolMember next, string done) => SaveReadingList(list => list.With(next), done);

    /// <summary>Puts <paramref name="hostId"/> first in the Reading list (added when missing, on again when off), as Devices'
    /// Use for reading on a host a friend shares does.</summary>
    private void UseReadingHost(string hostId, string where)
    {
        var offer = hostChecks.GetValueOrDefault(hostId)?.Offers?.GetValueOrDefault(HostRoles.Ocr);
        SaveReadingList(list =>
        {
            var member = (list.Find(PoolMember.Computer(hostId).Key) ?? PoolMember.Computer(hostId)) with { Off = false };
            if (!string.IsNullOrEmpty(offer)) member = member.WithSetting(PoolSettingKeys.Model, offer);
            return list.With(member).Move(member.Key, -PoolSettings.MaximumMembers);
        }, $"Martlet now reads the text on your screen on {where} first.");
    }

    /// <summary>Whether this PC's Reading list reads on <paramref name="hostId"/> (a member that is on names it).</summary>
    private bool ReadsOn(string hostId) => store is not null &&
        ReadingList.Load(store.DataDirectory).Members.Any(m => !m.Off && m.OnHost && m.HostId == hostId);

    /// <summary>Takes <paramref name="hostId"/> out of the Reading list (a host forgotten). When nothing else was on, This PC reads
    /// with Windows OCR, as before.</summary>
    private void ForgetReadingHost(string hostId)
    {
        if (store is null || !ReadingList.Load(store.DataDirectory).Members.Any(m => m.OnHost && m.HostId == hostId)) return;
        SaveReadingList(list =>
        {
            var next = list with { Members = [.. list.Members.Where(m => !(m.OnHost && m.HostId == hostId))] };
            return next.NoneOn && !list.NoneOn ? next.With(ReadingPool.Windows()) : next;
        }, $"{hostId} is no longer in the Reading list.");
    }

    /// <summary>After the Reading role was set up (or switched) on <paramref name="hostId"/> (null: this PC), the members that read
    /// there keep its model (This PC reads with the role then).</summary>
    private void ReadingSetUpDone(string? hostId, OptionalExtras.ReadingRoleModel model, string where)
    {
        var own = ThisPcHost()?.HostId;
        readingModels.Remove(hostId ?? ReadingThisPc);
        SaveReadingList(list =>
        {
            var members = list.Members.Select(m => hostId is null ? m.Kind == PoolMemberKind.ThisPc
                    ? m.WithSetting(PoolSettingKeys.Engine, ReadingPool.Role).WithSetting(PoolSettingKeys.Model, model.Key) : m
                : ReadingPool.HostOf(m, own) == hostId ? m.WithSetting(PoolSettingKeys.Model, model.Key) : m).ToList();
            var next = list with { Members = members };
            // Set up from Devices or another page: the place joins the list at its end.
            if (members.All(m => hostId is null ? m.Kind != PoolMemberKind.ThisPc : ReadingPool.HostOf(m, own) != hostId))
                next = next.With(hostId is null ? ReadingPool.ThisPcRole().WithSetting(PoolSettingKeys.Model, model.Key)
                    : PoolMember.Computer(hostId).WithSetting(PoolSettingKeys.Model, model.Key));
            return next;
        }, $"Reading is ready on {where} with {model.Name}.");
    }

    /// <summary>Sets the Reading role up on <paramref name="host"/> (null: this PC) with <paramref name="model"/>, or switches it
    /// to that model (<paramref name="changing"/>), after one confirmation naming its downloads, licences and what goes there,
    /// through the same martlet-host add as every role, then has Martlet read there.</summary>
    private async Task SetUpReadingAsync(PairedHost? host, OptionalExtras.ReadingRoleModel model, bool changing = false)
    {
        if (store is null || setupService is null || closing || readingPending is not null) return;
        var where = host is null ? "this PC" : host.HostId;
        var thisPc = ThisPcHost();
        var verb = changing ? "Switch" : "Set up";
        if (!ConfirmationDialog.Confirm(this,
                (changing ? $"Switch Reading on {where} to {model.Name}?"
                    : host is null && thisPc is null
                        ? machine.DockerInstalled ? $"Set up Reading with {model.Name} on {where} in Docker Desktop?"
                            : $"Install Docker Desktop, then set up Reading with {model.Name} on {where}?"
                        : $"Set up Reading with {model.Name} on {where}?") +
                "\n\n" + ReadingTerms(model, elsewhere: host is not null),
                $"{verb} Reading"))
            return;
        var answers = model.Answers();
        readingFailures.Remove(host?.HostId ?? ReadingThisPc);
        readingPending = host?.HostId ?? ReadingThisPc;
        RenderTab();
        try
        {
            if (host is not null)
            {
                var done = await RunHostActionAsync(host, changing ? HostAction.Change(HostRoles.Ocr) : HostRoles.Get(HostRoles.Ocr).Add, answers);
                if (closing) return;
                if (done is null) readingFailures[host.HostId] = $"{(changing ? "Switching" : "Setting")} it {(changing ? "" : "up ")}on {host.HostId} stopped; its run window has details.";
                else if (await WaitForReadingAsync(host, model.Model, lifetime.Token) is { } why)
                    readingFailures[host.HostId] = $"{(changing ? "The switch" : "Setup")} finished, but Martlet can't see {model.Name} on {host.HostId} yet ({why}), so check it in a minute.";
                else ReadingSetUpDone(host.HostId, model, host.HostId);
                return;
            }
            string? stopped = null;
            async Task<string> Continue(HostRunWindow run, PairedHost pc)
            {
                await InstallRoleHereAsync(run, pc, new(HostRoles.Ocr, "Reading", answers,
                    changing ? $"Switching Reading on this PC to {model.Name}..." : $"Installing Reading with {model.Name} on this PC...",
                    exit => $"Installing Reading stopped{(stopped is null ? $" (exit {exit})" : ": " + stopped.TrimEnd('.'))}; the output has details.")
                {
                    // Several graphics cards: the owner picks the one it reads on.
                    Questions = model.NeedsNvidia ? LocalRoleQuestions.Gpu : LocalRoleQuestions.None,
                    QuestionsStatus = "Checking this PC's graphics cards for Reading...",
                    Stopped = why => stopped ??= why,
                });
                run.Status("Checking Reading on this PC...");
                if (await WaitForReadingAsync(pc, model.Model, run.Token) is { } why)
                    throw new InvalidOperationException(stopped = $"Setup finished, but Martlet can't see {model.Name} yet ({why}), so check this PC in a minute.");
                Dispatcher.Invoke(() => ReadingSetUpDone(null, model, "this PC"));
                return $"Reading is ready on this PC with {model.Name}.";
            }
            var status = await RunRoleSetupHereAsync(thisPc, $"{verb} Reading on this PC", "Set up Reading on this PC", Continue,
                () => stopped is null ? "Reading wasn't set up; the run window has details." : $"Reading wasn't set up: {stopped}");
            if (!closing && status is not null)
            {
                ActionText.Text = status;
                if (!status.StartsWith("Reading is ready", StringComparison.Ordinal)) readingFailures[ReadingThisPc] = status;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or HostConnectionException || ClusterSync.IsHostFailure(error))
        {
            readingFailures[host?.HostId ?? ReadingThisPc] = error.Message;
            ActionText.Text = error.Message;
        }
        finally
        {
            readingPending = null;
            if (!closing && openTab is not null) RenderTab();
        }
    }

    /// <summary>After martlet-host added (or changed) the role, checks <paramref name="host"/> (about a minute) until its gateway
    /// offers the reading route with <paramref name="model"/> and its reader answers ready. Returns null once it does, else what it
    /// saw last; at once when the reader failed to load.</summary>
    private Task<string?> WaitForReadingAsync(PairedHost host, string model, CancellationToken token) => WaitForRoleAsync(host, async check =>
    {
        var offer = check.Offers?.GetValueOrDefault(HostRoles.Ocr);
        if (!string.IsNullOrEmpty(offer) && offer != model)
            return new($"it still reads with {OptionalExtras.ReadingModelOf(offer)?.Name ?? offer}");
        if (check.Routes?.Any(r => r.RouteId == HostConnection.OcrRouteId) != true || store is null) return new(check.Text);
        try
        {
            var state = await PoolScreenTextReader.StatusAsync(host, token);
            if (state == "ready") return null;
            if (state == "failed") return new("its reader failed to load; the Reading role's log on that computer says why", GiveUp: true);
            return new($"its reader is {state}");
        }
        catch (ScreenReadException error) { return new(error.Message); }
    }, token);

    // ---------- Read my screen now ----------

    /// <summary>Takes one full-size picture of the whole screen (kept only in memory) and reads it with the saved choice, then
    /// shows what it read, how long it took and the picture's size. With the Reading role, that picture goes to that computer.</summary>
    private async Task ReadScreenNowAsync()
    {
        if (store is null || readingTesting || closing) return;
        using var reader = ScreenReader.For(store.DataDirectory);
        if (reader is null)
        {
            readingTestState = "Reading is off.";
            RenderTab();
            return;
        }
        readingTesting = true;
        readingTestText = null;
        RenderTab();
        var glancer = new ScreenGlancer();
        ScreenFrame? frame = null;
        try
        {
            var result = await Task.Run(() => glancer.CaptureText(ScreenScope.ActiveScreen));
            frame = result.Frame;
            if (frame?.TakePixels() is not { } pixels)
            {
                readingTestState = result.Skip switch
                {
                    GlanceSkip.MartletInFront => "Martlet's own windows cover most of the screen and are never read, so make this " +
                        "window smaller or move it aside, then try again.",
                    GlanceSkip.Private => "A private window is in front, so Martlet won't read the screen.",
                    GlanceSkip.Blank => "The screen is black to Martlet (a locked screen, or protected video).",
                    _ => "Couldn't take a picture of the screen" + (result.Note is { } why ? $" ({why})." : ".")
                };
                return;
            }
            try
            {
                var read = await reader.ReadNowAsync(pixels, frame.Width, frame.Height, lifetime.Token);
                readingTestState = read.Describe();
                readingTestText = read.Text.Length > 0 ? read.Text : "(no text found)";
                ErrorLog.Info($"Reading: test read {read.Lines.Count} lines with {read.Engine} in {read.Took.TotalMilliseconds:0} ms.");
            }
            finally { Array.Clear(pixels); }
        }
        catch (ScreenReadException error) { readingTestState = error.Message; }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            readingTestState = $"Couldn't read the screen ({error.Message}).";
        }
        finally
        {
            frame?.Clear();
            Task.Run(glancer.Release).Forget();
            readingTesting = false;
            if (!closing && openTab == CompanionTab.Reading) RenderTab();
        }
    }
}
