using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Martlet.Audio;
using Martlet.Conversation;
using Martlet.Core.Contracts;
using Martlet.Core.Reading;
using Martlet.Core.Settings;
using Martlet.Diagnostics;
using Martlet.Providers;

namespace Martlet.Desktop;

public enum ChatRole { User, Martlet, Note, PcAudio, Work }

/// <summary>One entry in the talk window's history: what you typed or said, Martlet's reply, what Martlet heard playing on the PC
/// (marked, never shown as you), a short note, or a work card (Martlet started research, an app guide, a song or a picture).</summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    private string text;
    private string note = "";
    private string workLine = "";

    internal ChatMessage(ChatRole role, string text, string caption = "")
    {
        Role = role;
        this.text = text;
        Caption = caption;
    }

    public ChatRole Role { get; }
    public bool IsUser => Role == ChatRole.User;
    public bool IsNote => Role == ChatRole.Note;
    public bool IsPcAudio => Role == ChatRole.PcAudio;
    public bool IsWork => Role == ChatRole.Work;
    /// <summary>A work card's background job ID (such as research-1): its automation IDs are LiveWork-&lt;id&gt; (the line MCP
    /// reads) and LiveWorkTasks-&lt;id&gt; (Show tasks).</summary>
    public string WorkJob { get; init; } = "";
    public string WorkId => "LiveWork-" + WorkJob;
    public string WorkTasksId => "LiveWorkTasks-" + WorkJob;
    /// <summary>A work card's icon (Segoe Fluent Icons).</summary>
    public string WorkGlyph { get; init; } = "";
    /// <summary>A work card's line, such as "Research started" or "Research done after 1:02": the kind and the state only,
    /// never what the work is about (that is <see cref="Text"/>).</summary>
    public string WorkLine { get => workLine; set => Set(ref workLine, value); }
    public string Caption { get; }
    public string Text { get => text; set => Set(ref text, value); }
    public string Note { get => note; set { if (Set(ref note, value)) Changed(nameof(HasNote)); } }
    public bool HasNote => note.Length > 0;
    /// <summary>A picture Martlet shows with this message (draw_picture, or perform_creation showing one again).</summary>
    public System.Windows.Media.ImageSource? Picture { get; init; }
    public bool HasPicture => Picture is not null;
    /// <summary>What was sent to the Thinking model to make this message (What was sent), or null when that isn't known here (a
    /// message from before, a picture, a note) or was let go to save memory. In memory only, never saved or logged.</summary>
    internal MessageContext? Context
    {
        get => context;
        set
        {
            if (ReferenceEquals(context, value)) return;
            var had = context is not null;
            context = value;
            if (had != (value is not null)) Changed(nameof(HasContext));
        }
    }
    public bool HasContext => context is not null;
    private MessageContext? context;
    /// <summary>The number of this message's What was sent link (1 for the first message that kept what was sent, then 2...), so
    /// each link has its own automation ID (<see cref="ContextId"/>); 0 before it has one.</summary>
    public int ContextNumber
    {
        get => contextNumber;
        internal set
        {
            if (contextNumber == value) return;
            contextNumber = value;
            Changed(nameof(ContextNumber));
            Changed(nameof(ContextId));
            Changed(nameof(TokensId));
        }
    }
    public string ContextId => $"LiveMessageContext-{contextNumber}";
    private int contextNumber;
    /// <summary>The token line under the message (<see cref="MessageContext.TokensLine"/>): the input tokens its Thinking requests
    /// read and the estimated tokens of Martlet's instructions. Numbers only; it stays after What was sent is let go.</summary>
    public string Tokens { get => tokens; internal set { if (Set(ref tokens, value)) Changed(nameof(HasTokens)); } }
    public bool HasTokens => tokens.Length > 0;
    public string TokensId => $"LiveMessageTokens-{contextNumber}";
    private string tokens = "";
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void AddNote(string line) => Note = note.Length == 0 ? line : note + "  " + line;

    private bool Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return false;
        field = value;
        Changed(name);
        return true;
    }

    private void Changed(string? name) => PropertyChanged?.Invoke(this, new(name));
}
