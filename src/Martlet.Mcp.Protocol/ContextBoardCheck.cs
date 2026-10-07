using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Mcp.Shared;
using Martlet.Providers;

namespace Martlet.Mcp;

/// <summary>context_board: the context board (<see cref="ContextBoard"/>) as the live conversation uses it, rehearsed with the
/// production board, request contract (<see cref="BoundedTextInput.Context"/>), Chat Completions adapter and the desktop's
/// conversation buffer (<see cref="ConversationContextBuffer"/>) against a fixture endpoint on 127.0.0.1 (canned reply, NOT AI).
/// Fixture notes from four sources are posted (one stale, one consumed on read), plus an optional caller's test note; two
/// requests in a row are sent and captured. It shows which notes each request carried (after Martlet's other notes, at the
/// end of the user's message), that a stale note is skipped, that a consumed note goes with one request only, and that the
/// conversation keeps the message without the board's notes, so the next request starts like the one before.</summary>
internal static class ContextBoardCheck
{
    internal static async Task<object> RunAsync(string? source, string? text, int? maxAgeSeconds, bool? consume, int? ageSeconds,
        CancellationToken cancellation)
    {
        if (text is not null && (source is null || !ContextBoard.IsSource(source)))
            throw new ArgumentException("source is 1-32 lower-case letters, digits or '-'.");
        if (maxAgeSeconds is < 1 or > 3600 || ageSeconds is < 0 or > 7200)
            throw new ArgumentException("maxAgeSeconds is 1-3600 and ageSeconds 0-7200.");
        var board = new ContextBoard();
        var now = DateTimeOffset.Now;
        board.Post(ContextBoard.Character, "Your character is showing {glasses} (12 min). FIXTURE note.", now, TimeSpan.FromMinutes(1));
        board.Post(ContextBoard.Screen, "Screen over the last 20 s: a code editor, then a browser. FIXTURE note.", now.AddSeconds(-5), TimeSpan.FromSeconds(30));
        board.Post(ContextBoard.Sound, "This PC played upbeat music. FIXTURE note, stale.", now.AddSeconds(-60), TimeSpan.FromSeconds(30));
        board.Post(ContextBoard.Touch, "The user patted the character's head twice. FIXTURE note, consumed on read.", now.AddSeconds(-2),
            TimeSpan.FromMinutes(2), consume: true);
        ContextNote? posted = null;
        if (text is not null)
            posted = board.Post(source!, text, now.AddSeconds(-(ageSeconds ?? 0)), TimeSpan.FromSeconds(maxAgeSeconds ?? 60), consume == true);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var baseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
        var requests = new List<byte[]>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var serving = ContextCheck.ServeCacheAsync(listener, requests, stop.Token);
        try
        {
            using var adapter = ChatCompletionsTextGenerationAdapter.Create(baseUrl);
            const string instructions = "Use the user-selected companion persona below for conversational tone.\n\nCompanion name: Martlet (fixture)";
            var buffer = new ConversationContextBuffer();
            static string? Block(ContextBoardSnapshot snapshot) =>
                snapshot.Text is { } notes ? "[MARTLET_NOTES]\n" + notes + "\n[/MARTLET_NOTES]" : null;

            var firstSnapshot = board.Snapshot(now);
            var first = new BoundedTextInput("What do you think of this?", instructions, buffer.Snapshot(sent: true),
                notes: "[MARTLET_NOTES]\nFixture notes: lore entry A.\n[/MARTLET_NOTES]", context: Block(firstSnapshot));
            var firstAnswer = await ContextCheck.AskAsync(adapter, baseUrl, first, cancellation);
            var consumed = board.MarkSent(firstSnapshot);
            buffer.Add(first.UserText, "Fixture reply (not AI).", first.KeptUserText);

            var later = now.AddSeconds(1);
            var secondSnapshot = board.Snapshot(later);
            var second = new BoundedTextInput("And now?", instructions, buffer.Snapshot(sent: true), context: Block(secondSnapshot));
            var secondAnswer = await ContextCheck.AskAsync(adapter, baseUrl, second, cancellation);
            board.MarkSent(secondSnapshot);

            byte[][] bodies;
            lock (requests) bodies = [.. requests];
            if (bodies.Length < 2) return new { ok = false, problem = "the fixture endpoint got fewer than two requests" };
            var (one, two) = (ContextCheck.Messages(bodies[0]), ContextCheck.Messages(bodies[1]));
            static string Content(string message) => JsonDocument.Parse(message).RootElement.GetProperty("content").GetString() ?? "";
            var firstUser = Content(one[^1]);
            var secondUser = Content(two[^1]);
            // The second request carries the first message as the conversation kept it: the start of what was sent.
            var keptAgain = Content(two[^3]);
            var keptIsStart = firstUser.StartsWith(keptAgain, StringComparison.Ordinal) && keptAgain == first.KeptUserText;
            var keptHasBoard = keptAgain.Contains("FIXTURE note", StringComparison.Ordinal);
            var sharedMessages = 0;
            while (sharedMessages < one.Length - 1 && sharedMessages < two.Length && one[sharedMessages] == two[sharedMessages]) sharedMessages++;
            var boardAfterNotes = firstUser.IndexOf("lore entry A", StringComparison.Ordinal) is var lore and >= 0 &&
                firstUser.IndexOf("FIXTURE note", StringComparison.Ordinal) > lore && firstUser.EndsWith(first.Context!, StringComparison.Ordinal);
            string[] Carried(string user) => [.. new[] { ContextBoard.Character, ContextBoard.Screen, ContextBoard.Sound, ContextBoard.Touch }
                .Where(s => user.Contains(s switch
                {
                    ContextBoard.Character => "{glasses}", ContextBoard.Screen => "code editor", ContextBoard.Sound => "upbeat music",
                    _ => "patted"
                }, StringComparison.Ordinal))];
            var firstCarried = Carried(firstUser);
            var secondCarried = Carried(secondUser);
            var staleSkipped = !firstCarried.Contains(ContextBoard.Sound) && !firstSnapshot.Sources.Contains(ContextBoard.Sound);
            var consumedOnce = firstCarried.Contains(ContextBoard.Touch) && !secondCarried.Contains(ContextBoard.Touch) && consumed >= 1;
            var orderStable = firstSnapshot.Sources.Take(3).SequenceEqual([ContextBoard.Character, ContextBoard.Screen, ContextBoard.Touch]);
            var postedFirst = posted is not null && firstSnapshot.Notes.Any(n => n.Version == posted.Version);
            var postedSecond = posted is not null && secondSnapshot.Notes.Any(n => n.Version == posted.Version);
            var ok = firstAnswer.Outcome == "Completed" && secondAnswer.Outcome == "Completed" && keptIsStart && !keptHasBoard &&
                sharedMessages == one.Length - 1 && boardAfterNotes && staleSkipped && consumedOnce && orderStable &&
                secondCarried.Contains(ContextBoard.Screen);
            return new
            {
                ok,
                note = "The production context board and Chat Completions adapter against a fixture endpoint on 127.0.0.1 (canned reply, " +
                    "NOT AI), with FIXTURE notes (NOT anything seen, heard or touched).",
                firstRequest = new { notes = firstSnapshot.Sources, bytes = firstSnapshot.Utf8Bytes, carried = firstCarried, outcome = firstAnswer.Outcome },
                secondRequest = new { notes = secondSnapshot.Sources, bytes = secondSnapshot.Utf8Bytes, carried = secondCarried, outcome = secondAnswer.Outcome },
                staleSkipped, consumedOnce, orderStable,
                boardAt = boardAfterNotes ? "end of the user's message, after Martlet's other notes" : "elsewhere",
                historyKeepsBoardNotes = keptHasBoard,
                keptMessageIsStartOfSent = keptIsStart,
                messagesSentAgainUnchanged = sharedMessages, messagesBeforeTheMessage = one.Length - 1,
                lastSent = board.LastSent.Sources,
                posted = posted is null ? null : new
                {
                    posted.Source, posted.Text, ageSeconds = ageSeconds ?? 0, maxAgeSeconds = (int)posted.MaxAge.TotalSeconds,
                    posted.Consume, inFirstRequest = postedFirst, inSecondRequest = postedSecond
                },
                limits = new
                {
                    noteBytes = ContextBoard.MaximumNoteUtf8Bytes, totalBytes = ContextBoard.MaximumUtf8Bytes,
                    sources = ContextBoard.MaximumSources, maxAgeSeconds = (int)ContextBoard.MaximumAge.TotalSeconds
                }
            };
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await serving; } catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }
}
