using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using System.Windows;
using Martlet.Audio;
using Martlet.Avatar.Audio2Face;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Avatar.Hosting;
using Martlet.Avatars;
using Martlet.Conversation;
using Martlet.Core.Cluster;
using Martlet.Core.Contracts;

namespace Martlet.Desktop;

/// <summary>The renderer's lifecycle: showing, inspecting and activating (one attempt at a time, a newer one replaces an older one), handing lip-sync to a host, and hiding.</summary>
internal sealed partial class AvatarController
{
    /// <summary>Opens the character with idle animation and the profile's lip-sync mode.</summary>
    internal async Task ShowAsync(AvatarProfile selected, CancellationToken token)
    {
        await InspectAsync(selected, token);
        await changes.WaitAsync(token);
        try
        {
            if (renderer is not { HasExited: false } current || profile is null)
                throw new InvalidOperationException("The character window closed before it finished loading.");
            if (selected.LipSync == AvatarLipSync.Audio2Face)
            {
                Publish("Character is showing; review and activate Audio2Face below to start lip-sync.");
                return;
            }
            Audio2FaceMapping? automatic = null;
            if (selected.LipSync == AvatarLipSync.Auto)
            {
                automatic = AutoConfiguration(profile);
                if (automatic is not null) await current.SendAsync("configure", automatic.Config, token);
                if (automatic is not null && selected.RemoteHost is { } remote)
                {
                    try { Volatile.Write(ref hostLink, openHost(remote)); }
                    catch (Exception error) when (error is Audio2FaceHostException or ContractException) { }
                }
                if (automatic is not null) lipSyncPool.Warm(profile, hostLink);
            }
            StartCharacter(current, automatic);
            if (automatic is null)
                Publish(selected.LipSync == AvatarLipSync.Auto
                    ? "Character is showing, but no compatible mouth controls were found."
                    : "Character is showing, and its mouth follows Martlet's voice.");
            else if (hostLink is { } assigned && await assigned.ReadyAsync(token))
                Publish($"Character is showing, with lip-sync from host {assigned.Authority}.");
            else if (await Audio2FaceProbe.IsListeningAsync(automatic.Options, TimeSpan.FromMilliseconds(300), token))
                Publish($"Character is showing, with lip-sync from Audio2Face at {automatic.Options.Endpoint.Authority}.");
            else if (hostLink is { } host)
                Publish($"Character is showing; host {host.Authority} isn't ready, so its mouth follows Martlet's voice.");
            else if (selected.RemoteHost is not null)
                Publish("Character is showing; pair the host again to use it for lip-sync.");
            else
                Publish("Character is showing; Audio2Face isn't running, so its mouth follows Martlet's voice.");
        }
        finally { changes.Release(); }
    }

    internal async Task InspectAsync(AvatarProfile selected, CancellationToken token)
    {
        var (attempt, version) = BeginAttempt(token, replace: true);
        var entered = false;
        try
        {
            await changes.WaitAsync(attempt.Token);
            entered = true;
            await CleanupAsync();
            CheckAttempt(attempt, version);
            var snapshot = await LocalAvatarFiles.SnapshotAsync(selected, attempt.Token);
            CheckAttempt(attempt, version);
            var next = createRenderer();
            next.Requested += action => { if (ReferenceEquals(Volatile.Read(ref renderer), next)) Requested?.Invoke(action); };
            next.Touched += touch => { if (ReferenceEquals(Volatile.Read(ref renderer), next)) OnTouched(touch); };
            next.Stroked += stroke => { if (ReferenceEquals(Volatile.Read(ref renderer), next) && IsShowing) Stroked?.Invoke(stroke); };
            next.Physical += change => { if (ReferenceEquals(Volatile.Read(ref renderer), next) && IsShowing) PhysicalChanged?.Invoke(change); };
            renderer = next;
            ForgetEyes();
            ForgetZoneView();
            ForgetAnimations();
            await next.StartAsync(selected, snapshot.Revision, Placement, VoiceMuted, attempt.Token);
            // A camera view that was open stays open when the character shows again.
            if (Camera is { } view) await next.SendAsync("camera", view, attempt.Token);
            // Clicks pass through a newly shown character when that is on.
            if (ClickThrough) await next.SendAsync("click-through", new RendererClickThrough(true), attempt.Token);
            // Lingering emotes come back on the same model; another model forgets them.
            await RestoreHeldAsync(next, selected.ModelPath, attempt.Token);
            // The overlay starts following the mouse; its usual gaze and Eyes menu follow Martlet's.
            await Gaze.RestoreAsync(next, attempt.Token);
            // The eyes' vision measurement for this model, if one is saved; the answer says what the eyes use (the model's own
            // data comes first), and Martlet measures them when they are only estimated.
            await SendEyesAsync(next, Volatile.Read(ref eyesFor)?.Invoke(selected.ModelPath), attempt.Token);
            // Its touch zones, so Show the zones on the character and Martlet's MCP (character_zones) find them as it moves.
            await SendZoneViewAsync(next, Volatile.Read(ref zoneViewFor)?.Invoke(selected.ModelPath), attempt.Token);
            lock (stateGate)
            {
                CheckAttempt(attempt, version);
                profile = selected with { ResourceRevision = snapshot.Revision };
                Publish("Model controls inspected; review the mappings before you activate Audio2Face.");
            }
        }
        catch
        {
            Publish("Couldn't inspect the model; check the model file, SDK folder and WebView2, then try again.");
            if (entered)
            {
                if (renderer is { } failed) await failed.DisposeAsync();
                renderer = null;
                profile = null;
            }
            throw;
        }
        finally
        {
            FinishAttempt(attempt);
            if (entered) changes.Release();
        }
    }

    internal async Task ActivateAsync(AvatarProfile selected, bool approved, CancellationToken token)
    {
        if (!approved) throw new InvalidOperationException("Allow Audio2Face for this session first.");
        var (attempt, version) = BeginAttempt(token, replace: false);
        var entered = false;
        try
        {
            await changes.WaitAsync(attempt.Token);
            entered = true;
            CheckAttempt(attempt, version);
            if (worker is { IsCompleted: false } || activation is not null)
                throw new InvalidOperationException("Stop the current lip-sync action and wait a moment.");
            if (renderer is null || renderer.HasExited || profile is null || selected.ProfileId != profile.ProfileId ||
                selected.ResourceRevision != profile.ResourceRevision)
                throw new InvalidOperationException("Inspect this model before activating lip-sync.");
            var snapshot = await LocalAvatarFiles.SnapshotAsync(selected, attempt.Token);
            CheckAttempt(attempt, version);
            if (snapshot.Revision != selected.ResourceRevision)
                throw new InvalidOperationException("The model changed. Inspect it again.");
            var settings = selected.Settings with { Enabled = true };
            if (settings.Assignments.Any(a => a.SourceId != SourceId) ||
                settings.RequestedAspects.Except(settings.OmittedAspects).Any(a => a is not (AvatarAspect.Mouth or AvatarAspect.Expression)))
                throw new InvalidOperationException("Only mouth and expression mappings can be activated. Omit the rest.");
            if (settings.Assignments.Count == 0) throw new InvalidOperationException("Add at least one mapping first.");
            var targets = Targets(settings);
            var mappingRevision = Convert.ToHexString(SHA256.HashData(AvatarJson.WriteConfiguration(settings)));
            var config = new RendererConfiguration(SourceId, snapshot.Revision, mappingRevision,
                targets.Select(t => new RendererMapping(t.Id, t.Aspect.ToString())).ToArray());
            await renderer.SendAsync("configure", config, attempt.Token);
            await StopLoudnessAsync();
            lock (stateGate)
            {
                CheckAttempt(attempt, version);
                profile = selected;
                activation = attempt;
                pending = null;
                observer.Enable();
                worker = RunAsync(new Audio2FaceMapping(settings, targets, config, new Audio2FaceOptions { Endpoint = new(selected.Endpoint) }),
                    version, renderer, activation.Token);
                Publish("Audio2Face is ready for this session and starts when Martlet speaks.");
            }
        }
        finally
        {
            FinishAttempt(attempt);
            if (entered) changes.Release();
        }
    }

    private (CancellationTokenSource Source, long Version) BeginAttempt(CancellationToken token, bool replace)
    {
        lock (stateGate)
        {
            token.ThrowIfCancellationRequested();
            if (pending is not null || !replace && activation is not null)
                throw new InvalidOperationException("Another avatar action is still finishing. Wait a moment and try again.");
            if (replace) Revoke();
            pending = CancellationTokenSource.CreateLinkedTokenSource(token);
            return (pending, ++generation);
        }
    }

    private void CheckAttempt(CancellationTokenSource attempt, long version)
    {
        lock (stateGate)
        {
            attempt.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(pending, attempt) || generation != version)
                throw new OperationCanceledException("Avatar action was replaced.", attempt.Token);
        }
    }

    private void FinishAttempt(CancellationTokenSource attempt)
    {
        lock (stateGate)
        {
            if (ReferenceEquals(pending, attempt)) pending = null;
            if (!ReferenceEquals(activation, attempt)) attempt.Dispose();
        }
    }

    internal void Revoke()
    {
        lock (stateGate)
        {
            generation++;
            // Local loudness lip-sync is not a privileged analysis session; it survives pause/mute/config revocations.
            if (loudness is null) observer.Disable();
            if (pending is { } attempt) attempt.CancelAsync().Forget();
            if (activation is { } current) current.CancelAsync().Forget();
        }
    }

    internal async Task StopAsync()
    {
        Revoke();
        await changes.WaitAsync();
        try
        {
            Revoke();
            await CleanupAsync();
            Publish("Character hidden; the voice continues.");
        }
        finally { changes.Release(); }
    }

    /// <summary>Hands lip-sync to another paired host (or back to this PC with null) while the character keeps showing;
    /// the next sentence uses the new host. A sentence already relaying to the old host falls back to voice loudness.</summary>
    internal async Task UseHostAsync(AvatarRemoteHost? remote, CancellationToken token)
    {
        await changes.WaitAsync(token);
        try
        {
            IAvatarHostLink? next = null;
            if (remote is not null && IsShowing)
                try { next = openHost(remote); }
                catch (Exception error) when (error is Audio2FaceHostException or ContractException) { }
            Interlocked.Exchange(ref hostLink, next)?.Dispose();
            lipSyncPool.Clear();
            if (profile is not null) profile = profile with { RemoteHost = remote };
            if (IsShowing) lipSyncPool.Warm(profile, next);
            if (!IsShowing) return;
            if (next is not null)
                Publish(await next.ReadyAsync(token)
                    ? $"Lip-sync uses host {next.Authority}."
                    : $"Host {next.Authority} isn't ready; mouth movement follows Martlet's voice for now.");
            else if (remote is not null)
                Publish("Pair the host again to use it for lip-sync.");
            else
                Publish("Lip-sync uses this PC when Audio2Face is available; otherwise it follows Martlet's voice.");
        }
        finally { changes.Release(); }
    }

    private async Task CleanupAsync()
    {
        await StopLoudnessAsync();
        observer.Disable();
        Interlocked.Exchange(ref hostLink, null)?.Dispose();
        lipSyncPool.Clear();
        if (renderer is not null)
        {
            await renderer.DisposeAsync();
            renderer = null;
        }
        // A clip belongs to the model the renderer showed.
        ForgetAnimations();
        if (worker is { } running)
        {
            // A worker that already ended (even with an error) is finished; only a still-running one blocks cleanup.
            try { await running.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (running.IsCompleted) { }
            catch (Exception error) when (running.IsCompleted)
            {
                if (RendererFailures.Is(error, CancellationToken.None))
                    RendererFailures.Log("Avatar lip-sync analysis had ended before it was stopped", error);
                else ErrorLog.Warn("Avatar lip-sync analysis had ended with an error before it was stopped.", error);
            }
        }
        worker = null;
        lock (stateGate)
        {
            activation?.Dispose();
            activation = null;
        }
        profile = null;
    }

    public async ValueTask DisposeAsync()
    {
        await cueLifetime.CancelAsync();
        await StopAsync();
        observer.Dispose();
    }
}
