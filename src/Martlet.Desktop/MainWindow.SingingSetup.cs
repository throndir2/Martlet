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

/// <summary>Setting Singing up: its terms, the setup run on this PC or another computer, its progress and the wait for the role.</summary>
public partial class MainWindow
{
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
