using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Martlet.Mcp;
using Xunit.Abstractions;

namespace Martlet.Desktop.Tests;

public sealed class McpServerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NegotiatesAndListsToolsOverStdio()
    {
        var messages = await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        Assert.Equal(2, messages.Length);
        Assert.Equal("2025-06-18", messages[0].GetProperty("result").GetProperty("protocolVersion").GetString());
        var tools = messages[1].GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "ui_click");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "ui_scroll");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "character_touch");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "character_pose");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "character_mouth");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "character_look");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "deep_thinking_role_selftest");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "gpu_priority_selftest");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "gpu_priority_status");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "reading_check");
        Assert.Contains(tools, tool => tool.GetProperty("name").GetString() == "active_app_check");
        Assert.DoesNotContain(tools, tool => tool.GetProperty("name").GetString() == "fixture");
    }

    [Fact]
    public async Task ActiveAppCheckNamesTheProgramInFrontAndRehearsesWhatALookTellsTheModel()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.ActiveApp." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var message = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "active_app_check", arguments = new { dataDirectory = directory } }
            });
            var result = ToolResult((await SendAsync(message))[0]);
            // The window in front is read, never its title.
            Assert.False(result.GetProperty("inFront").TryGetProperty("title", out _));
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("sample").GetProperty("app").GetString()));
            var rules = result.GetProperty("rules").EnumerateArray()
                .ToDictionary(rule => rule.GetProperty("window").GetString()!, rule => rule.GetProperty("fullScreen").GetBoolean());
            Assert.True(rules["borderless full-screen game or video"]);
            Assert.True(rules["borderless window filling the second monitor"]);
            Assert.False(rules["maximized window over a taskbar that hides itself"]);
            Assert.False(rules["window smaller than its monitor"]);
            var prompts = result.GetProperty("prompts");
            Assert.Equal("none", prompts.GetProperty("state").GetString());
            Assert.StartsWith("(Screen glance. Active app: ELDEN RING (full screen). Active window: \"ELDEN RING\".",
                prompts.GetProperty("glance").GetString());
            Assert.Equal("Active app in the picture: ELDEN RING (full screen). Active window: \"ELDEN RING\".",
                prompts.GetProperty("withMessage").GetString());
            Assert.Equal("[Screen] You looked at the user's whole screen (active window \"ELDEN RING\" in ELDEN RING, full screen): " +
                "a boss fight, low health.", prompts.GetProperty("kept").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AudioModelCheckRehearsesYourVoiceGoingToTheAudioModelWithTheProductionCode()
    {
        var result = ToolResult((await SendAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"audio_model_check"}}"""))[0]);
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        var steps = result.GetProperty("steps").EnumerateArray().ToDictionary(step => step.GetProperty("Name").GetString()!,
            step => step.GetProperty("Passed").GetBoolean());
        Assert.True(steps["in-time-reply-takes-the-words"]);
        Assert.True(steps["late-reply-never-waits"]);
        Assert.True(steps["late-words-go-to-the-board"]);
        Assert.True(steps["refused"]);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("saved").ValueKind);
    }

    [Fact]
    public async Task SaidLatelyAndCheckInsRehearsalsPassWithTheProductionCode()
    {
        var messages = await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"said_lately_check"}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"check_ins_check"}}""");
        foreach (var message in messages)
        {
            var result = ToolResult(message);
            Assert.True(result.GetProperty("passed").GetBoolean(), result.GetRawText());
        }
        var steps = ToolResult(messages[0]).GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray();
        Assert.Contains("which requests carry it", steps);
        Assert.Contains("the check-in reads the same lines", steps);
        var checkInSteps = ToolResult(messages[1]).GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToArray();
        Assert.Contains("built-in ones recreated as your own", checkInSteps);
        Assert.Contains("context: goes with one request", checkInSteps);
        Assert.Contains("signals: when they wait", checkInSteps);
        Assert.Contains("signals: the facts and the check-ins", checkInSteps);
        Assert.Contains("signals: hours and the per-hour cap saved and read back", checkInSteps);
    }

    [Fact]
    public async Task DeniesUnapprovedUiEffectsAndUnrelatedProcesses()
    {
        var messages = await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ui_click","arguments":{"id":"LiveSend"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ui_connect","arguments":{"pid":-1}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"ui_snapshot"}}""");
        Assert.All(messages, message => Assert.True(message.GetProperty("result").GetProperty("isError").GetBoolean()));
        Assert.Contains("--allow-ui-effects", messages[0].GetRawText());
        Assert.Contains("positive", messages[1].GetRawText());
        Assert.Contains("Connect", messages[2].GetRawText());
    }

    [Fact]
    public async Task RejectsMalformedRequestWithoutEndingSession()
    {
        var messages = await SendAsync(
            """{"jsonrpc":123,"id":1,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        Assert.Equal(-32600, messages[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(2, messages[1].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task DiscordCompanionCheckRehearsesFriendsAndPrivateCallsWithoutAToken()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Discord." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var message = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "discord_companion_check", arguments = new { dataDirectory = directory, requestFrom = "42", requestName = "Bo" } }
            });
            var result = ToolResult((await SendAsync(message))[0]);
            Assert.Equal("Requested", result.GetProperty("filed").GetProperty("outcome").GetString());
            Assert.Equal(1, result.GetProperty("current").GetProperty("requestsWaiting").GetInt32());
            var call = result.GetProperty("rehearsal").GetProperty("call");
            Assert.True(call.GetProperty("rang").GetBoolean());
            Assert.True(call.GetProperty("deletedAfterEnd").GetBoolean());
            var everyone = call.GetProperty("overwrites").EnumerateArray().Single(o => o.GetProperty("who").GetString() == "@everyone");
            Assert.Contains("ViewChannel", everyone.GetProperty("denied").GetString());
            Assert.Single(Martlet.Discord.DiscordCompanionState.Load(directory).Requests);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MemoryStatusCountsWhoseFactsAreWithoutTheirTextNamesOrIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Memory." + Guid.NewGuid().ToString("N"));
        try
        {
            var none = ToolResult((await SendAsync(Call("memory_status", directory)))[0]);
            Assert.Equal("not set up", none.GetProperty("memory").GetString());
            Assert.Equal("none", none.GetProperty("state").GetString());

            var now = DateTimeOffset.UtcNow;
            static float[] Print(int seed)
            {
                var random = new Random(seed);
                return Martlet.Core.Speakers.VoicePrints.Normalize(Enumerable.Range(0, Martlet.Core.Speakers.VoicePrints.Dimension)
                    .Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
            }
            var roster = Martlet.Core.Speakers.VoiceRoster.Empty;
            (roster, var sam) = roster.Add(Print(1), 3, "desk", now);
            (roster, var other) = roster.Add(Print(2), 3, "desk", now);
            roster = roster.SetNames(sam!.Id, "Samantha", [], "desk", now).SetOwner(sam.Id, true, "desk", now);
            var settings = new Martlet.Core.Settings.SettingsStore(directory);
            var initial = Martlet.Core.Settings.SetupSettings.Begin(null);
            var saved = await settings.SaveAsync(initial, null);
            File.WriteAllBytes(Path.Combine(directory, "voices.json"), roster.Write());
            using (var memory = new DesktopMemoryService(settings))
            {
                var configured = await memory.SaveConfigurationAsync(initial, saved.Revision, true,
                    Martlet.Core.Settings.MemoryStoragePolicy.AppLocalData, null);
                var revision = configured.Settings.Memory!.ConfigurationRevision;
                var forever = Martlet.Memory.MemoryRetention.UntilDeleted();
                await memory.SaveFactAsync(revision, "Samantha keeps canary-1 private.", forever, sam.Id);
                await memory.SaveFactAsync(revision, "canary-2 expires.", Martlet.Memory.MemoryRetention.ExpiringAt(now.AddDays(1)), sam.Id);
                await memory.SaveFactAsync(revision, "canary-3 belongs to the other voice.", forever, other!.Id);
                await memory.SaveFactAsync(revision, "canary-4 is everyone's.", forever);
                await memory.SaveFactAsync(revision, "canary-5 belongs to a forgotten voice.", forever, "0123456789abcdef");
            }

            var message = (await SendAsync(Call("memory_status", directory)))[0];
            var raw = message.GetRawText();
            foreach (var secret in new[] { "canary", "Samantha", sam.Id, other.Id, "0123456789abcdef", Path.GetFileName(directory) })
                Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
            var result = ToolResult(message);
            Assert.Equal("on", result.GetProperty("memory").GetString());
            Assert.Equal("Martlet folder", result.GetProperty("storage").GetString());
            Assert.Equal("loaded", result.GetProperty("state").GetString());
            Assert.Equal("loaded", result.GetProperty("voiceList").GetString());
            Assert.Equal(5, result.GetProperty("facts").GetInt32());
            Assert.Equal(5, result.GetProperty("typed").GetInt32());
            Assert.Equal(0, result.GetProperty("fromConversation").GetInt32());
            Assert.Equal(1, result.GetProperty("expiring").GetInt32());
            var whose = result.GetProperty("whose");
            Assert.Equal(1, whose.GetProperty("everyone").GetInt32());
            Assert.Equal(1, whose.GetProperty("forgottenVoices").GetInt32());
            Assert.Equal(
                new[] { (sam.Tag, true, true, 2), (other.Tag, false, false, 1) },
                whose.GetProperty("voices").EnumerateArray().Select(v => (v.GetProperty("voice").GetString()!, v.GetProperty("named").GetBoolean(),
                    v.GetProperty("owner").GetBoolean(), v.GetProperty("facts").GetInt32())).ToArray());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        static string Call(string tool, string dataDirectory) => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = new { dataDirectory } }
        });
    }

    [Fact]
    public async Task DiscordStatusReadsTheSetupWithoutTheTokenNamesOrPeople()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Discord." + Guid.NewGuid().ToString("N"));
        try
        {
            var none = ToolResult((await SendAsync(DataCall("discord_status", directory)))[0]);
            Assert.Equal("none", none.GetProperty("state").GetString());
            Assert.False(none.GetProperty("configured").GetBoolean());

            Assert.True(new Martlet.Discord.DiscordPreferences
            {
                ApplicationId = 123456789012345678, CredentialId = Guid.NewGuid(), Enabled = true, OwnerUserId = 987654321098765432,
                ServerChat = Martlet.Discord.DiscordChatMode.Mentions,
                People = [new(111111111111111111, "canary-alice"), new(222222222222222222, "canary-bob", MayCall: false)]
            }.Save(directory));
            var message = (await SendAsync(DataCall("discord_status", directory)))[0];
            var raw = message.GetRawText();
            foreach (var secret in new[] { "canary", "987654321098765432", "111111111111111111" })
                Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
            var result = ToolResult(message);
            Assert.Equal("loaded", result.GetProperty("state").GetString());
            Assert.True(result.GetProperty("configured").GetBoolean());
            Assert.Equal("123456789012345678", result.GetProperty("applicationId").GetString());
            // No credential exists under the fresh reference, so the token is not readable (and never returned).
            Assert.NotEqual("readable", result.GetProperty("token").GetString());
            Assert.True(result.GetProperty("ownerSet").GetBoolean());
            Assert.Equal("Mentions", result.GetProperty("serverChat").GetString());
            Assert.Equal(2, result.GetProperty("people").GetInt32());
            Assert.Equal(1, result.GetProperty("peopleMayCall").GetInt32());
            Assert.Contains("reset the bot token", result.GetProperty("next").GetString());

            var check = ToolResult((await SendAsync(DataCall("discord_check", directory)))[0]);
            Assert.Equal("tokenUnreadable", check.GetProperty("state").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CharacterProfilesReportsKeysAndPartsWithoutNames()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Profiles." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var settings = Martlet.Core.Settings.CompanionSettings.Begin(null);
            var companion = settings.Companion!.Add("Secret persona");
            companion = companion
                .AddCharacter("Secret built-in", companion.ActivePersonaId, Martlet.Core.Settings.CharacterProfile.BuiltInModel, null, out var builtIn)
                .AddCharacter("Secret gone", companion.ActivePersonaId, "0123456789abcdef0123", "missing-voice", out var gone)
                .SelectCharacter(builtIn.Id);
            File.WriteAllBytes(Path.Combine(directory, "settings.json"), Martlet.Core.Contracts.ContractJson.Write(settings with { Companion = companion }));
            var message = (await SendAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "character_profiles", arguments = new { dataDirectory = directory } }
            })))[0];
            Assert.DoesNotContain("Secret", message.GetRawText());
            var result = ToolResult(message);
            Assert.Equal(2, result.GetProperty("count").GetInt32());
            Assert.Equal(builtIn.Key, result.GetProperty("current").GetString());
            Assert.Equal(builtIn.Key, result.GetProperty("lastUsed").GetString());
            var profiles = result.GetProperty("profiles").EnumerateArray().ToArray();
            Assert.Equal(("builtin", "keep", true), (profiles[0].GetProperty("look").GetString(), profiles[0].GetProperty("voice").GetString(),
                profiles[0].GetProperty("inUse").GetBoolean()));
            Assert.Equal((gone.Key, "missing", "missing", false), (profiles[1].GetProperty("key").GetString(), profiles[1].GetProperty("look").GetString(),
                profiles[1].GetProperty("voice").GetString(), profiles[1].GetProperty("inUse").GetBoolean()));
            Assert.Equal("none", result.GetProperty("hereState").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CharacterProfilesReportsWhatEachProfileKeepsOnThisPc()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.ProfilesHere." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var settings = Martlet.Core.Settings.CompanionSettings.Begin(null);
            var companion = settings.Companion!
                .AddCharacter("Secret tall", settings.Companion.ActivePersonaId, Martlet.Core.Settings.CharacterProfile.BuiltInModel, null, out var tall)
                .AddCharacter("Secret small", settings.Companion.ActivePersonaId, null, null, out var small);
            File.WriteAllBytes(Path.Combine(directory, "settings.json"), Martlet.Core.Contracts.ContractJson.Write(settings with { Companion = companion }));
            Assert.True(CharacterProfileLocalStore.Save(directory, CharacterProfilesHere.Empty.With(tall.Id, new CharacterProfileLocal(
                new Martlet.Avatar.Hosting.RendererPlacement(true, 2120, 300, 520, 693, @"\\.\DISPLAY2", 200, 260),
                Martlet.Avatar.Hosting.GazeMode.Near, false, Martlet.Conversation.TouchInterrupts.Never)).Using(tall.Id)));

            var message = (await SendAsync(DataCall("character_profiles", directory)))[0];
            Assert.DoesNotContain("Secret", message.GetRawText());
            var result = ToolResult(message);
            Assert.Equal(("loaded", tall.Key), (result.GetProperty("hereState").GetString(), result.GetProperty("hereInUse").GetString()));
            var profiles = result.GetProperty("profiles").EnumerateArray().ToArray();
            var here = profiles.Single(p => p.GetProperty("key").GetString() == tall.Key).GetProperty("here");
            var place = here.GetProperty("place");
            Assert.Equal((true, 520.0, 693.0, @"\\.\DISPLAY2"), (place.GetProperty("locked").GetBoolean(), place.GetProperty("width").GetDouble(),
                place.GetProperty("height").GetDouble(), place.GetProperty("screen").GetString()));
            Assert.Equal(("near", false, "never"), (here.GetProperty("gaze").GetString(), here.GetProperty("gazeFree").GetBoolean(),
                here.GetProperty("touchInterrupts").GetString()));
            var nothing = profiles.Single(p => p.GetProperty("key").GetString() == small.Key);
            Assert.True(!nothing.TryGetProperty("here", out var none) || none.ValueKind == JsonValueKind.Null);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CharacterStatusReadsWhetherClicksPassThroughTheCharacter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.ClickThrough." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var none = ToolResult((await SendAsync(DataCall("character_status", directory)))[0]).GetProperty("clickThrough");
            Assert.Equal(("none", false), (none.GetProperty("state").GetString(), none.GetProperty("on").GetBoolean()));

            Assert.True(Martlet.Desktop.CharacterClickThroughStore.Save(directory, true));
            var on = ToolResult((await SendAsync(DataCall("character_status", directory)))[0]).GetProperty("clickThrough");
            Assert.Equal(("loaded", true), (on.GetProperty("state").GetString(), on.GetProperty("on").GetBoolean()));

            File.WriteAllText(Path.Combine(directory, Martlet.Desktop.CharacterClickThroughStore.FileName), "not json");
            var unreadable = ToolResult((await SendAsync(DataCall("character_status", directory)))[0]).GetProperty("clickThrough");
            Assert.Equal(("unreadable", false), (unreadable.GetProperty("state").GetString(), unreadable.GetProperty("on").GetBoolean()));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MessagingStatusReadsTelegramWithoutTokenOrChats()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Messaging." + Guid.NewGuid().ToString("N"));
        try
        {
            var none = ToolResult((await SendAsync(DataCall("messaging_status", directory)))[0]);
            Assert.Equal("none", none.GetProperty("state").GetString());
            Assert.False(none.GetProperty("telegram").GetProperty("connected").GetBoolean());

            Assert.True(new MessagingPreferences
            {
                Telegram = new()
                {
                    Enabled = true, BotName = "Martlet", BotUsername = "my_martlet_bot", SpeakReplies = true,
                    Chats = [new("987654321", "Samantha canary"), new("123", "Other")]
                },
                WhatsApp = new()
                {
                    Enabled = true, Name = "Martlet test", Number = "+1 555-0100", AppId = "670843887433847", BusinessAccountId = "102290129340398",
                    PhoneNumberId = "106540352242922", Port = 47821, CredentialId = Guid.NewGuid(), Chats = [new("15550199", "Whats canary")]
                }
            }.Save(directory));
            var message = (await SendAsync(DataCall("messaging_status", directory)))[0];
            var raw = message.GetRawText();
            foreach (var secret in new[] { "987654321", "Samantha", "canary", "15550199" }) Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
            var telegram = ToolResult(message).GetProperty("telegram");
            Assert.True(telegram.GetProperty("connected").GetBoolean());
            Assert.True(telegram.GetProperty("enabled").GetBoolean());
            Assert.Equal("my_martlet_bot", telegram.GetProperty("bot").GetString());
            Assert.Equal(2, telegram.GetProperty("chats").GetInt32());
            Assert.True(telegram.GetProperty("speakReplies").GetBoolean());
            var whatsApp = ToolResult(message).GetProperty("whatsApp");
            Assert.True(whatsApp.GetProperty("connected").GetBoolean());
            Assert.Equal("+1 555-0100", whatsApp.GetProperty("number").GetString());
            Assert.Equal(47821, whatsApp.GetProperty("port").GetInt32());
            Assert.True(whatsApp.GetProperty("quickTunnel").GetBoolean());
            Assert.True(whatsApp.GetProperty("secretsSaved").GetBoolean());
            Assert.Equal(1, whatsApp.GetProperty("chats").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string DataCall(string tool, string dataDirectory) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = new { dataDirectory } }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AttachesToRealDesktopAndNavigates(bool churn) => WithDesktop(async (_, automation) =>
    {
        var churnTask = churn ? ChurnWindows() : Task.CompletedTask;
        try
        {
            await Task.Run(() => automation.ClickAsync("NavSettings"));
            var settings = "";
            for (var attempt = 0; attempt < 50 && !settings.Contains("AutomaticUpdateCheck", StringComparison.Ordinal); attempt++)
            {
                await Task.Delay(100);
                settings = JsonSerializer.Serialize(await Task.Run(() => automation.Snapshot()));
            }
            Assert.Contains("AutomaticUpdateCheck", settings);
            if (churn)
            {
                for (var iteration = 0; iteration < 100; iteration++)
                {
                    var snapshot = JsonSerializer.SerializeToElement(await Task.Run(() => automation.Snapshot()));
                    Assert.Single(snapshot.GetProperty("windows").EnumerateArray());
                    await Task.Delay(5);
                }
            }
        }
        finally { await churnTask; }
    });

    [Fact]
    public Task PreservesDialogsAndRejectsWrongOwnerHiddenMainAndExitedProcess() => WithDesktop(async (process, automation) =>
    {
        var pid = process.Id;
        process.Refresh();
        var mainHandle = process.MainWindowHandle;
        Assert.NotEqual(0, mainHandle);
        await Task.Run(() =>
        {
            Assert.Throws<ArgumentException>(() => automation.Connect(Environment.ProcessId));
            Assert.Throws<InvalidOperationException>(() => DesktopAutomation.WindowForProcess(Environment.ProcessId, mainHandle));
        });

        // A modal workflow window: Home's setup advisor opens and closes with safe clicks only.
        await Task.Run(() => automation.ClickAsync("OpenSetupAdvisor"));
        var snapshot = await WaitForWindowCount(automation, 2);
        Assert.Single(snapshot.GetProperty("controls").EnumerateArray(),
            control => control.GetProperty("id").GetString() == "AdvisorClose");
        var advisorHandle = await Task.Run(() => (nint)DesktopAutomation.WindowForProcess(pid, mainHandle)
            .FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SetupAdvisorWindow"))
            .Current.NativeWindowHandle);
        Assert.NotEqual(0, advisorHandle);

        Assert.True(ShowWindowAsync(mainHandle, 0));
        await WaitForVisibility(mainHandle, false);
        await Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => DesktopAutomation.WindowForProcess(pid, mainHandle));
            Assert.Throws<InvalidOperationException>(() => automation.Snapshot());
            Assert.Throws<InvalidOperationException>(() => new DesktopAutomation(false).Connect(pid));
        });
        Assert.True(ShowWindowAsync(mainHandle, 4));
        await WaitForVisibility(mainHandle, true);
        await Task.Run(() => automation.ClickAsync("AdvisorClose"));
        await WaitForVisibility(advisorHandle, false);
        await WaitForWindowCount(automation, 1);

        process.Kill();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Run(() =>
        {
            Assert.Throws<InvalidOperationException>(() => DesktopAutomation.WindowForProcess(pid, mainHandle));
            Assert.Throws<ArgumentException>(() => automation.Snapshot());
        });
    });

    private async Task WithDesktop(Func<Process, DesktopAutomation, Task> action)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Martlet.Desktop.exe");
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Tests." + Guid.NewGuid().ToString("N"));
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { "--data-directory", directory },
            UseShellExecute = false
        })!;
        try
        {
            var automation = new DesktopAutomation(false);
            Exception? lastError = null;
            var connected = false;
            for (var attempt = 0; attempt < 50 && !connected; attempt++)
            {
                await Task.Delay(100);
                try
                {
                    await Task.Run(() => automation.Connect(process.Id));
                    connected = true;
                }
                catch (InvalidOperationException error) { lastError = error; }
            }
            Assert.True(connected, lastError?.Message);
            await action(process, automation);
        }
        catch
        {
            process.Refresh();
            output.WriteLine($"Owned fixture: pid={process.Id}, exited={process.HasExited}, exitCode={(process.HasExited ? process.ExitCode : null)}, hwnd={(process.HasExited ? 0 : process.MainWindowHandle)}");
            throw;
        }
        finally
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static Task ChurnWindows()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Exception? failure = null;
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    for (var iteration = 0; iteration < 200; iteration++)
                    {
                        var window = new Window
                        {
                            Title = "MCP owned churn fixture", Width = 100, Height = 100,
                            ShowActivated = false, ShowInTaskbar = false
                        };
                        try
                        {
                            window.Show();
                            await Task.Delay(5);
                        }
                        finally { window.Close(); }
                    }
                }
                catch (Exception error) { failure = error; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
            if (failure is null) finished.SetResult();
            else finished.SetException(failure);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task;
    }

    private static async Task WaitForVisibility(nint handle, bool visible)
    {
        for (var attempt = 0; attempt < 50 && IsWindowVisible(handle) != visible; attempt++)
            await Task.Delay(20);
        Assert.Equal(visible, IsWindowVisible(handle));
    }

    private static async Task<JsonElement> WaitForWindowCount(DesktopAutomation automation, int count)
    {
        var snapshot = JsonSerializer.SerializeToElement(await Task.Run(() => automation.Snapshot()));
        for (var attempt = 0; attempt < 50 && snapshot.GetProperty("windows").GetArrayLength() != count; attempt++)
        {
            await Task.Delay(100);
            snapshot = JsonSerializer.SerializeToElement(await Task.Run(() => automation.Snapshot()));
        }
        Assert.Equal(count, snapshot.GetProperty("windows").GetArrayLength());
        return snapshot;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [Fact]
    public async Task LocalModelServersRehearsesFindingTestingAndKeysAgainstLoopbackFixtures()
    {
        var result = ToolResult((await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"local_model_servers","arguments":{"fixture":true,"address":"192.168.1.20:1234"}}}"""))[0]);
        var fixture = result.GetProperty("fixture");
        output.WriteLine(fixture.ToString());
        Assert.True(fixture.GetProperty("ok").GetBoolean());
        Assert.True(fixture.GetProperty("llamaCpp").GetProperty("test").GetProperty("ToolsRejected").GetBoolean());
        Assert.Equal("NeedsKey", fixture.GetProperty("keyed").GetProperty("withoutKey").GetProperty("kind").GetString());
        // An address off this PC is refused before anything is asked.
        var address = result.GetProperty("address");
        Assert.Equal(JsonValueKind.Null, address.GetProperty("baseUrl").ValueKind);
        Assert.Contains("isn't this computer", address.GetProperty("problem").GetString(), StringComparison.Ordinal);
        Assert.Contains(result.GetProperty("apps").EnumerateArray(), app => app.GetProperty("Id").GetString() == "lm-studio");
    }

    [Fact]
    public async Task OllamaRecoveryRehearsesTheRepairOnFixturesAndOnlyReadsThisPc()
    {
        var result = ToolResult((await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ollama_recovery","arguments":{"fixture":true}}}"""))[0]);
        var fixture = result.GetProperty("fixture");
        output.WriteLine(fixture.ToString());
        Assert.True(fixture.GetProperty("passed").GetBoolean());
        var scenarios = fixture.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(["Linked", "UnknownSettings", "OtherError", "Answering"], scenarios.Select(s => s.GetProperty("scenario").GetString()));
        var linked = scenarios[0];
        Assert.True(linked.GetProperty("repair").GetProperty("Repaired").GetBoolean());
        Assert.Equal("stop", linked.GetProperty("events")[0].GetString());
        Assert.StartsWith("<fixture>", linked.GetProperty("savedModels").GetString(), StringComparison.Ordinal);
        // This PC's own Ollama is only read: its state, never a path under the user's folders.
        var thisPc = result.GetProperty("thisPc");
        Assert.Contains(thisPc.GetProperty("kind").GetString(), new[] { "Answering", "NotInstalled", "NotRunning", "StopsOnStart", "CrashLoop" });
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), thisPc.GetRawText().Replace(@"\\", @"\", StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement ToolResult(JsonElement message)
    {
        var text = message.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement[]> SendAsync(params string[] requests)
    {
        using var reader = new StringReader(string.Join('\n', requests) + "\n");
        using var writer = new StringWriter();
        await new McpServer(new DesktopAutomation(false)).RunAsync(reader, writer, CancellationToken.None);
        return writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
    }
}
