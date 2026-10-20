using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Martlet.Core.Settings;
using Martlet.Providers;

namespace Martlet.Conversation;

/// <summary>Small text helpers the check-ins share.</summary>
internal static partial class CheckInText
{
    internal static string OneLine(string text) => Spaces().Replace(text, " ").Trim();

    internal static string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit].TrimEnd() + "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
