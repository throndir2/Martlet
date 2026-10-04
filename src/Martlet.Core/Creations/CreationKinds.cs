using System.Text.Json;
using Martlet.Core.Contracts;

namespace Martlet.Core.Creations;

/// <summary>One asset a kind's creations have: its name, the media types it may be, its size limit and whether every
/// creation of the kind must have it.</summary>
public sealed record CreationAssetRule(string Name, IReadOnlyList<string> MediaTypes, long MaximumBytes, bool Required = true);

/// <summary>A titled block of a creation's readable text for the Creations page (a song's verse, chorus...).</summary>
public sealed record CreationSection(string Heading, string Text);

/// <summary>A kind of creation, declared by the feature that makes it (songs by singing): its assets and their size limits,
/// whether Martlet may clean old ones up when space is needed, how it is described to the Thinking model and on the Creations
/// page, and what <c>perform_creation</c>'s options mean for it. How it is performed, shown or activated is an
/// <see cref="ICreationHandler"/> the feature attaches with <see cref="CreationRegistry.Handle"/>.</summary>
public sealed record CreationKind
{
    /// <summary>The kind's name in the list, such as <c>song</c> (<see cref="CreationLibrary.IsName"/>).</summary>
    public required string Name { get; init; }
    /// <summary>What one is called, in lower case: "song".</summary>
    public required string Noun { get; init; }
    /// <summary>What many are called, in lower case: "songs".</summary>
    public required string Plural { get; init; }
    /// <summary>What Martlet does with one, as a verb: "sing", "show" or "activate".</summary>
    public required string Verb { get; init; }
    /// <summary>The Segoe Fluent Icons glyph the Creations page shows for it.</summary>
    public string Glyph { get; init; } = "\uE7C3";
    /// <summary>The version of the kind's <see cref="Creation.Metadata"/> new creations get.</summary>
    public int KindVersion { get; init; } = 1;
    public required IReadOnlyList<CreationAssetRule> Assets { get; init; }
    /// <summary>One creation's size limit, its assets together.</summary>
    public long MaximumBytes { get; init; } = CreationLibrary.MaximumCreationBytes;
    /// <summary>Whether Martlet may delete the oldest of them by itself when a new creation needs the room.</summary>
    public bool AutoCleanup { get; init; }
    /// <summary>One sentence for <c>perform_creation</c>'s description on what its options mean for this kind, or null when it
    /// takes none. It is part of every request's tools, so keep it short and fixed.</summary>
    public string? OptionsHint { get; init; }
    /// <summary>A short description of one creation for the Thinking model (<c>list_creations</c>); its summary when null.</summary>
    public Func<Creation, string>? Describe { get; init; }
    /// <summary>Its readable text in blocks for the Creations page; the creation's <see cref="Creation.Text"/> when null.</summary>
    public Func<Creation, IReadOnlyList<CreationSection>>? Details { get; init; }

    public string DescribeFor(Creation creation)
    {
        try { return Describe?.Invoke(creation) ?? Default(creation); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Default(creation);
        }
    }

    public IReadOnlyList<CreationSection> DetailsFor(Creation creation)
    {
        try { return Details?.Invoke(creation) ?? Sections(creation); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Sections(creation);
        }
    }

    /// <summary>A creation's text as one block, for kinds without their own <see cref="Details"/> and kinds this Martlet
    /// doesn't know.</summary>
    public static IReadOnlyList<CreationSection> Sections(Creation creation) =>
        string.IsNullOrWhiteSpace(creation.Text) ? [] : [new("", creation.Text.Trim())];

    private string Default(Creation creation) => string.IsNullOrWhiteSpace(creation.Summary) ? Noun : creation.Summary;

    /// <summary>Throws <see cref="ContractException"/> when <paramref name="assets"/> don't follow this kind's rules.</summary>
    public void Check(IReadOnlyList<(string Name, string MediaType, long Bytes)> assets)
    {
        ContractRules.Require(CreationLibrary.IsName(Name), "A creation kind's name is invalid.");
        foreach (var rule in Assets.Where(r => r.Required))
            ContractRules.Require(assets.Any(a => a.Name == rule.Name), $"A {Noun} needs its {rule.Name}.");
        foreach (var (name, mediaType, bytes) in assets)
        {
            var rule = Assets.FirstOrDefault(r => r.Name == name);
            ContractRules.Require(rule is not null, $"A {Noun} has no part called {name}.");
            ContractRules.Require(rule!.MediaTypes.Contains(mediaType), $"A {Noun}'s {name} can't be {mediaType}.");
            ContractRules.Require(bytes <= rule.MaximumBytes, $"A {Noun}'s {name} is too large.", ErrorCode.PayloadTooLarge);
        }
        ContractRules.Require(assets.Sum(a => a.Bytes) <= MaximumBytes, $"The {Noun} is too large.", ErrorCode.PayloadTooLarge);
    }
}

/// <summary>A request to perform, show or activate a creation: the creation, the options the Thinking model passed (a JSON
/// object, empty when none) and its assets on this computer.</summary>
public sealed record CreationAction(Creation Creation, JsonElement Options, ICreationAssets Assets);

/// <summary>What happened, in words for the Thinking model (it tells the user); <see cref="IsError"/> when nothing started.</summary>
public sealed record CreationActionResult(string Text, bool IsError = false);

/// <summary>Performs, shows or activates creations of one kind. The feature that owns the kind attaches it (for songs, the
/// conversation that sings them). It is called from Martlet's <c>perform_creation</c> tool during a reply, so it should start
/// the work and return at once (a song plays on while the reply continues).</summary>
public interface ICreationHandler
{
    ValueTask<CreationActionResult> PerformAsync(CreationAction action, CancellationToken cancellationToken);
}

/// <summary>A creation's assets as this computer holds them.</summary>
public interface ICreationAssets
{
    /// <summary>Whether every asset is here (false while a copy from another computer is still on its way).</summary>
    bool IsComplete { get; }

    /// <summary>The bytes of the asset called <paramref name="name"/>, checked against its SHA-256, or null when this computer
    /// doesn't hold it (yet).</summary>
    ValueTask<byte[]?> ReadAsync(string name, CancellationToken cancellationToken);
}

/// <summary>The kinds of creation this Martlet knows and the handlers attached to them. Features register their kind at
/// startup (<see cref="Register"/>), before any conversation, so Martlet's creation tools are offered the same way in every
/// request; handlers come and go with what can perform them (<see cref="Handle"/>).</summary>
public sealed class CreationRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, CreationKind> kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ICreationHandler> handlers = new(StringComparer.Ordinal);

    /// <summary>The registry the desktop uses.</summary>
    public static CreationRegistry Shared { get; } = new();

    public event Action? Changed;

    /// <summary>Every registered kind, by name.</summary>
    public IReadOnlyList<CreationKind> Kinds
    {
        get { lock (gate) return kinds.Values.OrderBy(k => k.Name, StringComparer.Ordinal).ToArray(); }
    }

    public bool HasKinds
    {
        get { lock (gate) return kinds.Count > 0; }
    }

    /// <summary>Registers a kind. Registering the same kind again does nothing; another kind with the same name throws.</summary>
    public void Register(CreationKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ContractRules.Require(CreationLibrary.IsName(kind.Name), "A creation kind's name is invalid.");
        ContractRules.Require(kind.KindVersion is >= 1 and <= CreationLibrary.MaximumKindVersion && kind.Assets.Count <= CreationLibrary.MaximumAssets &&
            kind.Assets.All(r => CreationLibrary.IsName(r.Name) && r.MaximumBytes is > 0 and <= CreationLibrary.MaximumAssetBytes &&
                r.MediaTypes.Count > 0 && r.MediaTypes.All(CreationLibrary.MediaTypes.Contains)) &&
            kind.Assets.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() == kind.Assets.Count &&
            kind.MaximumBytes is > 0 and <= CreationLibrary.MaximumCreationBytes, "A creation kind's rules are invalid.");
        lock (gate)
        {
            if (kinds.TryGetValue(kind.Name, out var existing))
            {
                if (ReferenceEquals(existing, kind) || existing == kind) return;
                throw new InvalidOperationException($"Another creation kind is already called {kind.Name}.");
            }
            kinds[kind.Name] = kind;
        }
        Changed?.Invoke();
    }

    public CreationKind? Find(string? name)
    {
        if (name is null) return null;
        lock (gate) return kinds.GetValueOrDefault(name);
    }

    /// <summary>Attaches what performs, shows or activates creations of <paramref name="kind"/>, replacing any before it, until
    /// the returned handle is disposed.</summary>
    public IDisposable Handle(string kind, ICreationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ContractRules.Require(CreationLibrary.IsName(kind), "A creation kind's name is invalid.");
        lock (gate) handlers[kind] = handler;
        Changed?.Invoke();
        return new Attachment(this, kind, handler);
    }

    public ICreationHandler? HandlerFor(string? kind)
    {
        if (kind is null) return null;
        lock (gate) return handlers.GetValueOrDefault(kind);
    }

    private void Detach(string kind, ICreationHandler handler)
    {
        bool removed;
        lock (gate) removed = handlers.TryGetValue(kind, out var current) && ReferenceEquals(current, handler) && handlers.Remove(kind);
        if (removed) Changed?.Invoke();
    }

    private sealed class Attachment(CreationRegistry registry, string kind, ICreationHandler handler) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) registry.Detach(kind, handler);
        }
    }
}

/// <summary>An <see cref="ICreationHandler"/> from a function.</summary>
public sealed class CreationHandler(Func<CreationAction, CancellationToken, ValueTask<CreationActionResult>> perform) : ICreationHandler
{
    public ValueTask<CreationActionResult> PerformAsync(CreationAction action, CancellationToken cancellationToken) => perform(action, cancellationToken);
}
