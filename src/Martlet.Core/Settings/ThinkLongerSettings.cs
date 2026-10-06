using System.Text.Json.Serialization;
using Martlet.Core.Contracts;

namespace Martlet.Core.Settings;

/// <summary>How hard a background think reasons (Companion › Replies › Thinking longer › How hard it thinks).</summary>
public enum ThinkEffort { Medium, High }

/// <summary>When a finished background job is brought up: as soon as Martlet is free (it starts a reply of its own), or with
/// the next thing you say.</summary>
public enum ThinkDelivery { WhenFree, NextMessage }

/// <summary>Companion › Replies › Thinking longer: whether Martlet may decide, sparingly, that a task needs real thinking and
/// work it out in the background with the think_longer tool while the conversation carries on, how hard it thinks and when it
/// shares the result. A think has no time limit and no hourly limit: it runs until it is done or canceled. Saved inside
/// <see cref="GenerationSettings"/>, so it travels with the reply settings; every value is null until changed (on by default,
/// Medium, as soon as Martlet is free).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ThinkLongerSettings : IContract
{
    public const bool DefaultEnabled = true;
    public const ThinkEffort DefaultEffort = ThinkEffort.Medium;
    public const ThinkDelivery DefaultDelivery = ThinkDelivery.WhenFree;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkEffort? Effort { get; init; }
    /// <summary>The time limit older versions saved; read so those settings still load, then dropped (there is no limit).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Minutes { get; init; }
    /// <summary>The hourly limit older versions saved; read so those settings still load, then dropped (there is no limit).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PerHour { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkDelivery? Delivery { get; init; }

    [JsonIgnore] public bool IsDefault => Enabled is null && Effort is null && Minutes is null && PerHour is null && Delivery is null;
    [JsonIgnore] public bool On => Enabled ?? DefaultEnabled;
    [JsonIgnore] public ThinkEffort HowHard => Effort ?? DefaultEffort;
    [JsonIgnore] public ThinkDelivery When => Delivery ?? DefaultDelivery;

    /// <summary>The settings in effect for <paramref name="settings"/> (unset is the default for every value).</summary>
    public static ThinkLongerSettings Of(GenerationSettings? settings) => settings?.ThinkLonger ?? new();

    /// <summary>Null when every value is the default, so untouched settings keep their saved shape.</summary>
    public static ThinkLongerSettings? Normalize(ThinkLongerSettings? settings)
    {
        if (settings is null) return null;
        var lean = settings with
        {
            Enabled = settings.Enabled == DefaultEnabled ? null : settings.Enabled,
            Effort = settings.Effort == DefaultEffort ? null : settings.Effort,
            Minutes = null,
            PerHour = null,
            Delivery = settings.Delivery == DefaultDelivery ? null : settings.Delivery
        };
        return lean.IsDefault ? null : lean;
    }

    public void Validate()
    {
        if (Effort is { } effort) ContractRules.Defined(effort);
        if (Delivery is { } delivery) ContractRules.Defined(delivery);
    }
}
