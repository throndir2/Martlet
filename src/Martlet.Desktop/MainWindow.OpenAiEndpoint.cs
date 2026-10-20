using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Martlet.HostClient;
using Martlet.Core.Access;
using Martlet.Core.Contracts;
using Martlet.Presentation;

namespace Martlet.Desktop;

/// <summary>
/// The Share page (docs/OPENAI_ENDPOINT.md): your Martlet network, friends, your language models for other people's apps (the
/// OpenAI-compatible endpoint every host serves), API keys and the hosts friends share with this PC. This file draws the page and
/// the endpoint's card: the address people use (a reverse proxy with a real certificate in front of one host, the front door),
/// the models your hosts run (read with this PC's signed pairings when the page shows, at most every two minutes, or on Check
/// now; never on a reply's path), and the keys made for people, with their priority. Keys are ordinary API keys with the openai
/// scope, so they reach every host like any other key. Cloud providers and keys set in Martlet are never reachable through it.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan OpenAiStale = TimeSpan.FromMinutes(2);
    private Dictionary<string, IReadOnlyList<string>> openAiModels = new(StringComparer.Ordinal);
    private Dictionary<string, string> openAiProblems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> sharedHostModels = new(StringComparer.Ordinal);
    private DateTimeOffset? openAiCheckedAt;
    private bool openAiBusy;
    private string? openAiLogged;
    private TextBox? openAiAddressBox;
    private TextBlock? openAiAddressProblem;

    /// <summary>Draws the Share page's cards and reads again what is stale while it shows.</summary>
    private void RenderShare()
    {
        RenderNetwork();
        RenderFriends();
        RenderSharedHosts();
        RefreshFriendsWhenShown();
        RenderApiKeys();
        RenderOpenAi();
        RefreshOpenAiWhenShown();
        RenderShareSummary();
    }

    /// <summary>Draws the Devices map or the Share page, whichever shows, after something they show changed.</summary>
    private void RenderDevicePages()
    {
        if (DevicesPage.IsVisible) RenderMap();
        else if (SharePage.IsVisible) RenderShare();
    }

    private void RenderShareSummary()
    {
        var members = networkState.Roster is { } roster ? roster.ActiveDesktops.Count() + roster.ActiveHosts.Count() : homeHosts.Count + 1;
        var people = apiKeys.Keys.Count(k => !k.Revoked && k.Allows(ApiKeyScopes.OpenAi));
        var friends = FriendsOverview.Build(friendsRead, shareLinks.Made).Count(f => f.SharedOn.Any());
        ShareSummary.Text = $"{Count(members, "computer")} in your Martlet network · {Count(friends, "friend")} you share hosts with · " +
            $"{Count(people, "person", "people")} with a key to your language models · {Count(sharedHosts.Count, "host")} shared with this PC.";

        static string Count(int count, string what, string? many = null) => count == 1 ? $"1 {what}" : $"{count} {many ?? what + "s"}";
    }

    private void RefreshOpenAiWhenShown()
    {
        if (!SharePage.IsVisible || closing || store is null) return;
        if ((FriendsHosts().Count > 0 || sharedHosts.Count > 0) &&
            (openAiCheckedAt is not { } read || DateTimeOffset.Now - read > OpenAiStale))
            ReadOpenAiModelsAsync().Forget();
    }

    private void OpenAiCheck_Click(object sender, RoutedEventArgs e) => ReadOpenAiModelsAsync().Forget();

    /// <summary>Reads the language models each of your hosts, and each host a friend shares with this PC, offers (GET /v1/models,
    /// signed with this PC's pairing). Keeps a summary for MCP in openai-models.json.</summary>
    private async Task ReadOpenAiModelsAsync()
    {
        if (openAiBusy || closing || store is null) return;
        openAiBusy = true;
        OpenAiStatusText.Text = "Reading which language models your hosts offer...";
        try
        {
            var own = FriendsHosts();
            var shared = sharedHosts.ToArray();
            var results = await Task.WhenAll(own.Concat(shared).Select(async host =>
            {
                try
                {
                    using var connection = ClusterSync.Connect(host.Pairing);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    var models = await connection.ReadOpenAiModelsAsync(timeout.Token);
                    // Your own hosts' security audits say which one has the front door open (a friend's host never does for you).
                    if (!host.Shared)
                    {
                        try { networkSecurity[host.HostId] = (await connection.ReadSecurityAuditAsync(timeout.Token), DateTimeOffset.UtcNow); }
                        catch (Exception error) when (ClusterSync.IsHostFailure(error) || error is InvalidOperationException) { }
                    }
                    return (host.HostId, Models: models, Problem: (string?)null);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    return (host.HostId, Models: (IReadOnlyList<string>?)null, Problem: "didn't answer in time");
                }
                catch (Exception error) when (ClusterSync.IsHostFailure(error))
                {
                    return (host.HostId, Models: null, Problem: error is HostConnectionException { Code: "request.invalid" or "not_found" or "route.not_found" }
                        ? "runs an older Martlet without the OpenAI-compatible endpoint; update it" : error.Message);
                }
            }));
            if (closing) return;
            var ownIds = own.Select(h => h.HostId).ToHashSet(StringComparer.Ordinal);
            openAiModels = results.Where(r => r.Models is not null && ownIds.Contains(r.HostId))
                .ToDictionary(r => r.HostId, r => r.Models!, StringComparer.Ordinal);
            openAiProblems = results.Where(r => r.Problem is not null && ownIds.Contains(r.HostId))
                .ToDictionary(r => r.HostId, r => r.Problem!, StringComparer.Ordinal);
            sharedHostModels.Clear();
            foreach (var result in results.Where(r => r.Models is not null && !ownIds.Contains(r.HostId)))
                sharedHostModels[result.HostId] = result.Models!;
            openAiCheckedAt = DateTimeOffset.Now;
            SaveOpenAiSummary();
            var picture = string.Join("; ", openAiModels.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}: {(p.Value.Count == 0 ? "none" : string.Join(", ", p.Value))}"));
            if (picture != openAiLogged)
            {
                openAiLogged = picture;
                ErrorLog.Info("Share: your hosts' language models for the OpenAI-compatible endpoint: " + (picture.Length == 0 ? "none." : picture + "."));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            openAiBusy = false;
            if (!closing)
            {
                RenderOpenAi();
                RenderSharedHosts();
            }
        }
    }
}

/// <summary>What the Share page last read of your hosts' language models, kept in openai-models.json (host IDs and model names;
/// never a key or an address) so MCP openai_endpoint_status can show it without contacting a host.</summary>
internal static class OpenAiShare
{
    internal const string SummaryFile = "openai-models.json";

    /// <summary>The open front door: its host, port, where it listens and the address a proxy on the home network sends to.</summary>
    internal sealed record FrontDoorSummary(string HostId, int Port, IReadOnlyList<string> Listening, string Upstream);

    internal static void SaveSummary(string directory, IReadOnlyDictionary<string, IReadOnlyList<string>> models,
        IReadOnlyDictionary<string, string> problems, IReadOnlyDictionary<string, IReadOnlyList<string>> shared, DateTimeOffset at,
        FrontDoorSummary? frontDoor = null, string? addressCheck = null)
    {
        var document = new
        {
            version = 1,
            checkedAt = at,
            hosts = models.Keys.Concat(problems.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id => new
            {
                hostId = id,
                models = models.GetValueOrDefault(id) ?? [],
                problem = problems.GetValueOrDefault(id)
            }).ToArray(),
            sharedWithThisPc = shared.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { hostId = p.Key, models = p.Value }).ToArray(),
            frontDoor = frontDoor is null ? null : new { hostId = frontDoor.HostId, port = frontDoor.Port, listening = frontDoor.Listening, upstream = frontDoor.Upstream },
            addressCheck
        };
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SummaryFile);
        var temporary = Path.Combine(directory, $"openai-models.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
