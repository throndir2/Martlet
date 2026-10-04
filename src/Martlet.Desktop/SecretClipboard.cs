using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Martlet.Desktop;

/// <summary>Copies a secret, such as a one-use pairing code, so it pastes on another computer without lingering: Windows
/// leaves it out of clipboard history (Win+V) and the cloud clipboard, and <see cref="Withdraw"/> clears it once it stops
/// working unless something else was copied since.</summary>
internal static class SecretClipboard
{
    internal const string NoHistoryFormat = "CanIncludeInClipboardHistory";
    internal const string NoCloudFormat = "CanUploadToCloudClipboard";

    /// <summary>The text, marked with a DWORD 0 for each format Windows checks before keeping clipboard content.</summary>
    internal static DataObject Data(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        data.SetData(NoHistoryFormat, new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData(NoCloudFormat, new MemoryStream(BitConverter.GetBytes(0)));
        return data;
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard; false when another app holds it (WPF already retries).</summary>
    internal static bool Copy(string text)
    {
        try
        {
            Clipboard.SetDataObject(Data(text), copy: true);
            return true;
        }
        catch (ExternalException error)
        {
            ErrorLog.Warn("Couldn't copy to the clipboard", error);
            return false;
        }
    }

    /// <summary>Clears the clipboard if it still holds <paramref name="text"/>; anything copied since stays.</summary>
    internal static void Withdraw(string text)
    {
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == text) Clipboard.Clear();
        }
        catch (ExternalException error)
        {
            ErrorLog.Warn("Couldn't clear the clipboard", error);
        }
    }
}
