using System.Globalization;
using System.Windows.Data;
using Martlet.Core.Settings;

namespace Martlet.Desktop;

/// <summary>Shows a setup role as the job it does for the owner instead of the STT/LLM/TTS acronym.</summary>
public sealed class SetupJobNameConverter : IValueConverter
{
    internal static string Name(SetupRole role) => role switch
    {
        SetupRole.Llm => "Thinking",
        SetupRole.Stt => "Listening",
        SetupRole.Tts => "Speaking",
        _ => role.ToString()
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SetupRole role ? Name(role) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
