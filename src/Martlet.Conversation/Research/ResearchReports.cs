using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Martlet.Core.Creations;

namespace Martlet.Conversation;

/// <summary>Research reports as creations (<see cref="KindName"/>): the report in Markdown (<c>report</c>, text/plain) with its
/// title, summary and text in the shared Creations library, so the Creations page shows it and every paired Martlet computer
/// has it. Showing one (perform_creation on the user's yes) writes it as a web page in the data folder's
/// <see cref="FolderName"/> and opens it in the default browser, where its source links can be clicked.</summary>
public static class ResearchReports
{
    public const string KindName = "report";
    public const string FolderName = "research-reports";
    public const int MaximumReportBytes = 128 * 1024;

    public static CreationKind Kind { get; } = new()
    {
        Name = KindName,
        Noun = "report",
        Plural = "reports",
        Verb = "show",
        Glyph = "\uE8A5",
        Assets = [new("report", ["text/plain"], MaximumReportBytes)],
        MaximumBytes = MaximumReportBytes,
        AutoCleanup = true,
        Describe = creation => "a web research report: " + (string.IsNullOrWhiteSpace(creation.Summary) ? "no summary" : creation.Summary)
    };

    /// <summary>The creation that keeps <paramref name="report"/>.</summary>
    public static CreationDraft Draft(ResearchReport report, CreationAuthor author)
    {
        var bytes = Encoding.UTF8.GetBytes(report.Markdown);
        if (bytes.Length > MaximumReportBytes) bytes = Encoding.UTF8.GetBytes(Fit(report.Markdown, MaximumReportBytes));
        return new()
        {
            Kind = KindName,
            Title = report.Title,
            Summary = report.Summary,
            Text = Fit(report.Markdown, CreationLibrary.MaximumTextUtf8Bytes - 16),
            Metadata = JsonSerializer.SerializeToElement(new { sources = report.Sources.Count }),
            CreatedBy = author,
            Assets = [new("report", "text/plain", bytes)]
        };
    }

    private static string Fit(string text, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= bytes) return text;
        int keep = 0, used = 0;
        while (keep < text.Length)
        {
            var width = char.IsSurrogatePair(text, keep) ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(keep, width));
            if (used + size > bytes - 3) break;
            used += size;
            keep += width;
        }
        return text[..keep] + "…";
    }

    /// <summary>Shows report creations: writes the report as a web page in <paramref name="dataDirectory"/>'s
    /// <see cref="FolderName"/> and hands its path to <paramref name="open"/> (the desktop opens it in the default browser).</summary>
    public static ICreationHandler Handler(string dataDirectory, Action<string> open) => new CreationHandler(async (action, token) =>
    {
        var bytes = await action.Assets.ReadAsync("report", token).ConfigureAwait(false);
        if (bytes is null) return new("The report hasn't reached this computer yet. Say you'll show it in a moment.", true);
        string path;
        try
        {
            var folder = Path.Combine(dataDirectory, FolderName);
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, action.Creation.Key + ".html");
            await File.WriteAllTextAsync(path, Html(action.Creation.Title ?? "Report", Encoding.UTF8.GetString(bytes)), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new("The report couldn't be written out to show it. Tell the user briefly; it's still in Creations.", true);
        }
        try { open(path); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new("The report couldn't be opened in the browser. Tell the user it's in Companion › Creations.", true);
        }
        return new($"Showing the report \"{action.Creation.Title}\" in the browser now. Say so in a few words.");
    });

    private static readonly Regex Inline = new(@"\[([^\]\n]+)\]\((https?://[^\s)]+)\)|(https?://[^\s<>()\]]+)|\*\*([^*\n]+)\*\*",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The report as a plain, self-contained web page (no scripts, nothing loaded from elsewhere): headings, lists,
    /// paragraphs, bold and links (only http and https).</summary>
    public static string Html(string title, string markdown)
    {
        var html = new StringBuilder("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title><style>")
            .Append("body{font-family:Segoe UI,sans-serif;max-width:760px;margin:40px auto;padding:0 20px;line-height:1.55;color:#222}")
            .Append("a{color:#0b62c4}h1{font-size:1.7em}h2{font-size:1.25em;margin-top:1.6em}</style></head><body>\n");
        string? list = null;
        var paragraph = new List<string>();
        void Flush()
        {
            if (paragraph.Count > 0) html.Append("<p>").Append(string.Join("<br>\n", paragraph)).Append("</p>\n");
            paragraph.Clear();
        }
        void Close()
        {
            if (list is not null) html.Append("</").Append(list).Append(">\n");
            list = null;
        }
        foreach (var raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { Flush(); Close(); continue; }
            var heading = line.TakeWhile(c => c == '#').Count();
            if (heading is >= 1 and <= 4 && line.Length > heading && line[heading] == ' ')
            {
                Flush(); Close();
                html.Append("<h").Append(heading).Append('>').Append(Inlines(line[(heading + 1)..].Trim())).Append("</h").Append(heading).Append(">\n");
                continue;
            }
            var bullet = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
            var number = Regex.Match(line, @"^\d{1,3}\.\s");
            if (bullet || number.Success)
            {
                Flush();
                var kind = bullet ? "ul" : "ol";
                if (list != kind) { Close(); html.Append('<').Append(kind).Append(">\n"); list = kind; }
                html.Append("<li>").Append(Inlines(bullet ? line[2..] : line[number.Length..])).Append("</li>\n");
                continue;
            }
            Close();
            paragraph.Add(Inlines(line));
        }
        Flush(); Close();
        return html.Append("</body></html>\n").ToString();
    }

    private static string Inlines(string text)
    {
        var html = new StringBuilder();
        var at = 0;
        try
        {
            foreach (Match match in Inline.Matches(text))
            {
                html.Append(WebUtility.HtmlEncode(text[at..match.Index]));
                if (match.Groups[1].Success) Link(match.Groups[2].Value, match.Groups[1].Value);
                else if (match.Groups[3].Success) Link(match.Groups[3].Value.TrimEnd('.', ',', ';', ':'), null);
                else html.Append("<strong>").Append(WebUtility.HtmlEncode(match.Groups[4].Value)).Append("</strong>");
                at = match.Index + match.Length;
                if (match.Groups[3].Success) at -= match.Groups[3].Value.Length - match.Groups[3].Value.TrimEnd('.', ',', ';', ':').Length;
            }
        }
        catch (RegexMatchTimeoutException) { }
        return html.Append(WebUtility.HtmlEncode(text[at..])).ToString();

        void Link(string url, string? label)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && WebAccess.IsWeb(uri))
                html.Append("<a href=\"").Append(WebUtility.HtmlEncode(uri.AbsoluteUri)).Append("\" rel=\"noreferrer noopener\">")
                    .Append(WebUtility.HtmlEncode(label ?? url)).Append("</a>");
            else html.Append(WebUtility.HtmlEncode(label ?? url));
        }
    }
}
