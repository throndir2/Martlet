using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Avatar.Hosting;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;
using Martlet.Core.Pictures;
using Martlet.Core.Settings;
using Martlet.Credentials.Windows;
using Martlet.Presentation;
using Martlet.Providers.Pictures;

namespace Martlet.Desktop;

/// <summary>More of MainWindow.Pictures.cs, in a file of its own so that each file stays under 20 KB.</summary>
public partial class MainWindow
{
    /// <summary>Loads a ComfyUI Export (API) file as this place's own workflow (its own file in the data folder) and uses it there.</summary>
    private void LoadPicturesWorkflow(PoolMember member)
    {
        if (store is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Load a ComfyUI workflow (Export (API))", Filter = "ComfyUI API workflow (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > PicturesSettings.MaximumWorkflowBytes)
                throw new ContractException(ErrorCode.InvalidContract, "That workflow is too large (256 KB at most).");
            var workflow = JsonNode.Parse(File.ReadAllBytes(dialog.FileName)) as JsonObject ??
                throw new ContractException(ErrorCode.InvalidContract, "That file isn't a ComfyUI workflow.");
            // Fails clearly for a UI-format workflow (nodes/links) or one with nowhere to put the description.
            _ = ComfyWorkflows.Fill(workflow, new PictureRequest { Prompt = "test" }, 1);
            var file = PictureMakingPoolMembers.WorkflowFileFor(member);
            if (!PicturesSettings.SaveWorkflow(store.DataDirectory, workflow, file)) throw new IOException("Couldn't save the workflow in Martlet's data folder.");
            var list = PictureClient.List(store.DataDirectory);
            if (list.Find(member.Key) is not { } current) return;
            var next = PictureMakingPoolMembers.WithWorkflow(current, PictureWorkflow.Custom, null, file);
            SavePictureList(list.With(next), $"Loaded your workflow ({workflow.Count} nodes) for {PictureMakingPoolMembers.Describe(member)}; it draws with it now.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ContractException or PictureException)
        {
            ActionText.Text = error.Message;
        }
    }

    // ---------- cloud providers ----------

    /// <summary>Saves a pasted key as a cloud place's own (Windows Credential Manager; its reference in pool-keys.json) and
    /// deletes the key it had before.</summary>
    private async Task SavePictureKeyAsync(PoolMember member, PasswordBox box)
    {
        if (store is null || closing) return;
        var name = PictureMakingPoolMembers.ProviderName(member.Provider);
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        if (profile == Guid.Empty) { ActionText.Text = "Set up Martlet first, then add the key."; return; }
        if (PictureMakingPoolMembers.Place(member, null, null) is not { } place) return;
        using (var entered = box.SecurePassword)
            if (entered.Length == 0) { ActionText.Text = $"Paste your {name} API key first."; return; }
        var key = TakeKey(box);
        var vault = new WindowsCredentialStore();
        CredentialBinding? written = null;
        try
        {
            var id = Guid.NewGuid();
            var binding = place.Binding(profile, id);
            var error = await Task.Run(() => vault.Write(binding, key), lifetime.Token);
            if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
            written = binding;
            var keys = PoolKeys.Load(store.DataDirectory);
            var old = keys.For(PoolAreas.PictureMaking.Id, member.Key);
            if (!keys.With(PoolAreas.PictureMaking.Id, member.Key, id).Save(store.DataDirectory)) throw new InvalidOperationException("Couldn't save the key's reference on this PC.");
            written = null;
            if (old is { } oldId)
            {
                var oldBinding = place.Binding(profile, oldId);
                await Task.Run(() => vault.Delete(oldBinding), CancellationToken.None);
            }
            ErrorLog.Info($"Pictures: saved an own key for {PictureMakingPoolMembers.Describe(member)}.");
            ActionText.Text = $"Saved the {name} key for pictures in Windows Credential Manager.";
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException error) { ActionText.Text = error.Message; }
        finally
        {
            if (written is { } orphan) vault.Delete(orphan);
            key.Dispose();
            if (!closing && openTab == CompanionTab.Pictures) RenderTab();
        }
    }

    private async Task AddPictureCloudAsync(string provider, string model, PasswordBox keyBox, bool consent)
    {
        if (store is null || closing) return;
        var name = PictureMakingPoolMembers.ProviderName(provider);
        if (!consent)
        {
            ActionText.Text = picturesCloudState = $"Tick the box to agree, then press Add {name}.";
            RenderTab();
            return;
        }
        var directory = store.DataDirectory;
        var area = PoolAreas.PictureMaking.Id;
        var profile = homeSettings?.Profile.Id ?? Guid.Empty;
        var vault = new WindowsCredentialStore();
        SecretLease? key = null;
        CredentialBinding? written = null;
        Guid? id = null;
        try
        {
            ChatCompletionsSetup.ModelId(model);
            var member = PoolMember.Cloud(provider, model).WithConsent(area, DateTimeOffset.UtcNow);
            var list = PictureClient.List(directory);
            if (list.Find(member.Key) is not null)
                throw new ContractException(ErrorCode.InvalidContract, $"{PictureMakingPoolMembers.Describe(member)} is in the Picture making list already.");
            var place = PictureMakingPoolMembers.Place(member, null, null)!;
            var thinking = homeSettings.ThinkingRoute();
            using (var entered = keyBox.SecurePassword)
                if (entered.Length > 0) key = TakeKey(keyBox);
            // A key for the same provider saved anywhere else in Martlet is copied, so it is never asked for twice.
            string? borrowedFrom = null;
            if (key is null && !place.UsesThinkingKey(thinking) && SavedKeyWhere(place.Origin) is { } from &&
                await ReadSavedKeyAsync(place.Origin) is { } copy)
            {
                key = copy;
                borrowedFrom = from;
            }
            if (key is null && !place.UsesThinkingKey(thinking)) throw new ContractException(ErrorCode.InvalidContract, $"Paste your {name} API key first.");
            if (key is not null)
            {
                if (profile == Guid.Empty) throw new InvalidOperationException("Set up Martlet first, then add the key.");
                id = Guid.NewGuid();
                var binding = place.Binding(profile, id.Value);
                var lease = key;
                var error = await Task.Run(() => vault.Write(binding, lease), lifetime.Token);
                if (error != CredentialError.None) throw new InvalidOperationException(CredentialMessages.Describe(error));
                written = binding;
                if (!PoolKeys.Load(directory).With(area, member.Key, id).Save(directory))
                    throw new InvalidOperationException("Couldn't save the key's reference on this PC.");
            }
            if (!PictureClient.SaveList(directory, list.With(member)))
            {
                if (id is not null) PoolKeys.Load(directory).With(area, member.Key, null).Save(directory);
                throw new InvalidOperationException("Couldn't save the Picture making list on this PC.");
            }
            written = null;
            picturesCheck = null;
            var done = $"{PictureMakingPoolMembers.Describe(member)} is now last in the Picture making list" +
                (borrowedFrom is not null ? $", with your {name} key from {borrowedFrom}"
                    : key is null ? "" : ", with its API key in Windows Credential Manager") + "; move it up to draw there sooner.";
            ErrorLog.Info($"Pictures: added {PictureMakingPoolMembers.Describe(member)} to the Picture making list (the owner agreed to pay).");
            ActionText.Text = picturesCloudState = done;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is ContractException or InvalidOperationException)
        {
            ActionText.Text = picturesCloudState = error.Message;
        }
        finally
        {
            if (written is { } orphan) vault.Delete(orphan);
            key?.Dispose();
            if (!closing && openTab == CompanionTab.Pictures) RenderTab();
        }
    }

    // ---------- Martlet's Picture making role ----------

    /// <summary>Why this PC can't run the Picture making role: no NVIDIA graphics card, or one with less than 8 GB; null when it can or
    /// Martlet hasn't read this PC's hardware yet.</summary>
    private string? ThisPcCannotDraw()
    {
        if (ReferenceEquals(machine, MachineInfo.Unknown)) return null;
        if (machine.ArmRefusal("pictures") is { } arm) return arm.TrimEnd('.') + ": use another computer, your own ComfyUI or a cloud provider.";
        var nvidia = machine.Gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.MemoryGb ?? 0).FirstOrDefault();
        if (nvidia is null)
            return $"Needs an NVIDIA graphics card, and this PC has {(machine.Gpus.Count == 0 ? "none" : string.Join(", ", machine.Gpus.Select(g => g.Describe())))}: " +
                "use another computer, your own ComfyUI or a cloud provider.";
        return nvidia.MemoryGb is { } gb && gb < 7.5 ? $"Needs an NVIDIA graphics card with 8 GB+; this PC has {nvidia.Describe()}." : null;
    }

    /// <summary>Sets the pictures role up on <paramref name="host"/> (null: this PC) after one confirmation naming its downloads,
    /// licences and terms, through the same martlet-host add as every role; that place in the list then draws.</summary>
    private async Task SetUpPicturesAsync(PairedHost? host)
    {
        if (store is null || setupService is null || closing || picturesPending is not null) return;
        var where = host is null ? "this PC" : host.HostId;
        if (host is null && ThisPcCannotDraw() is { } cannot) { ActionText.Text = $"Picture making can't run on this PC: {char.ToLowerInvariant(cannot[0])}{cannot[1..]}"; return; }
        var thisPc = ThisPcHost();
        if (!ConfirmationDialog.Confirm(this,
                (host is null && thisPc is null
                    ? machine.DockerInstalled ? $"Set up Picture making on {where} in Docker Desktop?"
                        : $"Install Docker Desktop, then set up Picture making on {where}?"
                    : $"Set up Picture making on {where}?") +
                "\n\n" + PicturesTerms, "Set up Picture making"))
            return;
        var answers = new Dictionary<string, string>(StringComparer.Ordinal) { ["choice.PICTURES_MODEL"] = "z-image-turbo" };
        picturesFailure = null;
        picturesPending = picturesFailedOn = host?.HostId ?? PicturesThisPc;
        RenderTab();
        try
        {
            if (host is not null)
            {
                var done = await RunHostActionAsync(host, HostRoles.Get(HostRoles.Pictures).Add, answers);
                if (closing) return;
                if (done is null) picturesFailure = $"Setting it up on {host.HostId} stopped; its run window has details.";
                else if (await WaitForPicturesAsync(host, lifetime.Token) is { } why)
                    picturesFailure = $"Setup finished, but Martlet can't see Picture making on {host.HostId} yet ({why}), so check it in a minute.";
                else UsePicturesHost(host.HostId, host.HostId, thisPc: false);
                return;
            }
            string? stopped = null;
            async Task<string> Continue(HostRunWindow run, PairedHost pc)
            {
                await InstallRoleHereAsync(run, pc, new(HostRoles.Pictures, "Picture making", answers,
                    "Installing Picture making on this PC (a large download)...",
                    exit => $"Installing Picture making stopped{(stopped is null ? $" (exit {exit})" : ": " + stopped.TrimEnd('.'))}; the output has details.")
                    { Stopped = why => stopped ??= why });
                run.Status("Checking Picture making on this PC...");
                if (await WaitForPicturesAsync(pc, run.Token) is { } why)
                    throw new InvalidOperationException(stopped = $"Setup finished, but Martlet can't see Picture making yet ({why}), so check this PC in a minute.");
                Dispatcher.Invoke(() => UsePicturesHost(pc.HostId, "this PC", thisPc: true));
                return "Picture making is ready on this PC: ask Martlet to draw you something.";
            }
            var status = await RunRoleSetupHereAsync(thisPc, "Set up Picture making on this PC", "Set up Picture making on this PC", Continue,
                () => stopped is null ? "Picture making wasn't set up; the run window has details." : $"Picture making wasn't set up: {stopped}");
            if (!closing && status is not null)
            {
                ActionText.Text = status;
                if (!status.StartsWith("Picture making is ready", StringComparison.Ordinal)) picturesFailure = status;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ContractException or
            JsonException or ArgumentException or HostConnectionException || ClusterSync.IsHostFailure(error))
        {
            picturesFailure = error.Message;
            ActionText.Text = error.Message;
        }
        finally
        {
            picturesPending = null;
            if (!closing && openTab is not null) RenderTab();
        }
    }

    /// <summary>The Picture making role is ready on <paramref name="hostId"/>: its place in the list is on (added last when it isn't
    /// there yet).</summary>
    private void UsePicturesHost(string hostId, string where, bool thisPc)
    {
        if (store is null) return;
        var done = $"Picture making is ready on {where}: ask Martlet to draw you something.";
        var list = PictureClient.List(store.DataDirectory);
        var member = (thisPc ? list.Find(PoolMember.ThisPcKey) : null) ?? list.Find(PoolMember.Computer(hostId).Key);
        if (member is { Off: false })
        {
            ActionText.Text = done;
            ErrorLog.Info($"Pictures: the Picture making role is ready on {where}.");
            return;
        }
        member = member is null
            ? (thisPc ? PoolMember.ThisPc() : PoolMember.Computer(hostId)).WithSetting(PoolSettingKeys.Workflow, PictureMakingPoolMembers.ZImageTurbo)
            : member with { Off = false };
        SavePictureList(list.With(member), done);
    }

    /// <summary>After martlet-host added the role, checks <paramref name="host"/> (about a minute) until its gateway offers the
    /// pictures route and its ComfyUI answers ready. Returns null once it does, else what it saw last.</summary>
    private Task<string?> WaitForPicturesAsync(PairedHost host, CancellationToken token) => WaitForRoleAsync(host, async check =>
    {
        if (check.Routes?.Any(r => r.RouteId == HostConnection.PictureRouteId) != true || store is null) return new(check.Text);
        using var api = new GatewayComfyApi(store.DataDirectory, host.HostId);
        try
        {
            var status = await api.StatusAsync(token);
            return status["state"]?.ToString() == "ready" ? null : new($"its ComfyUI is {status["state"]}");
        }
        catch (PictureException error) { return new(error.Message); }
    }, token);

    // ---------- Check and Draw a test picture ----------

    /// <summary>Check asks each place in the list whether it can draw now (a cloud provider: only whether a key is there). Draw a
    /// test picture draws one picture where the list sends it and shows it here (it isn't kept); when the list has a cloud
    /// provider, it asks first, since a picture there costs money.</summary>
    private async Task CheckPicturesAsync(bool test)
    {
        if (store is null || picturesChecking || closing) return;
        if (test && PictureClient.FirstPaid(store.DataDirectory) is { } paid && !PictureClient.Fixture &&
            !ConfirmationDialog.Confirm(this, $"Draw a test picture, which may go to {paid} and cost money?", "Draw a test picture", "Draw", "Cancel"))
            return;
        var thinking = homeSettings.ThinkingRoute();
        var maker = PictureClient.For(store.DataDirectory, homeSettings?.Profile.Id ?? Guid.Empty, thinking);
        if (maker is null) return;
        picturesChecking = true;
        picturesTesting = test;
        picturesTestImage = null;
        RenderTab();
        try
        {
            if (!test)
            {
                IReadOnlyList<IPictureMaker> places = maker is PictureMakingPool pool ? [.. pool.Members.Select(m => m.Maker)] : [maker];
                List<string> lines = [];
                foreach (var place in places)
                {
                    var availability = await place.GetAvailabilityAsync(lifetime.Token);
                    lines.Add(availability.Available ? $"ready: {place.Where} can draw now" : (availability.Reason ?? $"{place.Where} can't draw now").TrimEnd('.'));
                }
                picturesCheck = Capitalized(string.Join("; ", lines)) + ".";
            }
            else
            {
                var availability = await maker.GetAvailabilityAsync(lifetime.Token);
                if (!availability.Available) picturesCheck = availability.Reason;
                else
                {
                    var result = await maker.GenerateAsync(new PictureRequest { Prompt = PicturesTestPrompt, Shape = PictureShape.Landscape }, null, lifetime.Token);
                    picturesTestImage = await Task.Run(() => PictureView.Decode(result.Image, 840));
                    picturesCheck = $"Drew a {result.Width}x{result.Height} test picture on {result.Where} in {result.Took.TotalSeconds:0.0} s" +
                        (result.Fixture ? " (FIXTURE - NOT AI)." : ".");
                    ErrorLog.Info($"Pictures: test picture on {result.Where}: {result.Width}x{result.Height} {result.MediaType}, {result.Image.Length / 1024} KiB, " +
                        $"{result.Took.TotalSeconds:0.0} s.");
                    PictureClient.FreeLater(maker);
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch (PictureException error)
        {
            picturesCheck = error.Message;
            ErrorLog.Info($"Pictures: {(test ? "test picture" : "check")} on {maker.Where} failed ({error.Code}: {error.Message}).");
        }
        finally
        {
            picturesChecking = false;
            (maker as IDisposable)?.Dispose();
            if (!closing && openTab == CompanionTab.Pictures) RenderTab();
        }
    }
}
