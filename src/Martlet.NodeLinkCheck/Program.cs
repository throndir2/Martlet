using System.Text.Json;

// Runs commands between Martlet computers end to end on this PC's loopback: the real gateway (Kestrel, pinned TLS, pairing,
// signed requests, the command mailbox and its storage), the desktop's real client and its real agent loop with a fixture
// runner (CommandMailboxCheck, LoopbackGateway.cs, FixtureRunner). Two fixture devices pair: a requester and the host's agent.
// Nothing leaves loopback; no real credentials, Docker or Martlet installs are touched. Prints one JSON object {passed, steps}
// (CheckSteps) and exits 0 only when every step passed. With "live <pairing-code> <container>" it checks a disposable Linux
// gateway container instead (LiveGatewayCheck, GatewayContainer).
// With MARTLET_UPDATE_CHECK_RECORD set (MCP's app_update_check) it stands in for the installer and for Martlet, which the
// desktop's update helper starts, and records how it was started (UpdateHelperFixture).
if (Environment.GetEnvironmentVariable(Martlet.NodeLinkCheck.UpdateHelperFixture.RecordVariable) is { Length: > 0 } updateRecord)
    return Martlet.NodeLinkCheck.UpdateHelperFixture.Record(updateRecord, args);
// With "network" it rehearses the Martlet network instead (NetworkRehearsal) and prints its report.
if (args is ["network"])
{
    var (networkOk, networkReport) = await Martlet.NodeLinkCheck.NetworkRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(networkReport));
    return networkOk ? 0 : 1;
}
// With "exposure" it rehearses a host reachable from outside home (ExposureRehearsal) and prints its report.
if (args is ["exposure"])
{
    var (exposureOk, exposureReport) = await Martlet.NodeLinkCheck.ExposureRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(exposureReport));
    return exposureOk ? 0 : 1;
}
// With "move" it rehearses a host whose computer got another address on the home network (MoveRehearsal).
if (args is ["move"])
{
    var (moveOk, moveReport) = await Martlet.NodeLinkCheck.MoveRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(moveReport));
    return moveOk ? 0 : 1;
}
// With "signin-lab <data directory> [--mode owner|friend|account] [--sign-in-desktop] [--share-with-friend] [--household|--join-desktop]" it runs a live
// sign-in lab for the desktop on that data directory (SignInLab): the desktop as the host's owner (the default) or as a friend the
// host is shared with, or (account) household account sign-in and host attestations, headless (with --household: two hosts paired with
// the desktop and household sign-in providers; with --join-desktop: the desktop joins a household as Sam's computer; SignInAccountLab). For a session without the
// desktop's window, --sign-in-desktop (friend) has the lab sign the desktop in as the friend, and --share-with-friend (owner)
// shares the host with the simulated friend from the start.
if (args is ["signin-lab", var labDirectory])
    return await Martlet.NodeLinkCheck.SignInLab.RunAsync(labDirectory);
if (args is ["signin-lab", var friendLabDirectory, "--mode", var labMode, .. var labOptions] &&
    labOptions.All(o => o is "--sign-in-desktop" or "--share-with-friend" or "--household" or "--join-desktop"))
    return labMode switch
    {
        "owner" => await Martlet.NodeLinkCheck.SignInLab.RunAsync(friendLabDirectory, labOptions.Contains("--share-with-friend")),
        "friend" => await Martlet.NodeLinkCheck.SignInLab.RunFriendAsync(friendLabDirectory, labOptions.Contains("--sign-in-desktop")),
        "account" when labOptions is ["--household"] => await Martlet.NodeLinkCheck.SignInAccountLab.RunAsync(friendLabDirectory),
        "account" when labOptions is ["--join-desktop"] => await Martlet.NodeLinkCheck.SignInAccountLab.RunJoinAsync(friendLabDirectory),
        "account" when labOptions.Length == 0 => await Martlet.NodeLinkCheck.SignInLab.RunAccountAsync(friendLabDirectory),
        _ => 2
    };
// With "role-lab <data directory>" it runs a live lab for switching computers between companion and host PC (RoleLab).
if (args is ["role-lab", var roleLabDirectory])
    return await Martlet.NodeLinkCheck.RoleLab.RunAsync(roleLabDirectory);
// With "signin" it rehearses joining from outside home by signing in (SignInRehearsal) and prints its report.
if (args is ["signin"])
{
    var (signInOk, signInReport) = await Martlet.NodeLinkCheck.SignInRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(signInReport));
    return signInOk ? 0 : 1;
}
// With "api" it rehearses API keys for software outside the network (ApiRehearsal) and prints its report.
if (args is ["api"])
{
    var (apiOk, apiReport) = await Martlet.NodeLinkCheck.ApiRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(apiReport));
    return apiOk ? 0 : 1;
}
// With "openai" it rehearses the OpenAI-compatible endpoint across a Martlet network (OpenAiRehearsal) and prints its report.
if (args is ["openai"])
{
    var (openAiOk, openAiReport) = await Martlet.NodeLinkCheck.OpenAiRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(openAiReport));
    return openAiOk ? 0 : 1;
}
// With "front-door" it rehearses a host's front door, its public listener (FrontDoorRehearsal), and prints its report.
if (args is ["front-door"])
{
    var (frontDoorOk, frontDoorReport) = await Martlet.NodeLinkCheck.FrontDoorRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(frontDoorReport));
    return frontDoorOk ? 0 : 1;
}// With "deep-thinking" it rehearses the Deep thinking host role beside Thinking's (DeepThinkingRehearsal) and prints its report.
if (args is ["deep-thinking"])
{
    var (deepOk, deepReport) = await Martlet.NodeLinkCheck.DeepThinkingRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(deepReport));
    return deepOk ? 0 : 1;
}
// With "gpu-priority" it rehearses live turn first on one graphics card (GpuPriorityRehearsal) and prints its report.
if (args is ["gpu-priority"])
{
    var (gpuOk, gpuReport) = await Martlet.NodeLinkCheck.GpuPriorityRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(gpuReport));
    return gpuOk ? 0 : 1;
}
// With "voices" it rehearses the shared speaking voices and their recordings (VoiceRehearsal) and prints its report.
if (args is ["voices"])
{
    var (voicesOk, voicesReport) = await Martlet.NodeLinkCheck.VoiceRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(voicesReport));
    return voicesOk ? 0 : 1;
}
// With "characters" it rehearses the shared character models and their pieces (CharacterRehearsal) and prints its report.
if (args is ["characters"])
{
    var (charactersOk, charactersReport) = await Martlet.NodeLinkCheck.CharacterRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(charactersReport));
    return charactersOk ? 0 : 1;
}
// With "settings" it rehearses one Martlet on every computer: the shared settings and their API keys (SettingsRehearsal).
if (args is ["settings"])
{
    var (settingsOk, settingsReport) = await Martlet.NodeLinkCheck.SettingsRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(settingsReport));
    return settingsOk ? 0 : 1;
}
// With "creations" it rehearses Martlet's creations and their assets shared through the hosts (CreationRehearsal).
if (args is ["creations"])
{
    var (creationsOk, creationsReport) = await Martlet.NodeLinkCheck.CreationRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(creationsReport));
    return creationsOk ? 0 : 1;
}
// With "memories" it rehearses one memory on every computer: real memory stores kept the same through the hosts (MemoryRehearsal).
if (args is ["memories"])
{
    var (memoriesOk, memoriesReport) = await Martlet.NodeLinkCheck.MemoryRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(memoriesReport));
    return memoriesOk ? 0 : 1;
}
// With "conversations" it rehearses one record of conversations on every computer: real records kept the same through the hosts
// (ConversationRehearsal).
if (args is ["conversations"])
{
    var (conversationsOk, conversationsReport) = await Martlet.NodeLinkCheck.ConversationRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(conversationsReport));
    return conversationsOk ? 0 : 1;
}
// With "accounts" it rehearses the household's account directory kept the same through the hosts (AccountRehearsal).
if (args is ["accounts"])
{
    var (accountsOk, accountsReport) = await Martlet.NodeLinkCheck.AccountRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(accountsReport));
    return accountsOk ? 0 : 1;
}
// With "logs" it rehearses shared logs: every computer's log lines reaching every host and desktop (LogRehearsal).
if (args is ["logs"])
{
    var (logsOk, logsReport) = await Martlet.NodeLinkCheck.LogRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(logsReport));
    return logsOk ? 0 : 1;
}
// With "host-connections" it rehearses kept connections to a host and its status as a host goes and comes back
// (HostConnectionRehearsal).
if (args is ["host-connections"])
{
    var (connectionsOk, connectionsReport) = await Martlet.NodeLinkCheck.HostConnectionRehearsal.RunAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(connectionsReport));
    return connectionsOk ? 0 : 1;
}
// With "voice-engine <engine> <endpoint> [text|-] [data directory]" it speaks one sentence with a live voice engine's loopback
// service through the engine's real relay and gateway (VoiceEngineCheck) and prints its report; Chatterbox Original uses the
// style saved in the data directory (chatterbox-style.json), else Resemble's suggestions.
if (args is ["voice-engine", var voiceEngine, var voiceEndpoint, .. var voiceRest] && voiceRest.Length <= 2)
{
    var (voiceOk, voiceReport) = await Martlet.NodeLinkCheck.VoiceEngineCheck.RunAsync(voiceEngine, voiceEndpoint,
        voiceRest.Length > 0 && voiceRest[0] != "-" ? voiceRest[0] : null, voiceRest.Length > 1 ? voiceRest[1] : null, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(voiceReport));
    return voiceOk ? 0 : 1;
}
// With "listening-engine <endpoint> <model|-> <clips directory>" it transcribes the folder's 16 kHz PCM16 clips with a live
// speech-to-text service on loopback (the stt role's whisper.cpp or Parakeet) through the role's real relay and gateway
// (ListeningEngineCheck) and prints its report.
if (args is ["listening-engine", var listeningEndpoint, var listeningModel, var listeningClips])
{
    var (listeningOk, listeningReport) = await Martlet.NodeLinkCheck.ListeningEngineCheck.RunAsync(listeningEndpoint,
        listeningModel == "-" ? null : listeningModel, listeningClips, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(listeningReport));
    return listeningOk ? 0 : 1;
}
// With "voice-recognition-engine <endpoint> <clips directory>" it recognizes who speaks in the folder's 16 kHz PCM16 clips with a
// live voice recognition service on loopback (the voice-recognition role's worker) through the role's real relay and gateway
// and the desktop's client (VoiceRecognitionEngineCheck) and prints its report.
if (args is ["voice-recognition-engine", var recognitionEndpoint, var recognitionClips])
{
    var (recognitionOk, recognitionReport) = await Martlet.NodeLinkCheck.VoiceRecognitionEngineCheck.RunAsync(recognitionEndpoint,
        recognitionClips, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(recognitionReport));
    return recognitionOk ? 0 : 1;
}
// With "singing-check <endpoint|fixture|paired:<data directory>> <seconds> <quality> <voice match> [save directory|-]
// [voice WAV|-] [transcript|-] [bpm|-] [key|-] [voice ID|-] [host ID|-] [split|-]" it makes one song with a singing service through the
// singing role's real relay and gateway (SingingCheck; paired: through a paired host's own gateway, as the desktop does),
// optionally in the voice of a recording copy or a shared voice, with a tempo and key, as two jobs (split: the music, then the
// voice match), and saving its WAVs, and prints its report.
if (args is ["singing-check", var singingEndpoint, var singingSeconds, var singingQuality, var singingMatch, .. var singingMore] &&
    singingMore.Length <= 8)
{
    string? Optional(int index) => singingMore.Length > index && singingMore[index] is { Length: > 0 } value && value != "-" ? value : null;
    var (singingOk, singingReport) = await Martlet.NodeLinkCheck.SingingCheck.RunAsync(singingEndpoint,
        int.Parse(singingSeconds, System.Globalization.CultureInfo.InvariantCulture), singingQuality, singingMatch,
        Optional(0), Optional(1), Optional(2),
        Optional(3) is { } singingBpm ? int.Parse(singingBpm, System.Globalization.CultureInfo.InvariantCulture) : null, Optional(4),
        CancellationToken.None, Optional(5), Optional(6), Optional(7) == "split");
    Console.WriteLine(JsonSerializer.Serialize(singingReport));
    return singingOk ? 0 : 1;
}
// With "singing-status <data directory>" it reads Singing on every host paired in that desktop data directory through each
// host's own gateway (SingingStatus) and prints it.
if (args is ["singing-status", var singingData])
{
    var singingStatus = await Martlet.NodeLinkCheck.SingingStatus.RunAsync(singingData, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(singingStatus));
    return 0;
}
// With "gpu-priority-status <data directory>" it reads GPU priority (live turn first) on every host paired in that desktop data
// directory through each host's own gateway (GpuPriorityStatus) and prints it.
if (args is ["gpu-priority-status", var priorityData])
{
    var priorityStatus = await Martlet.NodeLinkCheck.GpuPriorityStatus.RunAsync(priorityData, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(priorityStatus));
    return 0;
}
var check = new Martlet.NodeLinkCheck.CheckSteps();
try
{
    if (args is ["live", var code, var container]) await Martlet.NodeLinkCheck.LiveGatewayCheck.RunAsync(check, code, container);
    else await Martlet.NodeLinkCheck.CommandMailboxCheck.RunAsync(check);
}
catch (Exception error) { check.Step("unexpected", false, $"{error.GetType().Name}: {error.Message}"); }
Console.WriteLine(JsonSerializer.Serialize(check.Report));
return check.Passed ? 0 : 1;
