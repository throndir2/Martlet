using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Martlet.Mcp.Client;

namespace Martlet.Desktop;

/// <summary>Edits mcp.json. Saving validates the whole file first, so a typo never replaces a working configuration.</summary>
public partial class McpConfigWindow : ThemedWindow
{
    private const string Empty = "{\n  \"mcpServers\": {\n  }\n}\n";
    private readonly McpToolService service;
    private string saved = "";
    private bool loading;

    internal McpConfigWindow(McpToolService service)
    {
        this.service = service;
        InitializeComponent();
        loading = true;
        try
        {
            saved = service.ReadText();
            Editor.Text = saved.Trim().Length == 0 ? Empty.Replace("\n", Environment.NewLine) : saved;
            StatusText.Text = service.FilePath is { } path ? $"Saved in: {path}" : "Martlet has no data folder, so MCP servers can't be saved.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or McpConfigurationException)
        {
            Editor.Text = Empty.Replace("\n", Environment.NewLine);
            StatusText.Text = $"MCP server settings couldn't be read: {error.Message}";
        }
        finally { loading = false; }
        SaveButton.IsEnabled = service.FilePath is not null;
    }

    private bool Dirty => !string.Equals(Normalize(Editor.Text), Normalize(saved.Trim().Length == 0 ? Empty : saved), StringComparison.Ordinal);
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!loading && IsLoaded) StatusText.Text = "Not saved yet.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            service.Save(Editor.Text);
            saved = Editor.Text;
            service.EnsureStarted(retryNow: true);
            var servers = service.Servers;
            StatusText.Text = servers.Count == 0
                ? "Saved. No servers are set up."
                : $"Saved. Starting {string.Join(", ", servers.Where(s => !s.Disabled).Select(s => s.Name))}. See Tools for status.";
        }
        catch (Exception error) when (error is McpConfigurationException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Not saved: {error.Message}";
        }
    }

    // Adds the reference filesystem server (your Documents folder) to what's in the editor.
    private void Example_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var example = (JsonObject)JsonNode.Parse(McpConfiguration.Example)!;
            var document = Editor.Text.Trim().Length == 0 ? new JsonObject()
                : JsonNode.Parse(Editor.Text, documentOptions: new JsonDocumentOptions
                    { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                    ?? throw new McpConfigurationException("The file must be a JSON object.");
            var key = document["servers"] is JsonObject && document["mcpServers"] is null ? "servers" : "mcpServers";
            if (document[key] is not JsonObject servers) document[key] = servers = new JsonObject();
            var name = "filesystem";
            for (var n = 2; servers.ContainsKey(name); n++) name = $"filesystem-{n}";
            servers[name] = example["mcpServers"]!["filesystem"]!.DeepClone();
            Editor.Text = document.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            StatusText.Text = $"Added \"{name}\". It lets Martlet use files in your Documents folder and requires Node.js. Change the folder if needed, then save.";
        }
        catch (Exception error) when (error is JsonException or McpConfigurationException)
        {
            StatusText.Text = "Fix the JSON first (or clear the editor), then insert the example.";
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (service.FilePath is not { } path || Path.GetDirectoryName(path) is not { } folder || !Directory.Exists(folder)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose(); }
        catch (Win32Exception error) { StatusText.Text = $"Couldn't open the folder: {error.Message}"; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (Dirty && !ConfirmationDialog.Confirm(this, "Close without saving your MCP server changes?", "MCP servers"))
            e.Cancel = true;
    }
}
