using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Singing;

namespace Martlet.Desktop;

/// <summary>
/// Companion › Singing (an optional extra): Martlet writes a song from lyrics and a style and sings it with the voice it speaks
/// with, on the <c>singing</c> host role. A song has two parts, each with its own pool list (the shared list control,
/// docs/SINGING.md#the-two-singing-pools): the music (<see cref="PoolAreas.Singing"/>: ACE-Step 1.5 writes it with its own singer;
/// with none on, Martlet doesn't sing) and the voice match (<see cref="PoolAreas.VoiceMatch"/>: SoulX-Singer-SVC or VevoSing
/// matches the singing to the voice; with none on, songs keep the music model's own singer), which may be other computers. In
/// the standard order: Now; the two lists, where each member's Settings say where Singing stands there (with the voice matches
/// read from its singing service through the gateway), set the role up after a confirmation naming the downloads, licences and
/// terms, and choose that member's own quality (music: High quality by default, or Fast) or model (voice match: SoulX-Singer
/// by default, or VevoSing, added with its own confirmation); what Singing is (the role's facts); then the voice match models
/// compared. Songs are only ever performed by Martlet in conversation (through <see cref="SongMaker"/>), never played from a
/// button here. Automation IDs: <c>SingingNow</c>, the lists' <c>Pool-singing-...</c> and <c>Pool-voice-match-...</c> (and per
/// member <c>Pool-&lt;area&gt;-State-&lt;key&gt;</c>, <c>Pool-&lt;area&gt;-SetUp-&lt;key&gt;</c>,
/// <c>Pool-singing-Quality-&lt;key&gt;</c>, <c>Pool-singing-MusicModel-&lt;key&gt;</c>, <c>Pool-voice-match-Model-&lt;key&gt;</c>
/// and one <c>Pool-&lt;area&gt;-Add&lt;model&gt;-&lt;key&gt;</c> per model not set up there, from <see cref="SingingModels.AddOns"/>),
/// <c>Picker-Singing-Role</c>, <c>SingingGpu</c> and
/// <c>Picker-SingingVoiceMatch-&lt;SoulX|VevoSing&gt;</c>.
/// </summary>
public partial class MainWindow
{
    internal const string SingingThisPc = "this-pc";


    /// <summary>Whether Singing needs a graphics card of its own, in words (the GPU chip's tip and the card's note).</summary>
    internal const string SingingGpuNote =
        "It doesn't need a graphics card of its own. It shares one with the voice and listening, using 5-7 GB of it only while a " +
        "song is being made.";

    private string? singingPendingHost;
    /// <summary>The model being added on <see cref="singingPendingHost"/> (null: the Singing role itself).</summary>
    private SingingAddOn? singingPendingAddOn;
    /// <summary>Why the last setup failed, by computer (a host ID, or <see cref="SingingThisPc"/>): Singing, and each added model
    /// (by computer and the model's ID).</summary>
    private readonly Dictionary<string, string> singingFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, string AddOn), string> singingAddOnFailures = [];
    /// <summary>What each computer's singing service last reported through its gateway (the voice matches set up there), by
    /// host ID; null when it couldn't be read. Read when the page shows a computer that sings, and again after a setup.</summary>
    private readonly Dictionary<string, SongClient.SingingService?> singingServices = new(StringComparer.Ordinal);
    private readonly HashSet<string> singingReads = new(StringComparer.Ordinal);


    /// <summary>The song maker Martlet's conversation uses: <see cref="SongClient"/> on the singing pools, or the FIXTURE - NOT AI
    /// maker when MARTLET_SINGING_FIXTURE is 1; null before Martlet has its data directory.</summary>
    internal ISongMaker? SongMaker => store is null ? null : SongClient.For(store.DataDirectory);

    /// <summary>Saves one member's settings in one of Singing's lists (pools.json, shared with your other computers).</summary>
    private void SaveSingingMember(PoolArea area, PoolMember member, string done)
    {
        if (store is null) return;
        var list = WorkSharingRoster.Shared.Pool(store.DataDirectory, area) ?? new PoolList { Area = area.Id };
        if (!PoolSettings.SaveFor(store.DataDirectory, area, list.With(member)))
        {
            ActionText.Text = $"Couldn't save the {area.Title} list on this PC.";
            return;
        }
        QueueSettingsSync();
        ErrorLog.Info("Pools: " + done);
        ActionText.Text = done.TrimEnd('.') + "; your other computers follow it.";
        Dispatcher.InvokeAsync(() => { if (!closing && openTab == CompanionTab.Singing) RenderTab(); });
    }

    /// <summary>Makes the singing pool list once from Companion › Singing's older choice (the computer Martlet sang on, then
    /// your other computers that run Singing; all off when singing was off), so the choice isn't lost. Only when this PC sang on
    /// one of your computers: the list is shared, and an empty one made here would turn singing off on your other computers.</summary>
    private void EnsureSingingPool(SingingPreferences preferences)
    {
        if (store is null || WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.Singing) is not null) return;
        var own = NetworkMap.Hosts(Inputs()).Where(h => !h.Shared).ToArray();
        if (preferences.Host is not { } saved || own.All(h => h.HostId != saved)) return;
        var list = new PoolList { Area = PoolAreas.Singing.Id };
        foreach (var id in own.Where(h => h.HostId != saved && Offers(h, HostRoles.Singing)).Select(h => h.HostId).Prepend(saved))
            list = list.With(PoolMember.Computer(id) with { Off = preferences.Off });
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.Singing, list)) return;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: the Singing list was made from Companion › Singing's computer: {string.Join(", ", list.Members.Select(m => m.Name))}" +
            (preferences.Off ? " (all off: singing was off)." : "."));
    }

    /// <summary>Makes the voice match list once from the music list (the same computers, on and off alike, each with the voice
    /// match Companion › Singing chose before), so the computers that sang whole songs keep matching the voice. Until it is
    /// made, songs use the music list for their voice match too (<see cref="SongClient.Members"/>).</summary>
    private void EnsureVoiceMatchPool(SingingPreferences preferences)
    {
        if (store is null || WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.VoiceMatch) is not null ||
            WorkSharingRoster.Shared.Pool(store.DataDirectory, PoolAreas.Singing) is not { Members.Count: > 0 } music)
            return;
        var list = new PoolList
        {
            Area = PoolAreas.VoiceMatch.Id,
            Members = [.. music.Members.Select(m => SingingPool.WithVoiceMatch(m.WithSetting(SingingPool.QualitySetting, null), preferences.OlderVoiceMatch))]
        };
        if (!PoolSettings.SaveFor(store.DataDirectory, PoolAreas.VoiceMatch, list)) return;
        QueueSettingsSync();
        ErrorLog.Info($"Pools: the Voice match list was made from the Singing list: {string.Join(", ", list.Members.Select(m => m.Name))} " +
            $"({SingingPool.Name(preferences.OlderVoiceMatch)}).");
    }

    /// <summary>Saves a change to singing.json and shows <paramref name="done"/>; the next song uses it. A change of the main
    /// choice (<paramref name="choice"/>) shows the option in use again.</summary>
    private void SaveSinging(Func<SingingPreferences, SingingPreferences> change, string? done, bool choice = true)
    {
        if (store is null) return;
        try
        {
            var next = change(SingingPreferences.Load(store.DataDirectory));
            next.Save(store.DataDirectory);
            ErrorLog.Info($"Singing: {(next.Off ? "off" : "on")}, host {next.Host ?? "none"}.");
            if (done is not null) ActionText.Text = done;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ActionText.Text = $"Couldn't save the singing choices: {error.Message}";
            return;
        }
        if (choice) ForgetPicker("Singing");
        Dispatcher.BeginInvoke(() => { if (!closing && openTab == CompanionTab.Singing) RenderTab(); });
    }
    private bool Offers(PairedHost host, string kind) => hostChecks.GetValueOrDefault(host.HostId)?.Offers?.ContainsKey(kind) == true;

    /// <summary>Reads <paramref name="host"/>'s singing service through its gateway (the voice matches set up there) for the
    /// card, then shows the card again.</summary>
    private async Task ReadSingingServiceAsync(PairedHost host)
    {
        if (!singingReads.Add(host.HostId)) return;
        SongClient.SingingService? read = null;
        try { read = await SongClient.ReadServiceAsync(host.Pairing, lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is JsonException)
        {
            ErrorLog.Info($"Singing: couldn't read the singing service on {host.HostId} ({error.Message}).");
        }
        finally { singingReads.Remove(host.HostId); }
        singingServices[host.HostId] = read;
        if (!closing && openTab == CompanionTab.Singing && !CompanionContent.IsKeyboardFocusWithin) RenderTab();
    }

    /// <summary>Remembers in singing.json which computer runs Singing after a check of the paired computers (kept while that
    /// computer can't be reached, cleared once it answers without Singing), for <see cref="SongClient.IsSetUp"/>.</summary>
    private void NoteSingingHost()
    {
        if (store is null) return;
        try
        {
            var saved = SingingPreferences.Load(store.DataDirectory);
            var running = hostChecks.Where(c => c.Value.Reachable == true && c.Value.Offers?.ContainsKey(HostRoles.Singing) == true)
                .Select(c => c.Key).ToList();
            var next = saved.Host is { } host && (running.Contains(host) ||
                hostChecks.GetValueOrDefault(host)?.Reachable != true && running.Count == 0)
                ? host
                : running.FirstOrDefault();
            if (next != saved.Host) (saved with { Host = next }).Save(store.DataDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Warn($"Couldn't remember the singing computer: {error.Message}");
        }
    }

    /// <summary>Why this PC can't sing: no NVIDIA graphics card, or one with less than 6 GB; null when it can or Martlet
    /// hasn't read this PC's hardware yet.</summary>
    private string? ThisPcCannotSing()
    {
        if (ReferenceEquals(machine, MachineInfo.Unknown)) return null;
        if (machine.ArmRefusal("singing") is { } arm) return arm;
        var nvidia = machine.Gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.MemoryGb ?? 0).FirstOrDefault();
        if (nvidia is null)
            return $"Needs an NVIDIA graphics card; this PC has {(machine.Gpus.Count == 0 ? "none" : string.Join(", ", machine.Gpus.Select(g => g.Describe())))}.";
        return nvidia.MemoryGb is { } gb && gb < 5.75 ? $"Needs an NVIDIA graphics card with 6 GB+; this PC has {nvidia.Describe()}." : null;
    }


    /// <summary>What setting Singing up downloads and agrees to, in one sentence for its confirmation (role.conf's terms in short;
    /// the wiki's Singing#consent-and-licenses has every model and licence).</summary>
    internal static string SingingTerms() =>
        "It needs an NVIDIA graphics card with 6 GB+ and downloads about 16 GB of open models (MIT and Apache-2.0), and it may only " +
        "sing in voices that are yours or that you have permission to use.";

    /// <summary>Sets the singing role up on <paramref name="host"/> (null: this PC), or adds a model (<paramref name="addOn"/>) where it
    /// runs, after one confirmation naming the downloads, licences and terms, through the same martlet-host add as every role. A
    /// plain Set up installs ACE-Step 1.5 and SoulX-Singer only; an addition keeps what the computer already has. Progress shows in
    /// the run window; the card says setting up, ready (with what is set up there) or failed and why.</summary>
    private async Task SetUpSingingAsync(PairedHost? host, SingingAddOn? addOn)
    {
        if (store is null || setupService is null || closing) return;
        // The Singing card follows one setup at a time (what it shows while setting up); everything else goes ahead meanwhile.
        if (singingPendingHost is not null)
        {
            ActionText.Text = $"Singing is still being set up on {(singingPendingHost == SingingThisPc ? "this PC" : singingPendingHost)}; " +
                "set up the next one when its window says it's done.";
            return;
        }
        var where = host is null ? "this PC" : host.HostId;
        if (host is null && ThisPcCannotSing() is { } cannot)
        {
            ActionText.Text = $"Singing can't run on this PC: {char.ToLowerInvariant(cannot[0])}{cannot[1..]}";
            return;
        }
        var thisPc = ThisPcHost();
        var confirmed = addOn is not null
            ? ConfirmationDialog.Confirm(this, $"Add {addOn.Name} to Singing on {where}?\n\n{addOn.Terms}", $"Add {addOn.Name}")
            : ConfirmationDialog.Confirm(this,
                (host is null && thisPc is null
                    ? machine.DockerInstalled ? $"Set up Singing on {where} in Docker Desktop?"
                        : $"Install Docker Desktop, then set up Singing on {where}?"
                    : $"Set up Singing on {where}?") +
                "\n\n" + SingingTerms(),
                "Set up Singing");
        if (!confirmed) return;
        // An addition keeps every model the computer already has.
        var service = (host ?? thisPc) is { } known ? singingServices.GetValueOrDefault(known.HostId) : null;
        var answers = SingingModels.Choices(a => a.Music is { } m ? service?.Has(m) == true : service?.Has(a.Match!.Value) == true, addOn)
            .ToDictionary(c => "choice." + c.Key, c => c.Value, StringComparer.Ordinal);
        var key = host is null || host.HostId == thisPc?.HostId ? SingingThisPc : host.HostId;
        void Failed(string why)
        {
            if (addOn is null) singingFailures[key] = why;
            else singingAddOnFailures[(key, addOn.Id)] = why;
        }
        if (addOn is null) singingFailures.Remove(key);
        else singingAddOnFailures.Remove((key, addOn.Id));
        singingPendingHost = key;
        singingPendingAddOn = addOn;
        var what = addOn?.Name ?? "Singing";
        RenderTab();
        if (host is not null)
        {
            try
            {
                var done = await RunHostActionAsync(host, HostRoles.Get(HostRoles.Singing).Add, answers);
                if (closing) return;
                singingServices.Remove(host.HostId);
                if (done is null) Failed($"Setting it up on {host.HostId} stopped; its run window has details.");
                else if (await WaitForSingingAsync(host, addOn, lifetime.Token) is { } why)
                    Failed($"Setup finished, but Martlet can't see {what} on {host.HostId} yet ({why}); check again in a minute.");
                else
                {
                    // Setting the role up from the page's main choice uses it: Martlet sings there (and isn't off any more).
                    if (addOn is null) SaveSinging(p => p with { Off = false, Host = host.HostId }, null);
                    ActionText.Text = addOn is null ? $"Singing is ready on {host.HostId}: ask Martlet to sing you a song."
                        : $"{what} is ready on {host.HostId}.";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is JsonException) { Failed(error.Message); }
            finally
            {
                singingPendingHost = null;
                singingPendingAddOn = null;
                if (!closing && openTab is not null) RenderTab();
            }
            return;
        }
        try
        {
            // Why martlet-host stopped ("Stopped: ..."), for the card when the run fails.
            string? stopped = null;
            async Task<string> Continue(HostRunWindow run, PairedHost pc)
            {
                await InstallRoleHereAsync(run, pc, new(HostRoles.Singing, what, answers,
                    addOn is not null ? $"Adding {addOn.Name} on this PC ({addOn.Download})..." : "Installing Singing on this PC (a large download)...",
                    exit => $"Installing {what} stopped{(stopped is null ? $" (exit {exit})" : ": " + stopped)}. The output has details.")
                    { Stopped = why => stopped ??= why });
                run.Status($"Checking {what} on this PC...");
                singingServices.Remove(pc.HostId);
                if (await WaitForSingingAsync(pc, addOn, run.Token) is { } why)
                {
                    stopped = $"Setup finished, but Martlet can't see {what} yet ({why}); check this PC again in a minute.";
                    throw new InvalidOperationException(stopped);
                }
                if (addOn is null) Dispatcher.Invoke(() => SaveSinging(p => p with { Off = false, Host = pc.HostId }, null));
                return addOn is null ? "Singing is ready on this PC: ask Martlet to sing you a song." : $"{what} is ready on this PC.";
            }

            var status = await RunRoleSetupHereAsync(thisPc, addOn is null ? "Set up Singing on this PC" : $"Add {what} on this PC",
                "Set up Singing on this PC", Continue,
                () => stopped is null ? $"{what} wasn't set up; the run window has details." : $"{what} wasn't set up: {stopped}");
            if (!closing && status is not null)
            {
                ActionText.Text = status;
                if (!status.StartsWith($"{what} is ready", StringComparison.Ordinal)) Failed(status);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or HostConnectionException)
        {
            Failed(error.Message);
            ActionText.Text = error.Message;
        }
        finally
        {
            singingPendingHost = null;
            singingPendingAddOn = null;
        }
        if (!closing && openTab is not null) RenderTab();
    }
    /// <summary>martlet-host's output for the run window, with the singing role's own progress (the image build's steps, the
    /// service starting, each pinned model file's download) as its status line, so the window (and MCP, through
    /// HostRunStatus) shows how far setting up is; <paramref name="stopped"/> gets why martlet-host stopped, if it does.</summary>
    private static IProgress<string> SetupProgress(HostRunWindow run, string what, Action<string> stopped) => new Progress<string>(line =>
    {
        run.Output.Report(line);
        if (line.StartsWith("Stopped: ", StringComparison.Ordinal)) stopped(line["Stopped: ".Length..].Trim());
        else if (SingingDownloadPattern().Match(line) is { Success: true } download)
            run.Status($"{what}: downloading {download.Groups["file"].Value}, {download.Groups["percent"].Value}% of {download.Groups["mib"].Value} MiB...");
        else if (SingingModelPattern().Match(line) is { Success: true } model)
            run.Status($"{what}: getting {model.Groups["file"].Value}...");
        else if (SingingBuildPattern().Match(line) is { Success: true } step)
            run.Status($"{what}: building the singing image (step {step.Groups["step"].Value} of {step.Groups["of"].Value})...");
        else if (line.Contains("Waiting for singing", StringComparison.Ordinal))
            run.Status($"{what}: starting the singing service...");
    });

    [GeneratedRegex(@"^(?<file>[\w.-]+): (?<percent>\d{1,3})% of (?<mib>\d+) MiB$")]
    private static partial Regex SingingDownloadPattern();

    [GeneratedRegex(@"^Model file (?<file>\S+) \(")]
    private static partial Regex SingingModelPattern();

    [GeneratedRegex(@"^#\d+ \[singing[^\]]*? (?<step>\d+)/(?<of>\d+)\]")]
    private static partial Regex SingingBuildPattern();

    /// <summary>After martlet-host added the role, checks <paramref name="host"/> (about a minute) until its gateway offers the
    /// singing route and its singing service answers set up (with the model <paramref name="addOn"/> adds, if any).
    /// Returns null once it does, else what it saw last.</summary>
    private Task<string?> WaitForSingingAsync(PairedHost host, SingingAddOn? addOn, CancellationToken token) => WaitForRoleAsync(host, async check =>
    {
        NoteSingingHost();
        if (check.Routes?.Any(r => r.RouteId == HostConnection.SongRouteId) != true) return new(check.Text);
        try
        {
            var service = await SongClient.ReadServiceAsync(host.Pairing, token);
            singingServices[host.HostId] = service;
            var added = addOn is null || (addOn.Music is { } music ? service?.Has(music) == true : service?.Has(addOn.Match!.Value) == true);
            if (service is { State: not "not_provisioned" } && added) return null;
            return new(service is null ? "it offers no singing route" : !added
                ? $"its singing service doesn't list {addOn!.Name}"
                : $"its singing service is {service.State}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is JsonException) { return new(error.Message); }
    }, token);
}
