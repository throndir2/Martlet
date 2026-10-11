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
        var messages = await SendWithAsync(false, true,
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
        Assert.Equal(tools.Length, tools.Select(tool => tool.GetProperty("name").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task TellsAssistantsWhereToStartAndListsTheirToolsFirst()
    {
        var messages = await SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"martlet_guide"}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"martlet_guide","arguments":{"search":"latency"}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"martlet_guide","arguments":{"tool":"latency_report"}}}""",
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"martlet_guide","arguments":{"topic":"tools"}}}""");
        var initialized = messages[0].GetProperty("result");
        Assert.Equal("Martlet", initialized.GetProperty("serverInfo").GetProperty("title").GetString());
        Assert.NotEqual("0.1.0", initialized.GetProperty("serverInfo").GetProperty("version").GetString());
        var instructions = initialized.GetProperty("instructions").GetString()!;
        Assert.Contains("martlet_guide", instructions, StringComparison.Ordinal);
        Assert.Contains("character_create", instructions, StringComparison.Ordinal);
        Assert.Contains("--allow-changes", instructions, StringComparison.Ordinal);

        // The short list: the assistant's tools first, each with a title and hints; the rest run by name.
        var tools = messages[1].GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.InRange(tools.Length, 10, 40);
        Assert.Equal("martlet_guide", tools[0].GetProperty("name").GetString());
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("title").GetString()));
            Assert.Equal(tool.GetProperty("title").GetString(), tool.GetProperty("annotations").GetProperty("title").GetString());
        }
        var names = tools.Select(tool => tool.GetProperty("name").GetString()).ToArray();
        Assert.Subset(names.ToHashSet(), new HashSet<string?> { "martlet_call", "characters_list", "character_create", "character_update",
            "character_use", "character_delete", "settings_get", "settings_schema", "settings_set", "doctor_status", "ui_connect", "ui_snapshot", "ui_click" });
        Assert.DoesNotContain("thinking_pool_check", names);
        Assert.True(tools.Single(t => t.GetProperty("name").GetString() == "characters_list").GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.True(tools.Single(t => t.GetProperty("name").GetString() == "character_delete").GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        // ui_connect finds the running Martlet by itself.
        Assert.Equal(0, tools.Single(t => t.GetProperty("name").GetString() == "ui_connect").GetProperty("inputSchema").GetProperty("required").GetArrayLength());

        var guide = ToolResult(messages[2]);
        Assert.Contains("companion", guide.GetProperty("about").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("off", guide.GetProperty("server").GetProperty("changes").GetString(), StringComparison.Ordinal);
        Assert.Contains(guide.GetProperty("recipes").EnumerateArray(), recipe => recipe.GetProperty("task").GetString() == "Make a character");
        Assert.Contains(guide.GetProperty("areas").EnumerateArray(), area => area.GetProperty("area").GetString() == "Characters");

        var found = ToolResult(messages[3]).GetProperty("matches").EnumerateArray().ToArray();
        Assert.Equal("latency_report", found[0].GetProperty("name").GetString());
        Assert.False(found[0].GetProperty("listed").GetBoolean());

        var one = ToolResult(messages[4]);
        Assert.Equal("latency_report", one.GetProperty("tool").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, one.GetProperty("tool").GetProperty("inputSchema").ValueKind);
        Assert.Contains("martlet_call", one.GetProperty("run").GetString(), StringComparison.Ordinal);

        var areas = ToolResult(messages[5]).GetProperty("areas").EnumerateArray().ToArray();
        Assert.True(areas.Sum(area => area.GetProperty("tools").GetArrayLength()) >= 170);
        Assert.Contains(areas.SelectMany(area => area.GetProperty("tools").EnumerateArray()), tool => tool.GetProperty("name").GetString() == "ui_click");
    }

    [Fact]
    public async Task MartletCallRunsToolsThatAreNotListed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Call." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var call = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "martlet_call", arguments = new { tool = "character_status", arguments = new { dataDirectory = directory } } }
            });
            var messages = await SendAsync(call,
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"martlet_call","arguments":{"tool":"no_such_tool"}}}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"martlet_call","arguments":{"tool":"martlet_call"}}}""");
            Assert.Equal("none", ToolResult(messages[0]).GetProperty("personality").GetProperty("state").GetString());
            Assert.True(messages[1].GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Contains("martlet_guide", messages[1].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
            Assert.True(messages[2].GetProperty("result").GetProperty("isError").GetBoolean());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CharacterToolsMakeChangeSwitchAndDeleteCharactersOnlyWithAllowChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Characters." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string Call(int id, string name, object arguments) => JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments }
            });

            // Without --allow-changes nothing is saved.
            var refused = await SendAsync(Call(1, "character_create", new { name = "Ava", dataDirectory = directory }));
            Assert.True(refused[0].GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Contains("--allow-changes", refused[0].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(directory, "settings.json")));

            var messages = await SendWithAsync(true, false,
                Call(1, "characters_list", new { dataDirectory = directory }),
                Call(2, "character_create", new { name = "Ava", personality = "You are Ava, a cheerful astronomer.", look = "builtin", dataDirectory = directory }),
                Call(3, "character_create", new { name = "ava", dataDirectory = directory }),
                Call(4, "character_update", new { character = "Ava", name = "Ava Star", personality = "You are Ava Star.", dataDirectory = directory }),
                Call(5, "character_use", new { character = "Martlet", dataDirectory = directory }),
                Call(6, "characters_list", new { includeText = true, dataDirectory = directory }),
                Call(7, "character_create", new { name = "Bo", look = "no such look", dataDirectory = directory }),
                Call(8, "character_delete", new { character = "Ava Star", dataDirectory = directory }),
                Call(9, "characters_list", new { dataDirectory = directory }));

            var first = ToolResult(messages[0]);
            Assert.Equal("first-run", first.GetProperty("state").GetString());
            Assert.Equal("Martlet", first.GetProperty("active").GetProperty("personality").GetString());

            var created = ToolResult(messages[1]);
            Assert.Equal("Ava", created.GetProperty("created").GetProperty("name").GetString());
            Assert.Equal("builtin", created.GetProperty("created").GetProperty("profile").GetProperty("lookId").GetString());
            Assert.True(created.GetProperty("created").GetProperty("active").GetBoolean());
            Assert.Contains("CharacterProfileUse-", created.GetProperty("followUp").GetRawText(), StringComparison.Ordinal);

            Assert.True(messages[2].GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Equal("Ava Star", ToolResult(messages[3]).GetProperty("updated").GetProperty("profile").GetProperty("name").GetString());
            Assert.Equal("Martlet", ToolResult(messages[4]).GetProperty("active").GetProperty("personality").GetString());

            var listed = ToolResult(messages[5]);
            var ava = listed.GetProperty("characters").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Ava Star");
            Assert.Equal("You are Ava Star.", ava.GetProperty("personality").GetString());
            Assert.False(ava.GetProperty("active").GetBoolean());

            Assert.Contains("builtin", messages[6].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
            Assert.Equal("Ava Star", ToolResult(messages[7]).GetProperty("removed").GetProperty("personality").GetString());
            Assert.Equal(["Martlet"], ToolResult(messages[8]).GetProperty("characters").EnumerateArray().Select(c => c.GetProperty("name").GetString()));

            // The production settings store reads what the tools saved.
            var saved = await new Martlet.Core.Settings.SettingsStore(directory).LoadAsync();
            Assert.Equal(Martlet.Core.Settings.SettingsLoadState.Loaded, saved.State);
            Assert.Single(saved.Settings!.Companion!.Personas);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CharacterCreateImportsACharacterCardWithItsLorebook()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Card." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var card = Path.Combine(directory, "nova.json");
            await File.WriteAllTextAsync(card, JsonSerializer.Serialize(new
            {
                spec = "chara_card_v2", spec_version = "2.0",
                data = new
                {
                    name = "Nova", description = "{{char}} is a starship pilot.", personality = "Bold and kind.", scenario = "", first_mes = "Hi!",
                    mes_example = "", creator_notes = "", system_prompt = "", post_history_instructions = "", tags = Array.Empty<string>(),
                    creator = "", character_version = "", alternate_greetings = Array.Empty<string>(), extensions = new { },
                    character_book = new
                    {
                        name = "Nova lore", extensions = new { },
                        entries = new object[]
                        {
                            new { keys = new[] { "Orion" }, content = "Orion is Nova's ship.", extensions = new { }, enabled = true, insertion_order = 0,
                                constant = false, id = 1 }
                        }
                    }
                }
            }));
            var call = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "character_create", arguments = new { cardPath = card, dataDirectory = directory } }
            });
            var result = ToolResult((await SendWithAsync(true, false, call))[0]);
            output.WriteLine(result.ToString());
            Assert.Equal("Nova", result.GetProperty("created").GetProperty("name").GetString());
            Assert.Equal("Character Card V2", result.GetProperty("card").GetProperty("format").GetString());
            Assert.Equal(1, result.GetProperty("card").GetProperty("lorebook").GetProperty("entries").GetInt32());
            var saved = await new Martlet.Core.Settings.SettingsStore(directory).LoadAsync();
            Assert.Contains("Nova is a starship pilot.", saved.Settings!.Companion!.ActivePersona.Text, StringComparison.Ordinal);
            var lore = await new Martlet.Core.Lorebooks.LorebookStore(directory).LoadAsync();
            Assert.Equal(saved.Settings.Companion.ActivePersonaId, Assert.Single(lore.Library.Books).PersonaIds.Single());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SettingsToolsReadExplainAndChangeTheOwnersChoicesButNotSetup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Settings." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string Call(int id, string name, object arguments) => JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments }
            });
            var messages = await SendWithAsync(true, false,
                Call(1, "settings_get", new { dataDirectory = directory }),
                Call(2, "settings_schema", new { path = "generation" }),
                Call(3, "settings_set", new { path = "generation.temperature", value = 0.7, dataDirectory = directory }),
                Call(4, "settings_set", new { path = "generation.temperature", value = "hot", dataDirectory = directory }),
                Call(5, "settings_set", new { path = "setup.checkpoint", value = "done", dataDirectory = directory }),
                Call(6, "settings_set", new { path = "companion.personas[0].text", value = "Be brief.", dataDirectory = directory }),
                Call(7, "settings_set", new { path = "generation.temperature", value = (object?)null, dataDirectory = directory }),
                Call(8, "settings_set", new { path = "memory.enabled", value = false, revision = "stale", dataDirectory = directory }));

            var first = ToolResult(messages[0]);
            Assert.Equal("first-run", first.GetProperty("state").GetString());
            Assert.Contains(first.GetProperty("sections").EnumerateArray(), s => s.GetProperty("name").GetString() == "setup" && !s.GetProperty("changeable").GetBoolean());

            var schema = ToolResult(messages[1]).GetProperty("schema");
            Assert.True(schema.GetProperty("properties").TryGetProperty("temperature", out _));

            Assert.Equal(0.7, ToolResult(messages[2]).GetProperty("value").GetDouble());
            Assert.True(messages[3].GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Contains("generation.temperature", messages[3].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
            Assert.True(messages[4].GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Equal("Be brief.", ToolResult(messages[5]).GetProperty("value").GetString());
            Assert.Equal(JsonValueKind.Null, ToolResult(messages[6]).GetProperty("value").ValueKind);
            Assert.True(messages[7].GetProperty("result").GetProperty("isError").GetBoolean());

            var saved = await new Martlet.Core.Settings.SettingsStore(directory).LoadAsync();
            // Every value back at its default leaves the replies part out, as Martlet's pages save it.
            Assert.Null(saved.Settings!.Generation);
            Assert.Equal("Be brief.", saved.Settings.Companion!.Personas[0].Text);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ThinkingPoolCheckRehearsesPriorityStopsRaisesAndRetriesOnOneSlot()
    {
        var result = ToolResult((await SendAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"thinking_pool_check"}}"""))[0]);
        var steps = result.GetProperty("steps").EnumerateArray()
            .ToDictionary(step => step.GetProperty("name").GetString()!, step => (Passed: step.GetProperty("passed").GetBoolean(), Detail: step.GetProperty("detail").GetString()));
        foreach (var name in new[]
        {
            "priority stop: on one slot, a barge-in judge stops research and research runs again later",
            "priority stop off: the judge waits for research to end",
            "priority stop: the stopped job keeps its priority and goes before same-priority jobs that waited",
            "priority stop: a summary stopped for priority waits again and is not dropped",
            "priority raise: after every 2 stops the stopped job's priority goes up by 1",
            "priority raise: a job of the raised priority no longer stops it",
            "retries: a failed job is tried again at its priority; with 0 retries it fails",
            "retries: a timed-out job is tried again",
            "retries: a raised job is tried again at the priority it was left at",
            "per GPU: a card turned off stays off while the computer's other card keeps working, and comes back with its boxes"
        })
            Assert.True(steps[name].Passed, $"{name}: {steps[name].Detail}");
        var priority = result.GetProperty("priority");
        Assert.Equal(11, priority.GetProperty("raise").GetProperty("low").GetProperty("Priority").GetInt32());
        Assert.Equal(1, priority.GetProperty("retries").GetProperty("oneRetry").GetProperty("Retries").GetInt32());
        Assert.True(result.GetProperty("passed").GetBoolean(), result.GetRawText());
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
        Assert.Contains("tools: only a member that calls tools runs them", checkInSteps);
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
            roster = roster.SetNames(sam!.Id, "Samantha", [], "desk", now).SetOwner(sam.Id, true, "desk", now)
                .SetAccount(sam.Id, Guid.NewGuid(), owner: true, "desk", now);
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
            foreach (var secret in new[] { "canary", "Samantha", sam.Id, other.Id, "0123456789abcdef", Path.GetFileName(directory),
                         roster.Resolve(sam.Id)!.Account!.Value.ToString(), roster.Resolve(sam.Id)!.Account!.Value.ToString("N") })
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
                new[] { (sam.Tag, true, true, true, 2), (other.Tag, false, false, false, 1) },
                whose.GetProperty("voices").EnumerateArray().Select(v => (v.GetProperty("voice").GetString()!, v.GetProperty("named").GetBoolean(),
                    v.GetProperty("owner").GetBoolean(), v.GetProperty("linked").GetBoolean(), v.GetProperty("facts").GetInt32())).ToArray());
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
    public async Task MemoryStatusAndSyncStatusCountEachMemorySpaceWithoutFactText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.MemorySpaces." + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Martlet.Core.Settings.SettingsStore(directory);
            var initial = Martlet.Core.Settings.SetupSettings.Begin(null);
            var saved = await settings.SaveAsync(initial, null);
            var id = Guid.NewGuid();
            var space = Martlet.Core.Sync.MemorySpaceId.Account(id);
            using (var memory = new DesktopMemoryService(settings))
            {
                memory.UseAccount(new MemoryAccount(id, Martlet.Core.Sync.MemorySpaceFolders.AccountFolder(directory, id), directory));
                var configured = await memory.SaveConfigurationAsync(initial, saved.Revision, true,
                    Martlet.Core.Settings.MemoryStoragePolicy.AppLocalData, null);
                var revision = configured.Settings.Memory!.ConfigurationRevision;
                var forever = Martlet.Memory.MemoryRetention.UntilDeleted();
                await memory.SaveFactAsync(revision, "canary-1 is Sam's.", forever);
                await memory.SaveFactAsync(revision, "canary-2 is Sam's too.", forever);
                await memory.SaveFactAsync(revision, "canary-3 is the household's.", forever, space: Martlet.Core.Sync.MemorySpaceId.Household);
            }
            var state = Martlet.Core.Sync.MemorySpaceFolders.Sync(directory, space);
            Martlet.Core.Sync.MemorySyncState.Empty.Save(state);

            var message = (await SendAsync(SpaceCall("memory_status")))[0];
            Assert.DoesNotContain("canary", message.GetRawText(), StringComparison.Ordinal);
            var result = ToolResult(message);
            Assert.Equal("none", result.GetProperty("state").GetString());
            Assert.Equal(new[] { (space, 2), (Martlet.Core.Sync.MemorySpaceId.Household, 1) },
                result.GetProperty("spaces").EnumerateArray().Select(s => (s.GetProperty("space").GetString()!,
                    s.GetProperty("store").GetProperty("facts").GetInt32())).ToArray());

            var sync = ToolResult((await SendAsync(SpaceCall("memory_sync_status")))[0]);
            Assert.Equal("none", sync.GetProperty("state").GetString());
            Assert.Equal(space, sync.GetProperty("spaces").EnumerateArray().Single().GetProperty("space").GetString());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        string SpaceCall(string tool) => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = new { dataDirectory = directory } }
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
    public async Task HouseholdSharingReadsWhatEachAccountSharesWithoutNamesOrText()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.Sharing." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var none = ToolResult((await SendAsync(DataCall("household_sharing", directory)))[0]);
            Assert.Equal("none", none.GetProperty("state").GetString());

            var sam = Guid.NewGuid();
            var alex = Guid.NewGuid();
            var settings = Martlet.Core.Settings.CompanionSettings.Begin(null);
            var samsCompanion = settings.Companion!.Update(settings.Companion.ActivePersonaId, "Secret persona", "Secret text")
                .AddCharacter("Secret Aria", settings.Companion.ActivePersonaId, Martlet.Core.Settings.CharacterProfile.BuiltInModel, null, out var aria)
                .AddCharacter("Secret Bo", settings.Companion.ActivePersonaId, null, "voice-1", out var bo);
            var lore = Martlet.Core.Lorebooks.LorebookLibrary.Create();
            var samsSharing = Martlet.Core.Sharing.HouseholdSharing.Empty(sam)
                .WithMode(aria.Id, Martlet.Core.Sharing.CharacterShareMode.Together, samsCompanion, lore)
                .WithMode(bo.Id, Martlet.Core.Sharing.CharacterShareMode.Copy, samsCompanion, lore);
            var alexsSharing = Martlet.Core.Sharing.HouseholdSharing.Empty(alex).WithJoined(sam, aria.Id).WithNewFactsAboutMe(true);
            var document = Martlet.Core.Sync.SharedSettings.Empty
                .Put(Martlet.Core.Sharing.HouseholdSharing.Key(sam), samsSharing.Write(), null, "sams-pc", DateTimeOffset.UtcNow)
                .Put(Martlet.Core.Sharing.HouseholdSharing.Key(alex), alexsSharing.Write(), null, "alexs-pc", DateTimeOffset.UtcNow)
                .Put("sharing.00000000000000000000000000000001", "{\"schema_version\":9}", null, "old-pc", DateTimeOffset.UtcNow);
            Martlet.Core.Sync.SharedSettingsState.Save(directory, document, new Dictionary<string, string>());
            Directory.CreateDirectory(Path.Combine(directory, "accounts"));
            File.WriteAllText(Path.Combine(directory, "accounts", "session.json"), JsonSerializer.Serialize(new { current = alex }));
            // Alex's PC holds the mirror of Aria (same ID) in Alex's characters.
            var alexsCompanion = Martlet.Core.Sharing.SharedCharacters.Join(Martlet.Core.Settings.CompanionSettings.Create(), samsSharing.Find(aria.Id)!);
            File.WriteAllBytes(Path.Combine(directory, "settings.json"), Martlet.Core.Contracts.ContractJson.Write(settings with { Companion = alexsCompanion }));

            var message = (await SendAsync(DataCall("household_sharing", directory)))[0];
            Assert.DoesNotContain("Secret", message.GetRawText());
            var result = ToolResult(message);
            Assert.Equal(("loaded", alex.ToString("N"), 3, 1), (result.GetProperty("state").GetString(), result.GetProperty("currentAccount").GetString(),
                result.GetProperty("entries").GetInt32(), result.GetProperty("unreadable").GetInt32()));
            Assert.Equal((2, 0, 1, true), (result.GetProperty("householdCharacters").GetInt32(), result.GetProperty("ownShared").GetInt32(),
                result.GetProperty("ownJoined").GetInt32(), result.GetProperty("newFactsAboutMe").GetBoolean()));
            var accounts = result.GetProperty("accounts").EnumerateArray().ToArray();
            var sams = accounts.Single(a => a.GetProperty("account").GetString() == sam.ToString("N"));
            Assert.Equal((1, 1, "sams-pc"), (sams.GetProperty("copy").GetInt32(), sams.GetProperty("together").GetInt32(), sams.GetProperty("updatedBy").GetString()));
            var shared = sams.GetProperty("characters").EnumerateArray().Single(c => c.GetProperty("key").GetString() == aria.Key);
            Assert.Equal(("together", "character-" + aria.Id.ToString("N"), "builtin"),
                (shared.GetProperty("mode").GetString(), shared.GetProperty("space").GetString(), shared.GetProperty("look").GetString()));
            var joined = accounts.Single(a => a.GetProperty("account").GetString() == alex.ToString("N")).GetProperty("joinedCharacters")[0];
            Assert.Equal((aria.Key, true), (joined.GetProperty("key").GetString(), joined.GetProperty("stillShared").GetBoolean()));

            var profiles = ToolResult((await SendAsync(DataCall("character_profiles", directory)))[0]).GetProperty("profiles").EnumerateArray().ToArray();
            Assert.Equal("joined", profiles.Single(p => p.GetProperty("key").GetString() == aria.Key).GetProperty("sharing").GetString());
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

    [Fact]
    public async Task VoicesStatusSharesPeopleWithYourHostsWhateverTheSyncSwitchSays()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Martlet.Mcp.VoiceSharing." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var none = ToolResult((await SendAsync(DataCall("voices_status", directory)))[0]).GetProperty("sharing");
            Assert.Equal("no paired hosts yet", none.GetProperty("state").GetString());
            Assert.True(none.GetProperty("always").GetBoolean());

            File.WriteAllText(Path.Combine(directory, "cluster-sync.txt"), "off");
            File.WriteAllText(Path.Combine(directory, "hosts.json"), JsonSerializer.Serialize(new
            {
                version = 1,
                hosts = new object[]
                {
                    new { pairing = new { hostId = "home-host" } },
                    new { pairing = new { hostId = "friends-host" }, access = "friend" }
                }
            }));
            var sharing = ToolResult((await SendAsync(DataCall("voices_status", directory)))[0]).GetProperty("sharing");
            Assert.Equal("on", sharing.GetProperty("state").GetString());
            Assert.Equal(1, sharing.GetProperty("hosts").GetInt32());
            Assert.Equal(1, sharing.GetProperty("friendHostsNeverUsed").GetInt32());

            // Voices linked to people's accounts are counted, never named or identified.
            var random = new Random(7);
            float[] Print() => Martlet.Core.Speakers.VoicePrints.Normalize(Enumerable.Range(0, Martlet.Core.Speakers.VoicePrints.Dimension)
                .Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
            var account = Guid.NewGuid();
            var roster = Martlet.Core.Speakers.VoiceRoster.Empty;
            for (var i = 0; i < 3; i++) roster = roster.Add(Print(), 3, "desk", DateTimeOffset.UtcNow).Roster;
            roster = roster.SetAccount(roster.Live[0].Id, account, true, "desk", DateTimeOffset.UtcNow)
                .SetAccount(roster.Live[1].Id, account, true, "desk", DateTimeOffset.UtcNow);
            File.WriteAllBytes(Path.Combine(directory, "voices.json"), roster.Write());
            var message = (await SendAsync(DataCall("voices_status", directory)))[0];
            Assert.DoesNotContain(account.ToString("N"), message.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(account.ToString(), message.GetRawText(), StringComparison.OrdinalIgnoreCase);
            var counts = ToolResult(message).GetProperty("roster");
            Assert.Equal(3, counts.GetProperty("voices").GetInt32());
            Assert.Equal(2, counts.GetProperty("linked").GetInt32());
            Assert.Equal(1, counts.GetProperty("accounts").GetInt32());
            Assert.Equal(2, counts.GetProperty("owner").GetInt32());
            Assert.True(!counts.TryGetProperty("yours", out var before) || before.ValueKind == JsonValueKind.Null);
            Martlet.Core.Accounts.AccountSessionState.For("S-1-5-21-1000-1000-1000-1001", account).Save(directory);
            var signedIn = ToolResult((await SendAsync(DataCall("voices_status", directory)))[0]).GetProperty("roster");
            Assert.Equal(2, signedIn.GetProperty("yours").GetInt32());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

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

    private static Task<JsonElement[]> SendAsync(params string[] requests) => SendWithAsync(false, false, requests);

    private static async Task<JsonElement[]> SendWithAsync(bool allowChanges, bool allTools, params string[] requests)
    {
        using var reader = new StringReader(string.Join('\n', requests) + "\n");
        using var writer = new StringWriter();
        await new McpServer(new DesktopAutomation(false), allowChanges, allTools).RunAsync(reader, writer, CancellationToken.None);
        return writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();
    }
}
