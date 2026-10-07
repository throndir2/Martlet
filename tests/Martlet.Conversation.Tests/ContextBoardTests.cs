using System.Text;
using Martlet.Conversation;

namespace Martlet.Conversation.Tests;

public sealed class ContextBoardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Snapshot_keeps_fresh_notes_in_a_stable_source_order_and_drops_stale_ones()
    {
        var board = new ContextBoard();
        board.Post("zeta", "A custom note.", Now, TimeSpan.FromMinutes(1));
        board.Post(ContextBoard.Touch, "A pat.", Now, TimeSpan.FromMinutes(1));
        board.Post(ContextBoard.Sound, "Old music.", Now.AddSeconds(-40), TimeSpan.FromSeconds(30));
        board.Post(ContextBoard.Screen, "A browser.", Now, TimeSpan.FromMinutes(1));
        board.Post(ContextBoard.Character, "Showing glasses.", Now, TimeSpan.FromMinutes(1));
        board.Post("alpha", "Another note.", Now, TimeSpan.FromMinutes(1));

        var snapshot = board.Snapshot(Now);
        Assert.Equal([ContextBoard.Character, ContextBoard.Screen, ContextBoard.Touch, "alpha", "zeta"], snapshot.Sources);
        Assert.Equal("Showing glasses.\nA browser.\nA pat.\nAnother note.\nA custom note.", snapshot.Text);
        // The stale note was dropped from the board too.
        Assert.False(board.Clear(ContextBoard.Sound));
        Assert.Empty(new ContextBoard().Snapshot(Now).Notes);
        Assert.Null(new ContextBoard().Snapshot(Now).Text);
    }

    [Fact]
    public void A_new_post_replaces_the_source_note_and_empty_text_clears_it()
    {
        var board = new ContextBoard();
        var changes = 0;
        board.Changed += () => changes++;
        board.Post(ContextBoard.Screen, "First.", Now, TimeSpan.FromMinutes(1));
        board.Post(ContextBoard.Screen, "  Second\r\n\tline.  ", Now, TimeSpan.FromMinutes(1));
        Assert.Equal("Second line.", Assert.Single(board.Snapshot(Now).Notes).Text);
        Assert.Null(board.Post(ContextBoard.Screen, "   ", Now, TimeSpan.FromMinutes(1)));
        Assert.Empty(board.Snapshot(Now).Notes);
        Assert.Equal(3, changes);
        Assert.Throws<ArgumentException>(() => board.Post("Screen", "x", Now, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => board.Post("screen", "x", Now, TimeSpan.Zero));
        Assert.Equal(ContextBoard.MaximumAge, board.Post("screen", "x", Now, TimeSpan.FromDays(3))!.MaxAge);
    }

    [Fact]
    public void A_consumed_note_goes_with_one_sent_request_only_unless_a_newer_one_came()
    {
        var board = new ContextBoard();
        board.Post(ContextBoard.Touch, "A pat.", Now, TimeSpan.FromMinutes(1), consume: true, kept: " (touch:\n pat) ");
        Assert.Equal("(touch: pat)", board.Snapshot(Now).KeptText);
        board.Post(ContextBoard.Screen, "A browser.", Now, TimeSpan.FromMinutes(1));

        // Taking a snapshot consumes nothing and tells no one (a turn stopped before its request was sent).
        var delivered = new List<ContextBoardSnapshot>();
        board.Sent += delivered.Add;
        _ = board.Snapshot(Now);
        Assert.Empty(delivered);
        var first = board.Snapshot(Now);
        Assert.Contains(ContextBoard.Touch, first.Sources);
        Assert.Equal(1, board.MarkSent(first));
        Assert.Same(first, Assert.Single(delivered));
        Assert.Equal([ContextBoard.Screen, ContextBoard.Touch], board.LastSent.Sources);
        Assert.Equal([ContextBoard.Screen], board.Snapshot(Now).Sources);
        Assert.Equal(0, board.MarkSent(first));

        // A newer touch posted while a request was being sent stays for the next one.
        board.Post(ContextBoard.Touch, "A stroke.", Now, TimeSpan.FromMinutes(1), consume: true);
        var taken = board.Snapshot(Now);
        board.Post(ContextBoard.Touch, "A drag.", Now, TimeSpan.FromMinutes(1), consume: true);
        Assert.Equal(0, board.MarkSent(taken));
        Assert.Equal("A browser.\nA drag.", board.Snapshot(Now).Text);
    }

    [Fact]
    public void Notes_and_snapshots_stay_within_their_size_caps()
    {
        var board = new ContextBoard();
        var note = board.Post(ContextBoard.Screen, new string('é', 1_000), Now, TimeSpan.FromMinutes(1))!;
        Assert.True(Encoding.UTF8.GetByteCount(note.Text) <= ContextBoard.MaximumNoteUtf8Bytes);
        Assert.EndsWith("...", note.Text);
        for (var i = 0; i < 6; i++) board.Post($"source-{i}", new string('a', 590), Now, TimeSpan.FromMinutes(1));
        var snapshot = board.Snapshot(Now);
        Assert.True(snapshot.Utf8Bytes <= ContextBoard.MaximumUtf8Bytes);
        Assert.Equal(ContextBoard.Screen, snapshot.Sources[0]);
        Assert.Equal(3, snapshot.Notes.Count);
        for (var i = 6; i < ContextBoard.MaximumSources - 1; i++) board.Post($"source-{i}", "x", Now, TimeSpan.FromMinutes(1));
        Assert.Throws<InvalidOperationException>(() => board.Post("one-too-many", "x", Now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_moment_names_its_context_notes()
    {
        Assert.Equal("your words and 1 context note", MomentTurn.Describe(true, 0, false, null, 0, contextNotes: 1));
        Assert.Equal("the picture and 3 context notes", MomentTurn.Describe(false, 0, true, null, 0, contextNotes: 3));
        Assert.Equal("your words", MomentTurn.Describe(true, 0, false, null, 0));
    }
}
