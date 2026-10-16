using System.IO;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Core.Nodes;

namespace Martlet.Desktop;

/// <summary>
/// The front door (docs/OPENAI_ENDPOINT.md) on Share › Share your language models: one of your hosts opens a second, public
/// port that serves only Martlet's web page and the OpenAI-compatible API, for a reverse proxy, Tailscale Funnel or a tunnel to
/// send people to. Opening it runs martlet-host exposure --front-door on that host (its gateway restarts), like Outside access.
/// Which host has it comes from each host's security audit, read with the models when the Share page shows. Check address asks
/// the public address for /models without a key: Martlet's own "missing_api_key" answer proves the whole way works.
/// </summary>
public partial class MainWindow
{
    private ComboBox? frontDoorHostChoice;
    private TextBox? frontDoorPortBox;
    private string? frontDoorCheck;
    private bool frontDoorChecking;

    /// <summary>Your hosts whose front door is open now (from their security audits), with its port.</summary>
    private IReadOnlyList<(PairedHost Host, int Port, IReadOnlyList<string> Listening)> FrontDoors() => FriendsHosts()
        .Select(h => (Host: h, Audit: networkSecurity.GetValueOrDefault(h.HostId).Audit))
        .Where(h => h.Audit?.FrontDoorPort is not null)
        .Select(h => (h.Host, h.Audit!.FrontDoorPort!.Value, h.Audit.FrontDoorListeners))
        .OrderBy(h => h.Host.HostId, StringComparer.Ordinal).ToArray();

    /// <summary>The open front door your proxy should send to: the chosen host's when it is open, else the first open one.</summary>
    private (PairedHost Host, int Port, IReadOnlyList<string> Listening)? OpenFrontDoor(OpenAiEndpointSettings settings)
    {
        var open = FrontDoors();
        return open.Count == 0 ? null : open.FirstOrDefault(d => d.Host.HostId == settings.FrontDoorHostId) is { Host: not null } chosen ? chosen : open[0];
    }

    /// <summary>https://&lt;home address&gt;:&lt;port&gt;: where a proxy on your home network sends people.</summary>
    private static string FrontDoorOrigin(PairedHost host, int port)
    {
        var home = new Uri(host.Pairing.Origin);
        return $"https://{home.Host}:{port}";
    }

    private FrameworkElement FrontDoorBlock(OpenAiEndpointSettings settings)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(new TextBlock { Text = "Front door", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var help = HelpTip.For("The front door",
            "The front door is one of your hosts with a second, public port. That port serves only Martlet's web page and the " +
            "OpenAI-compatible API: never pairing, your network, your settings or anything else of Martlet. Every request needs a key " +
            "you made, and your own work comes first.\n\nChoose a host that is always on. It runs the models it has and sends the " +
            "others to the host of your network that has them, so it needs no graphics card of its own. Opening it restarts that " +
            "host's gateway for a few seconds.\n\nThe front door listens on its home address and on 127.0.0.1, so a proxy on " +
            "your home network or Tailscale on the same computer can reach it. Then give it an https address (the ? next to the address).",
            "OpenAiFrontDoor");
        help.Margin = new Thickness(8, 0, 0, 0);
        heading.Children.Add(help);
        stack.Children.Add(heading);

        var open = OpenFrontDoor(settings);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        status.SetResourceReference(StyleProperty, "Muted");
        status.Text = open is { } door
            ? $"Open on {door.Host.HostId}, port {door.Port}" +
              (door.Listening.Count > 0 ? $" (listening on {string.Join(" and ", door.Listening)})." : ", but it isn't listening: see that host's log.") +
              (FrontDoors().Count > 1 ? $" Also open on {string.Join(", ", FrontDoors().Where(d => d.Host.HostId != door.Host.HostId).Select(d => d.Host.HostId))}." : "")
            : openAiCheckedAt is null ? "Not checked yet." : "No front door yet. Open one on a host that is always on.";
        AutomationProperties.SetAutomationId(status, "OpenAiFrontDoorStatus");
        stack.Children.Add(status);

        var managed = FriendsHosts().Where(h => h.CanLaunch).OrderBy(h => h.HostId, StringComparer.Ordinal).ToArray();
        if (managed.Length == 0)
        {
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
            note.SetResourceReference(StyleProperty, "Muted");
            note.Text = $"Martlet can't run commands on your hosts from this PC. On the host, run: martlet-host exposure --front-door " +
                $"{OpenAiEndpoint.DefaultFrontDoorPort}";
            stack.Children.Add(note);
            return stack;
        }
        frontDoorHostChoice ??= new ComboBox { MinWidth = 170, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(frontDoorHostChoice, "OpenAiFrontDoorHost");
        AutomationProperties.SetName(frontDoorHostChoice, "The host to open the front door on");
        frontDoorPortBox ??= new TextBox { Width = 70, MaxLength = 5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(frontDoorPortBox, "OpenAiFrontDoorPort");
        AutomationProperties.SetName(frontDoorPortBox, "The front door's port");
        if (!frontDoorHostChoice.IsKeyboardFocusWithin)
        {
            var selected = (frontDoorHostChoice.SelectedItem as ComboBoxItem)?.Tag as string ?? open?.Host.HostId ?? settings.FrontDoorHostId;
            frontDoorHostChoice.Items.Clear();
            foreach (var host in managed) frontDoorHostChoice.Items.Add(new ComboBoxItem { Content = host.HostId, Tag = host.HostId });
            frontDoorHostChoice.SelectedItem = frontDoorHostChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ??
                frontDoorHostChoice.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }
        if (!frontDoorPortBox.IsKeyboardFocusWithin)
            frontDoorPortBox.Text = (open?.Port ?? OpenAiEndpoint.DefaultFrontDoorPort).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        (frontDoorHostChoice.Parent as Panel)?.Children.Remove(frontDoorHostChoice);
        (frontDoorPortBox.Parent as Panel)?.Children.Remove(frontDoorPortBox);
        row.Children.Add(frontDoorHostChoice);
        var portLabel = new Label { Content = "_port", Target = frontDoorPortBox, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 0, 0, 0) };
        row.Children.Add(portLabel);
        row.Children.Add(frontDoorPortBox);
        var openButton = new Button
        {
            Content = open is null ? "Open front door" : "Move or change the front door", Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetAutomationId(openButton, "OpenAiFrontDoorOpen");
        AutomationProperties.SetName(openButton, "Open the front door on the chosen host and port");
        openButton.Click += (_, _) => OpenFrontDoorAsync().Forget();
        row.Children.Add(openButton);
        if (open is not null)
        {
            var close = new Button { Content = "Close front door", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(close, "OpenAiFrontDoorClose");
            AutomationProperties.SetName(close, "Close the front door: nothing is reachable through it any more");
            close.Click += (_, _) => CloseFrontDoorAsync().Forget();
            row.Children.Add(close);
        }
        stack.Children.Add(row);
        return stack;
    }

    private async Task OpenFrontDoorAsync()
    {
        if (store is null || closing) return;
        var hostId = (frontDoorHostChoice?.SelectedItem as ComboBoxItem)?.Tag as string;
        var host = FriendsHosts().FirstOrDefault(h => h.HostId == hostId);
        if (host is null) { ActionText.Text = "Choose the host to open the front door on."; return; }
        if (!int.TryParse(frontDoorPortBox?.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) ||
            port is < 1024 or > 65535 || port == new Uri(host.Pairing.Origin).Port)
        {
            ActionText.Text = $"Type a port from 1024 to 65535 for the front door, other than the gateway's own ({new Uri(host.Pairing.Origin).Port}). " +
                $"{OpenAiEndpoint.DefaultFrontDoorPort} is a good choice.";
            return;
        }
        var others = FrontDoors().Where(d => d.Host.HostId != host.HostId).Select(d => d.Host).ToArray();
        if (!ConfirmationDialog.Confirm(this,
                $"Open the front door on {host.HostId}, port {port}? Its gateway restarts, which takes a few seconds. The front door serves " +
                "only Martlet's web page and the OpenAI-compatible API, and every request needs a key you made." +
                (others.Length > 0 ? $" The front door on {string.Join(", ", others.Select(o => o.HostId))} closes." : ""),
                "Open the front door", yes: "_Open", no: "_Cancel", questionId: "FrontDoorOpenQuestion"))
            return;
        foreach (var other in others.Where(o => o.CanLaunch))
            await RunHostActionAsync(other, HostAction.Exposure(NodeCommandRules.FrontDoorArguments(null)));
        if (await RunHostActionAsync(host, HostAction.Exposure(NodeCommandRules.FrontDoorArguments(port))) is null) return;
        var settings = OpenAiEndpointSettings.Load(store.DataDirectory) with { FrontDoorHostId = host.HostId };
        if (settings.Save(store.DataDirectory)) QueueSettingsSync();
        ErrorLog.Info($"Share: the front door is open on {host.HostId}, port {port}.");
        ActionText.Text = $"The front door is open on {host.HostId}, port {port}. Point your proxy at {FrontDoorOrigin(host, port)} (Show setup).";
        foreach (var each in others.Append(host)) networkSecurity.Remove(each.HostId);
        await ReadOpenAiModelsAsync();
    }

    private async Task CloseFrontDoorAsync()
    {
        if (store is null || closing || FrontDoors() is not { Count: > 0 } open) return;
        if (!ConfirmationDialog.Confirm(this,
                $"Close the front door on {string.Join(", ", open.Select(d => d.Host.HostId))}? Apps and the web page can't reach your " +
                "language models through your address any more. Keys stay; open it again at any time.",
                "Close the front door", yes: "_Close it", no: "_Cancel", questionId: "FrontDoorCloseQuestion"))
            return;
        foreach (var (host, _, _) in open.Where(d => d.Host.CanLaunch))
        {
            if (await RunHostActionAsync(host, HostAction.Exposure(NodeCommandRules.FrontDoorArguments(null))) is not null)
                ErrorLog.Info($"Share: the front door on {host.HostId} is closed.");
            networkSecurity.Remove(host.HostId);
        }
        await ReadOpenAiModelsAsync();
    }

    /// <summary>Asks the public address for /models without a key and says what answered: Martlet's front door, Martlet's
    /// gateway (the proxy points at the wrong port), something else, or nothing. Sends no key and changes nothing.</summary>
    private async Task CheckFrontDoorAddressAsync()
    {
        var settings = OpenAiEndpointSettings.Load(store?.DataDirectory);
        if (settings.PublicAddress is not { } address)
        {
            frontDoorCheck = "Type and save the address first.";
            RenderOpenAi();
            return;
        }
        if (frontDoorChecking) return;
        frontDoorChecking = true;
        frontDoorCheck = $"Checking {address}...";
        RenderOpenAi();
        try
        {
            frontDoorCheck = await FrontDoorCheck.RunAsync(address, OpenFrontDoor(settings) is { } door ? FrontDoorOrigin(door.Host, door.Port) : null,
                lifetime.Token);
            ErrorLog.Info("Share: Check address: " + frontDoorCheck);
            SaveOpenAiSummary();
        }
        catch (OperationCanceledException) { }
        finally
        {
            frontDoorChecking = false;
            if (!closing) RenderOpenAi();
        }
    }
}

/// <summary>Check address (<see cref="MainWindow"/>'s front door): what a public address answers for /models without a key.</summary>
internal static class FrontDoorCheck
{
    internal static async Task<string> RunAsync(string address, string? upstream, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(8) })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        var pointTo = upstream is null ? "the front door (open it first)" : $"the front door at {upstream}";
        try
        {
            using var response = await http.GetAsync(address.TrimEnd('/') + "/models", cancellationToken);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            string? code = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                    code = value.GetString();
                else if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("code", out var martlet) &&
                    martlet.ValueKind == JsonValueKind.String)
                    code = martlet.GetString();
            }
            catch (JsonException) { }
            return (status, code) switch
            {
                (401, OpenAiEndpoint.FrontDoorMissingKey) => $"Works: {address} reaches your front door. People with a key can use it now.",
                (401 or 403, not null) => $"{address} reaches a Martlet gateway, but not its front door. Point your proxy at {pointTo}, " +
                    "never at the gateway's own port.",
                (502 or 503 or 504, _) => $"Your proxy answers ({status}) but can't reach Martlet. Check that it sends to {pointTo}.",
                (301 or 302 or 307 or 308, _) => $"{address} redirects ({status}) to {response.Headers.Location}. Save the address it ends at.",
                _ => $"{address} answers {status}, but not as Martlet. Check that your proxy sends everything to {pointTo} (Show setup)."
            };
        }
        catch (HttpRequestException error) when (error.InnerException is AuthenticationException)
        {
            return $"{address} has no trusted certificate. Give your proxy a real certificate (for example Let's Encrypt), or use Tailscale Funnel.";
        }
        catch (HttpRequestException error)
        {
            return $"Nothing answered at {address} ({error.HttpRequestError}). Check its DNS name, your router's port 443 and the proxy.";
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"{address} didn't answer in 15 seconds. Check the proxy and that it sends to {pointTo}.";
        }
        catch (IOException error)
        {
            return $"The connection to {address} broke ({error.Message}).";
        }
    }
}
