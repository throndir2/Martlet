using System.IO;
using System.Text.Json;
using Martlet.HostClient;
using Martlet.Core.Contracts;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Providers;
using Martlet.Providers.Ollama;

namespace Martlet.Desktop;

/// <summary>The changes Companion › Thinking pool saves: thinking-pool.json (members, their rules, Runs on, Backup for slow
/// replies, Busy pool), the members' API keys in Windows Credential Manager, the Thinking pool role on a paired computer, and
/// Thinking longer and Web research in the reply settings. Each change tells the owner in the status line and draws the page
/// again; none throws.</summary>
internal sealed partial class ThinkingPoolPageModel
{
    /// <summary>Saves thinking-pool.json changed by <paramref name="change"/> and reloads the conversation's pool.</summary>
    internal async Task SavePoolAsync(Func<ThinkingPoolSettings, ThinkingPoolSettings> change, string done)
    {
        if (services.DataDirectory is not { } directory || services.Closing) return;
        try
        {
            var next = change(ThinkingPoolSettings.Load(directory));
            if (!next.Save(directory)) throw new InvalidOperationException("Couldn't save the Thinking pool; check access to Martlet's data folder.");
            services.ReloadThinkingPool();
            ErrorLog.Info($"Thinking pool: {done}");
            services.ShowStatus(done);
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            services.ShowStatus(error.Message);
        }
        await Task.CompletedTask;
        services.RedrawIfOpen();
    }

    /// <summary>Removes a member from the pool; its own key (an endpoint's) is deleted from Windows Credential Manager.</summary>
    internal async Task RemovePoolMemberAsync(string key, string name)
    {
        if (services.DataDirectory is not { } directory || services.Closing) return;
        var old = ThinkingPoolSettings.Load(directory);
        var gone = old.All.FirstOrDefault(m => m.Key == key);
        // A paired computer stays out until it is ticked again; otherwise it would join again by itself at its next check.
        if (gone is { Place: DeepThinkingPlace.Host, HostId: { } hostId })
            await SavePoolAsync(p => p.TakeOut(hostId), $"{name} left the Thinking pool and stays out; tick In the pool to add it again.");
        else await SavePoolAsync(p => p.Remove(key), $"{name} left the Thinking pool.");
        var profile = services.Settings?.Profile.Id ?? Guid.Empty;
        if (gone?.CredentialId is { } oldKey && profile != Guid.Empty &&
            ThinkingPoolSettings.Load(directory).All.All(m => m.CredentialId != oldKey))
        {
            var binding = gone.Binding(profile, oldKey);
            await Task.Run(() => new WindowsCredentialStore().Delete(binding), CancellationToken.None);
        }
    }

    // ---------- the Thinking pool role on a paired computer ----------

    /// <summary>Sets a paired computer's Thinking pool model on graphics card <paramref name="card"/> to <paramref name="model"/>
    /// with <paramref name="slots"/> jobs at once, in a run window (the role's own settings there). Null slots: what fits for the
    /// new model, at most <paramref name="before"/>. Once it is done, the next check of that computer follows the new model and slots.</summary>
    internal async Task ChangeDeepRoleAsync(PairedHost host, int card, string model, int? slots, int before = int.MaxValue)
    {
        if (services.Closing) return;
        var role = SelfHostSetup.DeepThinkingRoleKind(card);
        var fit = DeepThinkingFit.Recommend(services.HardwareOf(host.HostId), services.CheckOf(host.HostId)?.Offers, card);
        var fits = fit.TryGetValue($"choice.{DeepThinkingFit.SlotsVariable}@{DeepThinkingFit.ModelVariable}={model}", out var said) &&
            int.TryParse(said.Value, System.Globalization.CultureInfo.InvariantCulture, out var most) ? most : 1;
        var count = slots ?? Math.Max(1, Math.Min(fits, before));
        var answers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["choice." + DeepThinkingFit.ModelVariable] = model,
            ["choice." + DeepThinkingFit.SlotsVariable] = count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        ErrorLog.Info($"Thinking pool: changing {host.HostId}'s graphics card {card} to {model} with {count} slot{(count == 1 ? "" : "s")}.");
        await services.RunHostActionAsync(host, HostAction.Change(role), answers);
        // Put the row back as it is now: a run that stopped changed nothing; one that finished shows after the computer's check.
        services.RedrawIfOpen();
    }

    /// <summary>Installs the Deep thinking role on a paired computer (its own run window, with the model choice) and, once it
    /// runs, thinks there. With <paramref name="card"/> 2 to 4 it installs the Thinking pool model for that graphics card
    /// (deep-thinking-2...), which joins as a member of its own.</summary>
    internal async Task AddDeepRoleAsync(PairedHost host, int card = 1)
    {
        if (services.Closing) return;
        var kind = SelfHostSetup.DeepThinkingRoleKind(card);
        if (services.CannotHand(host.HostId, kind, "deep thinking") is { } cannot)
        {
            services.ShowStatus($"The Thinking pool role can't be set up on {host.HostId}: {cannot}");
            return;
        }
        if (await services.RunHostActionAsync(host, HostRoles.Get(kind).Add) is null || services.Closing) return;
        await UseDeepHostAsync(host);
    }

    /// <summary>Thinks on <paramref name="host"/>: in place of where it thinks now, or with <paramref name="alsoHere"/> as one more
    /// place, so several thinks run at once (where it thinks now becomes this computer when it can't think there). The owner
    /// asked for it, so the computer is no longer kept out: when it doesn't answer now, it joins by itself once it does.</summary>
    internal async Task UseDeepHostAsync(PairedHost host, bool alsoHere = false)
    {
        if (services.Closing) return;
        var directory = services.DataDirectory;
        if (directory is not null && ThinkingPoolSettings.Read(directory) is ({ } kept, not "unreadable") && kept.Left(host.HostId))
        {
            try
            {
                if (kept.KeepOut(host.HostId, false).Save(directory))
                    ErrorLog.Info($"Thinking pool: {host.HostId} is no longer kept out.");
            }
            catch (ContractException) { }
        }
        services.ShowStatus($"Checking {host.HostId}...");
        try
        {
            var check = await services.CheckHostAsync(host);
            if (check.Reachable != true)
            {
                services.ShowStatus($"{host.HostId} didn't join the Thinking pool: it didn't respond ({check.Text}).");
                services.Redraw();
                return;
            }
            // Its own Thinking pool role first; otherwise its Ollama, when that doesn't do Thinking for the conversation.
            var seen = MainWindow.PoolHost(host, check.Routes ?? []);
            if (ThinkingPoolAutoJoin.Route(seen, NetworkMap.ThinkingHost(services.Settings)) is not { } route)
            {
                services.ShowStatus(seen.Offers.Count > 0
                    ? $"{host.HostId}'s Ollama does Thinking for the conversation; add the Thinking pool role there and it joins by itself."
                    : $"{host.HostId} doesn't run the Thinking pool role yet; add the role there and it joins by itself.");
                services.Redraw();
                return;
            }
            // A card the owner turned off stays off: only the others join (or follow the computer's role).
            var current = directory is null ? new ThinkingPoolSettings() : ThinkingPoolSettings.Load(directory);
            var cards = ThinkingPoolAutoJoin.Members(seen, NetworkMap.ThinkingHost(services.Settings), DateTimeOffset.Now)
                .Where(m => !current.IsOff(m.Key)).ToArray();
            if (cards.Length == 0)
            {
                services.ShowStatus($"{host.HostId} is turned off in the Thinking pool; tick On on its row to use it again.");
                services.Redraw();
                return;
            }
            var next = cards[0];
            // A host with a Thinking pool model on each of several graphics cards joins with one member per card.
            if (directory is not null && cards.Length > 1)
            {
                var pool = cards.Aggregate(current, (p, m) =>
                    p.Members.Any(x => x.Key == m.Key) || p.Members.Count < DeepThinkingSettings.MaxPlaces ? p.Add(m) : p);
                try { pool.Save(directory); }
                catch (ContractException) { }
            }
            await SaveDeepThinkingAsync(next, null, next.OnHostRole ? $"{host.HostId}'s Thinking pool role ({next.ModelId}) joined the Thinking pool."
                : $"{host.HostId} ({next.ModelId}) joined the Thinking pool.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or ContractException or HostConnectionException)
        {
            services.ShowStatus(error.Message);
        }
    }

    // ---------- a model in Ollama on this PC, a cloud provider or server ----------

    /// <summary>Checks whether <paramref name="deep"/> fits beside Thinking's <paramref name="thinking"/> in Ollama on this PC
    /// (read-only: it loads nothing). Null when the window closed meanwhile.</summary>
    internal async Task<SideBySideFit?> CheckLocalFitAsync(string thinking, string deep)
    {
        try
        {
            var fit = await LocalDeepThinking.CheckAsync(thinking, deep, loadThinking: false, services.Lifetime);
            ErrorLog.Info($"Thinking pool: {deep} {(fit.Fits ? "fits" : "doesn't fit")} beside {thinking} in Ollama on this PC. {fit.Why}");
            return fit;
        }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>Adds <paramref name="id"/> in Ollama on this PC to the pool.</summary>
    internal Task AddLocalAsync(string id) =>
        SaveDeepThinkingAsync(new() { Place = DeepThinkingPlace.Endpoint, Origin = MainWindow.LocalOllamaBaseUrl, ModelId = id, ChosenAt = DateTimeOffset.Now },
            null, $"{id} in Ollama on this PC joined the Thinking pool" +
            (services.OllamaModels is { } known && !LocalOllama.Serves(known, id) ? $"; download {id} on Thinking to use it." : "."));

    /// <summary>Adds a cloud provider or any OpenAI-compatible server (HTTPS, or a server on this PC) to the list, with its own key
    /// (<paramref name="takeKey"/> takes it from the form, or null when none was pasted) or Thinking's. A provider already in the
    /// list (<paramref name="members"/>, on or off) with a saved key uses that key again unless a new one is pasted, so a second
    /// model there needs no key.</summary>
    internal async Task SaveDeepCloudAsync(MainWindow.CloudProvider provider, string url, string model, Func<SecretLease?> takeKey, bool consent,
        IReadOnlyList<DeepThinkingSettings> members, SetupRoute? thinking)
    {
        if (!consent)
        {
            services.ShowStatus($"Tick the box to confirm {provider.Name} for the Thinking pool, then press Add to the Thinking pool.");
            return;
        }
        SecretLease? key = null;
        try
        {
            _ = ChatCompletionsSetup.BaseUri(url);
            ChatCompletionsSetup.ModelId(model);
            key = takeKey();
            var keep = SavedKey(members, url)?.CredentialId;
            var next = new DeepThinkingSettings
            {
                Place = DeepThinkingPlace.Endpoint, Origin = url, ModelId = model, CredentialId = keep, ChosenAt = DateTimeOffset.Now
            };
            // A key for the same provider saved anywhere else in Martlet is copied, so it is never asked for twice.
            if (key is null && keep is null && !next.UsesThinkingKey(thinking) &&
                SavedProviderKeys.For(services.Settings, services.DataDirectory, url) is { Count: > 0 } elsewhere)
                key = await Task.Run(() => SavedProviderKeys.Read(new WindowsCredentialStore(), elsewhere));
            if (key is null && provider.NeedsKey && keep is null && !next.UsesThinkingKey(thinking))
                throw new ContractException(ErrorCode.InvalidContract, $"Paste your {provider.Name} API key first.");
            await SaveDeepThinkingAsync(next, key, $"{provider.Name} ({model}) joined the Thinking pool" +
                (key is null ? "." : ", and its API key is saved in Windows Credential Manager."));
        }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            services.ShowStatus(error.Message);
        }
        finally { key?.Dispose(); }
    }

    /// <summary>Adds <paramref name="next"/> to the Thinking pool on this PC (thinking-pool.json), or replaces the member for the
    /// same computer or the same endpoint and model. A new key is written to Windows Credential Manager first and removed again
    /// if the file can't be saved; a key no member uses any more is removed after. The next job uses it.</summary>
    internal async Task SaveDeepThinkingAsync(DeepThinkingSettings next, SecretLease? key, string done)
    {
        if (services.DataDirectory is not { } directory || services.Closing) return;
        var profile = services.Settings?.Profile.Id ?? Guid.Empty;
        var vault = new WindowsCredentialStore();
        var old = ThinkingPoolSettings.Load(directory);
        CredentialBinding? written = null;
        try
        {
            if (key is not null)
            {
                if (profile == Guid.Empty) throw new InvalidOperationException("Set up Martlet first, then add the key.");
                next = next with { CredentialId = Guid.NewGuid() };
                var binding = next.Binding(profile, next.CredentialId!.Value);
                var lease = key;
                var error = await Task.Run(() => vault.Write(binding, lease), services.Lifetime);
                if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
                written = binding;
            }
            var pool = old.Add(next);
            if (!pool.Save(directory)) throw new InvalidOperationException("Couldn't save the Thinking pool; check access to Martlet's data folder.");
            written = null;
            foreach (var gone in old.All.Where(m => m.CredentialId is { } k && pool.All.All(p => p.CredentialId != k)))
                if (profile != Guid.Empty)
                {
                    var oldBinding = gone.Binding(profile, gone.CredentialId!.Value);
                    await Task.Run(() => vault.Delete(oldBinding), CancellationToken.None);
                }
            services.ReloadThinkingPool();
            var plan = pool.Plan(services.Settings?.Setup?.Routes ?? [], WorkSharingRoster.Shared.Settings(services.DataDirectory), WorkSharingRoster.Device).Plan;
            ErrorLog.Info($"Thinking pool: now {pool.Places.DescribeAll()}; {(plan.Available ? "in parallel with the conversation" : "can't run there")}. {plan.Why}");
            var turnedOn = !ThinkLongerSettings.Of(services.Settings?.Generation).On && await SetThinkLongerAsync(true);
            services.ShowStatus(done.TrimEnd('.') + (turnedOn ? ", and Thinking longer is on again" : "") +
                (plan.Available ? "." : "; " + plan.Why));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or JsonException)
        {
            services.ShowStatus(error.Message);
        }
        finally
        {
            if (written is { } orphan) vault.Delete(orphan);
            services.RedrawIfOpen();
        }
    }

    // ---------- Thinking longer and Web research (saved with the reply settings) ----------

    /// <summary>Thinking longer's on/off (ThinkLongerOn), saved with the reply settings (your computers share
    /// it). Shows <paramref name="done"/> once saved.</summary>
    internal async Task TurnDeepThinkingAsync(bool on, string done)
    {
        if (services.Closing) return;
        if (await SetThinkLongerAsync(on))
        {
            ErrorLog.Info($"Thinking pool: thinking longer turned {(on ? "on" : "off")}.");
            services.ShowStatus(done.TrimEnd('.') + "; reload an open conversation to use it.");
        }
        services.RedrawIfOpen();
    }

    /// <summary>Saves whether Martlet may think longer into the newest reply settings, trying again shortly while another change
    /// holds them. True once it is saved that way.</summary>
    internal async Task<bool> SetThinkLongerAsync(bool on)
    {
        for (var attempt = 0; attempt < 20 && !services.Closing; attempt++)
        {
            if (await services.SaveRepliesAsync(loaded => GenerationSettings.Normalize((loaded ?? new()) with
                {
                    ThinkLonger = ThinkLongerSettings.Normalize((loaded?.ThinkLonger ?? new()) with { Enabled = on })
                }), _ => { }, on ? "Thinking longer is on." : "Thinking longer is off."))
                return ThinkLongerSettings.Of(services.Settings?.Generation).On == on;
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), services.Lifetime); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    /// <summary>Saves How hard and Share it (<paramref name="read"/> reads them from the card when the save runs) into the newest
    /// saved reply settings; whether Martlet may think longer stays as saved. <paramref name="saved"/> gets Thinking longer's
    /// settings once they are saved.</summary>
    internal Task<bool> SaveThinkLongerChoicesAsync(Func<(ThinkEffort Effort, ThinkDelivery When)> read, Action<ThinkLongerSettings?> saved)
    {
        ThinkLongerSettings? next = null;
        return services.SaveRepliesAsync(loaded =>
        {
            var (effort, when) = read();
            next = ThinkLongerSettings.Normalize(new()
            {
                Enabled = loaded?.ThinkLonger?.Enabled,
                WebResearch = loaded?.ThinkLonger?.WebResearch,
                Effort = effort,
                Delivery = when
            });
            return GenerationSettings.Normalize((loaded ?? new()) with { ThinkLonger = next });
        }, generation =>
        {
            saved(generation?.ThinkLonger);
            var now = next ?? new();
            ErrorLog.Info($"Thinking longer: {now.HowHard} effort, no time or hourly limit, " +
                $"shares {(now.When == ThinkDelivery.WhenFree ? "as soon as Martlet is free." : "when you talk next.")}");
        }, "Thinking longer saved; reload an open conversation to use it.");
    }

    /// <summary>Saves whether Martlet may research on the web into the newest reply settings (your computers share it).</summary>
    internal async Task SetWebResearchAsync(bool on)
    {
        if (services.Closing) return;
        var saved = false;
        for (var attempt = 0; attempt < 20 && !services.Closing && !saved; attempt++)
        {
            saved = await services.SaveRepliesAsync(loaded => GenerationSettings.Normalize((loaded ?? new()) with
            {
                ThinkLonger = ThinkLongerSettings.Normalize((loaded?.ThinkLonger ?? new()) with { WebResearch = on })
            }), _ => { }, on ? "Web research is on; reload an open conversation to use it." : "Web research is off.");
            if (!saved)
                try { await Task.Delay(TimeSpan.FromMilliseconds(250), services.Lifetime); }
                catch (OperationCanceledException) { return; }
        }
        if (saved) ErrorLog.Info($"Web research: turned {(on ? "on" : "off")}.");
        services.RedrawIfOpen();
    }
}
