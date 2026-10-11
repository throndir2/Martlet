using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Martlet.Conversation.Guides;

/// <summary>A node of a parsed HTML page: an element (its lower-case name, attributes and children) or a run of text (raw, its
/// character references not yet decoded).</summary>
internal sealed class HtmlNode
{
    private static readonly char[] Spaces = [' ', '\t', '\n', '\r', '\f'];
    private readonly Dictionary<string, string>? attributes;
    private string[]? classes;

    public HtmlNode(string name, Dictionary<string, string>? attributes = null)
    {
        Name = name;
        this.attributes = attributes;
    }

    private HtmlNode(string text, bool _)
    {
        Name = null;
        Text = text;
    }

    public static HtmlNode TextNode(string text) => new(text, true);

    /// <summary>The element's lower-case name, or null for text.</summary>
    public string? Name { get; }
    public string Text { get; } = "";
    public List<HtmlNode> Children { get; } = [];

    /// <summary>The attribute's value (character references decoded), or null when the element doesn't have it.</summary>
    public string? this[string attribute] => attributes is not null && attributes.TryGetValue(attribute, out var value) ? WebUtility.HtmlDecode(value) : null;

    public IReadOnlyList<string> Classes => classes ??= (this["class"] ?? "").ToLowerInvariant().Split(Spaces, StringSplitOptions.RemoveEmptyEntries);

    public bool HasClass(string name) => Classes.Contains(name);

    public IEnumerable<HtmlNode> Descendants()
    {
        var stack = new Stack<(HtmlNode Node, int Next)>();
        stack.Push((this, 0));
        while (stack.Count > 0)
        {
            var (node, next) = stack.Pop();
            if (next >= node.Children.Count) continue;
            stack.Push((node, next + 1));
            var child = node.Children[next];
            yield return child;
            if (child.Children.Count > 0) stack.Push((child, 0));
        }
    }
}

/// <summary>A tolerant HTML parser for reading pages: tags, attributes, text, comments and raw-text elements (script, style), with
/// the end tags HTML lets pages leave out (paragraphs, list items, table rows and cells) closed where a browser would close them.
/// It never throws on bad markup.</summary>
internal static class HtmlParser
{
    private const int MaximumDepth = 400;
    private static readonly HashSet<string> Void = ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param",
        "source", "track", "wbr"];
    private static readonly HashSet<string> RawText = ["script", "style", "textarea", "title", "xmp", "iframe", "noembed", "noframes",
        "noscript", "plaintext"];
    private static readonly HashSet<string> ClosesParagraph = ["address", "article", "aside", "blockquote", "details", "div", "dl",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "main", "menu", "nav",
        "ol", "p", "pre", "section", "table", "ul", "li", "dd", "dt"];
    private static readonly HashSet<string> TableParts = ["table", "caption", "thead", "tbody", "tfoot", "tr", "td", "th"];

    public static HtmlNode Parse(string html)
    {
        html ??= "";
        var root = new HtmlNode("#root");
        var stack = new List<HtmlNode> { root };
        var i = 0;
        var n = html.Length;
        while (i < n)
        {
            var lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                Add(HtmlNode.TextNode(html[i..]));
                break;
            }
            if (lt > i) Add(HtmlNode.TextNode(html[i..lt]));
            i = lt;
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }
            if (i + 1 < n && html[i + 1] is '!' or '?')
            {
                var end = html.IndexOf('>', i);
                i = end < 0 ? n : end + 1;
                continue;
            }
            var closing = i + 1 < n && html[i + 1] == '/';
            var start = i + (closing ? 2 : 1);
            if (start >= n || !char.IsAsciiLetter(html[start]))
            {
                Add(HtmlNode.TextNode("<"));
                i++;
                continue;
            }
            var j = start;
            while (j < n && (char.IsAsciiLetterOrDigit(html[j]) || html[j] is '-' or ':' or '_')) j++;
            var name = html[start..j].ToLowerInvariant();
            Dictionary<string, string>? attributes = closing ? null : new(StringComparer.Ordinal);
            var selfClosing = false;
            while (j < n && html[j] != '>')
            {
                var c = html[j];
                if (char.IsWhiteSpace(c) || c == '/')
                {
                    selfClosing = c == '/';
                    j++;
                    continue;
                }
                selfClosing = false;
                var a = j;
                while (j < n && !char.IsWhiteSpace(html[j]) && html[j] is not ('=' or '>' or '/')) j++;
                var attribute = html[a..j].ToLowerInvariant();
                while (j < n && char.IsWhiteSpace(html[j])) j++;
                var value = "";
                if (j < n && html[j] == '=')
                {
                    j++;
                    while (j < n && char.IsWhiteSpace(html[j])) j++;
                    if (j < n && html[j] is '"' or '\'')
                    {
                        var end = html.IndexOf(html[j], j + 1);
                        if (end < 0) end = n;
                        value = html[(j + 1)..end];
                        j = Math.Min(n, end + 1);
                    }
                    else
                    {
                        var v = j;
                        while (j < n && !char.IsWhiteSpace(html[j]) && html[j] != '>') j++;
                        value = html[v..j];
                    }
                }
                if (attributes is not null && attribute.Length > 0) attributes.TryAdd(attribute, value);
            }
            i = Math.Min(n, j + 1);
            if (closing)
            {
                if (name == "br") Add(new HtmlNode("br"));
                else Close(name);
                continue;
            }
            var element = Open(name, attributes, selfClosing);
            if (RawText.Contains(name) && !selfClosing)
            {
                var end = EndTag(html, i, name);
                element.Children.Add(HtmlNode.TextNode(html[i..(end < 0 ? n : end)]));
                if (stack[^1] == element) stack.RemoveAt(stack.Count - 1);
                if (end < 0) break;
                var gt = html.IndexOf('>', end);
                i = gt < 0 ? n : gt + 1;
            }
        }
        return root;

        void Add(HtmlNode node) => stack[^1].Children.Add(node);

        HtmlNode Open(string name, Dictionary<string, string>? attributes, bool selfClosing)
        {
            if (ClosesParagraph.Contains(name) && stack[^1].Name == "p") stack.RemoveAt(stack.Count - 1);
            switch (name)
            {
                case "li": CloseOpen(["li"], ["ul", "ol", "menu", "table", "td", "th"]); break;
                case "dt" or "dd": CloseOpen(["dt", "dd"], ["dl", "table", "td", "th"]); break;
                case "tr": CloseOpen(["tr"], ["table", "thead", "tbody", "tfoot"]); break;
                case "td" or "th": CloseOpen(["td", "th"], ["tr", "table"]); break;
                case "thead" or "tbody" or "tfoot": CloseOpen(["thead", "tbody", "tfoot"], ["table"]); break;
                case "option": CloseOpen(["option"], ["select", "datalist"]); break;
            }
            var element = new HtmlNode(name, attributes);
            Add(element);
            if (!Void.Contains(name) && !selfClosing && stack.Count < MaximumDepth) stack.Add(element);
            return element;
        }

        // Closes the nearest open element named in `names` unless one of `stops` is nearer.
        void CloseOpen(string[] names, string[] stops)
        {
            for (var k = stack.Count - 1; k > 0; k--)
            {
                var open = stack[k].Name!;
                if (names.Contains(open))
                {
                    stack.RemoveRange(k, stack.Count - k);
                    return;
                }
                if (stops.Contains(open)) return;
            }
        }

        void Close(string name)
        {
            var table = TableParts.Contains(name);
            for (var k = stack.Count - 1; k > 0; k--)
            {
                var open = stack[k].Name!;
                if (open == name)
                {
                    stack.RemoveRange(k, stack.Count - k);
                    return;
                }
                // An end tag never closes a table cell or a table it isn't for.
                if (!table && open is "td" or "th" or "table" or "caption") return;
                if (name is "tr" or "td" or "th" or "thead" or "tbody" or "tfoot" && open == "table") return;
            }
        }
    }

    private static int EndTag(string html, int from, string name)
    {
        while (true)
        {
            var at = html.IndexOf("</", from, StringComparison.Ordinal);
            if (at < 0) return -1;
            var after = at + 2 + name.Length;
            if (string.Compare(html, at + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
                (after >= html.Length || html[after] is '>' or '/' || char.IsWhiteSpace(html[after])))
                return at;
            from = at + 2;
        }
    }
}

/// <summary>A link on a page: where it goes (without its #fragment), the page title the link names (MediaWiki's title attribute)
/// and whether it points at a page that doesn't exist yet (a MediaWiki red link).</summary>
internal sealed record HtmlLink(Uri Url, string? Title, bool Missing);

/// <summary>Turns an HTML page into outline text for app guides: each heading a Markdown line ("# ", "## ", "### "), each
/// paragraph, list item and table row a line of its own (table cells named by their column headings, infobox facts as
/// "Key: Value"), and menus, navigation boxes, references, edit links, galleries, images, scripts and hidden parts left out.</summary>
internal static class HtmlOutline
{
    /// <summary>The most text kept of one page.</summary>
    public const int MaxCharacters = 100_000;

    private static readonly HashSet<string> SkipTags = ["script", "style", "noscript", "template", "svg", "math", "iframe", "object",
        "embed", "canvas", "video", "audio", "img", "picture", "source", "map", "input", "button", "select", "textarea", "form", "nav",
        "footer", "head", "title", "meta", "link", "figure", "dialog", "sup", "s", "del", "strike"];
    private static readonly HashSet<string> BlockTags = ["p", "div", "section", "article", "main", "blockquote", "pre", "dl", "dt", "dd",
        "center", "details", "summary", "header", "aside", "address", "fieldset", "figcaption", "caption", "hr", "body", "html", "#root"];
    private static readonly HashSet<string> NoiseClasses = ["navbox", "navbox-styles", "vertical-navbox", "navigation-box", "mw-editsection",
        "editsection", "reference", "references", "reflist", "refbegin", "mw-references-wrap", "mw-cite-backlink", "gallery",
        "wikia-gallery", "gallerybox", "toc", "toctitle", "mw-toc", "printfooter", "catlinks", "noprint", "metadata", "mw-empty-elt",
        "mbox", "ambox", "ombox", "tmbox", "cmbox", "message-box", "hatnote", "dablink", "rellink", "mw-jump-link", "mw-collapsible-toggle",
        "collapsible-toggle-wrapper", "navigation-not-searchable", "sister-project", "thumb", "thumbinner", "thumbcaption", "mw-indicators",
        "error", "cite-error", "magnify", "sr-only", "screen-reader-text", "visually-hidden", "skip-link", "noexcerpt", "mw-headline-anchor"];
    private static readonly HashSet<string> NoiseIds = ["toc", "catlinks", "footer", "sidebar", "sitesub", "contentsub", "contentsub2",
        "jump-to-nav", "mw-navigation", "mw-panel", "mw-head", "p-personal", "mw-footer", "left-navigation", "right-navigation",
        "sitenotice", "mw-indicators", "header", "masthead", "breadcrumbs", "comments"];
    private static readonly string[] NoiseParts = ["navbox", "sidebar", "breadcrumb", "cookie", "advert", "social", "share", "newsletter",
        "footer", "navbar", "comment", "popup", "modal"];
    private static readonly HashSet<string> NoiseRoles = ["navigation", "banner", "contentinfo", "search", "dialog", "alertdialog", "menu",
        "menubar", "toolbar"];

    /// <summary>The outline text of <paramref name="content"/>, starting with "# <paramref name="title"/>" when the content has no
    /// top heading of its own, cut at <see cref="MaxCharacters"/>.</summary>
    public static string Text(HtmlNode content, string? title)
    {
        var writer = new Writer(MaxCharacters, bullets: true);
        if (!string.IsNullOrWhiteSpace(title) && !content.Descendants().Any(d => d.Name == "h1" && !Skipped(d)))
            writer.Heading(1, Clean(title));
        Walk(content, writer, plain: false);
        return writer.Result();
    }

    /// <summary>The part of a whole page that holds its content: a MediaWiki article's text, the page's main or article element,
    /// or else its body.</summary>
    public static HtmlNode Content(HtmlNode root)
    {
        var elements = root.Descendants().Where(d => d.Name is not null).ToList();
        IEnumerable<Func<HtmlNode, bool>> picks =
        [
            e => e.HasClass("mw-parser-output"),
            e => (e["id"] ?? "").ToLowerInvariant() is "mw-content-text" or "wiki-content-block" or "page-content",
            e => e.Name == "main" || e["role"] == "main",
            e => e.Name == "article",
            e => (e["id"] ?? "").ToLowerInvariant() is "content" or "main-content" or "maincontent" or "main" or "bodycontent",
        ];
        foreach (var pick in picks)
        {
            var best = elements.Where(pick).Select(e => (Element: e, Length: TextLength(e))).OrderByDescending(x => x.Length).FirstOrDefault();
            if (best.Element is not null && best.Length >= 200) return best.Element;
        }
        return elements.FirstOrDefault(e => e.Name == "body") ?? root;
    }

    /// <summary>The page's own title: its main heading, else its title element.</summary>
    public static string? Title(HtmlNode root, HtmlNode content)
    {
        var heading = content.Descendants().FirstOrDefault(d => d.Name == "h1" && !Skipped(d))
            ?? root.Descendants().FirstOrDefault(d => d.Name == "h1" && !Skipped(d));
        var text = heading is null ? null : Inline(heading);
        if (string.IsNullOrWhiteSpace(text))
        {
            var title = root.Descendants().FirstOrDefault(d => d.Name == "title");
            text = title is null ? null : Clean(WebUtility.HtmlDecode(string.Concat(title.Children.Select(c => c.Text))));
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length > 200 ? text[..200] : text;
    }

    /// <summary>The page's links: those in <paramref name="content"/> first, then the rest of the page (footers left out), each
    /// once, resolved against the page's address (or its base element).</summary>
    public static IReadOnlyList<HtmlLink> Links(HtmlNode root, HtmlNode content, Uri page)
    {
        var baseUrl = page;
        if (root.Descendants().FirstOrDefault(d => d.Name == "base" && d["href"] is not null)?["href"] is { } href &&
            Uri.TryCreate(page, href.Trim(), out var declared) && WebAccess.IsWeb(declared))
            baseUrl = declared;
        var links = new List<HtmlLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Collect(content);
        Collect(root);
        return links;

        void Collect(HtmlNode from)
        {
            var stack = new Stack<HtmlNode>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.Name is null || node.Name is "footer" or "script" or "style" or "template" || node["role"] == "contentinfo") continue;
                if (node.Name == "a" && node["href"] is { } target && Resolve(baseUrl, target) is { } url && seen.Add(url.AbsoluteUri))
                    links.Add(new(url, node["title"], node.HasClass("new")));
                for (var k = node.Children.Count - 1; k >= 0; k--) stack.Push(node.Children[k]);
            }
        }
    }

    /// <summary>The page's canonical address (its link rel="canonical"), or null.</summary>
    public static Uri? Canonical(HtmlNode root, Uri page)
    {
        var link = root.Descendants().FirstOrDefault(d => d.Name == "link" && (d["rel"] ?? "").Split(' ').Contains("canonical", StringComparer.OrdinalIgnoreCase));
        return link?["href"] is { } href ? Resolve(page, href) : null;
    }

    private static readonly Regex ScriptPath = new("\"wgScriptPath\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Where a MediaWiki page's api.php may be, best guess first (its EditURI or search link, its script path), or
    /// nothing when the page isn't from MediaWiki.</summary>
    public static IReadOnlyList<Uri> MediaWikiApis(string html, HtmlNode root, Uri page)
    {
        var elements = root.Descendants().Where(d => d.Name is "meta" or "link").ToList();
        var generator = elements.Any(d => d.Name == "meta" && (d["name"] ?? "").Equals("generator", StringComparison.OrdinalIgnoreCase) &&
            (d["content"] ?? "").StartsWith("MediaWiki", StringComparison.OrdinalIgnoreCase));
        var apis = new List<Uri>();
        foreach (var link in elements.Where(d => d.Name == "link" && d["href"] is not null))
        {
            var rel = (link["rel"] ?? "").ToLowerInvariant();
            var href = link["href"]!;
            if (rel == "edituri" && href.Contains("api.php", StringComparison.OrdinalIgnoreCase)) Add(href[..(href.IndexOf("api.php", StringComparison.OrdinalIgnoreCase) + 7)]);
            else if (rel == "search" && href.Contains("opensearch_desc.php", StringComparison.OrdinalIgnoreCase))
                Add(href[..href.IndexOf("opensearch_desc.php", StringComparison.OrdinalIgnoreCase)] + "api.php");
        }
        try
        {
            if (ScriptPath.Match(html) is { Success: true } script) Add(script.Groups[1].Value.Replace("\\/", "/", StringComparison.Ordinal) + "/api.php");
        }
        catch (RegexMatchTimeoutException) { }
        if (apis.Count == 0 && (generator || root.Descendants().Any(d => d.HasClass("mw-parser-output"))))
        {
            Add("/api.php");
            Add("/w/api.php");
        }
        return apis;

        void Add(string href)
        {
            if (Resolve(page, href) is { } url && !apis.Contains(url)) apis.Add(url);
        }
    }

    internal static Uri? Resolve(Uri page, string href)
    {
        href = href.Trim();
        if (href.Length == 0 || href.StartsWith('#')) return null;
        if (!Uri.TryCreate(page, href, out var url) || !WebAccess.IsWeb(url)) return null;
        return url.Fragment.Length == 0 ? url : new Uri(url.GetLeftPart(UriPartial.Query));
    }

    /// <summary>Whether an element is left out of the outline: menus, navigation, references, edit links, galleries, images,
    /// scripts, hidden parts and the like.</summary>
    internal static bool Skipped(HtmlNode e)
    {
        var name = e.Name!;
        if (SkipTags.Contains(name)) return name != "sup" || e.HasClass("reference") || (e["id"] ?? "").StartsWith("cite_ref", StringComparison.Ordinal);
        if (name == "aside" && !IsInfobox(e)) return true;
        if (name == "header" && !e.Descendants().Any(d => d.Name == "h1")) return true;
        if (e["hidden"] is not null || e["aria-hidden"] == "true") return true;
        if (e["style"] is { } style)
        {
            var compact = style.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
            if (compact.Contains("display:none", StringComparison.Ordinal) || compact.Contains("visibility:hidden", StringComparison.Ordinal)) return true;
        }
        if (e["role"] is { } role && NoiseRoles.Contains(role.ToLowerInvariant())) return true;
        if (IsInfobox(e)) return false;
        foreach (var css in e.Classes)
            if (NoiseClasses.Contains(css) || NoiseParts.Any(part => css.Contains(part, StringComparison.Ordinal))) return true;
        if (e["id"] is { Length: > 0 } id)
        {
            id = id.ToLowerInvariant();
            if (NoiseIds.Contains(id) || NoiseParts.Any(part => id.Contains(part, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    private static bool IsInfobox(HtmlNode e) => e.Classes.Any(c => c.Contains("infobox", StringComparison.Ordinal));

    private static void Walk(HtmlNode node, Writer w, bool plain)
    {
        foreach (var child in node.Children)
        {
            if (w.Full) return;
            if (child.Name is null) w.Text(WebUtility.HtmlDecode(child.Text));
            else Element(child, w, plain);
        }
    }

    private static void Element(HtmlNode e, Writer w, bool plain)
    {
        if (Skipped(e)) return;
        var name = e.Name!;
        switch (name)
        {
            case "br":
                w.Break();
                return;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                var heading = Inline(e);
                if (plain) w.Line(heading);
                else w.Heading(name[1] - '0', heading);
                return;
            case "table":
                Table(e, w, plain);
                return;
            case "ul" or "ol" or "menu":
                w.Break();
                var number = 0;
                foreach (var child in e.Children)
                {
                    if (w.Full) return;
                    if (child.Name is null) w.Text(WebUtility.HtmlDecode(child.Text));
                    else if (child.Name == "li" && !Skipped(child)) Item(child, w, plain, name == "ol" ? ++number + ". " : "- ");
                    else Element(child, w, plain);
                }
                w.Break();
                return;
            case "li":
                Item(e, w, plain, "- ");
                return;
        }
        if (e.HasClass("pi-data") && e.Descendants().FirstOrDefault(d => d.HasClass("pi-data-value")) is { } value)
        {
            var label = e.Descendants().FirstOrDefault(d => d.HasClass("pi-data-label")) is { } l ? Inline(l) : "";
            var fact = Cell(value);
            if (fact.Length > 0) w.Line(label.Length > 0 ? label + ": " + fact : fact);
            return;
        }
        if (IsInfobox(e))
        {
            w.Break();
            Walk(e, w, plain: true);
            w.Break();
            return;
        }
        if (BlockTags.Contains(name))
        {
            w.Break();
            Walk(e, w, plain);
            w.Break();
            return;
        }
        w.InlineStart(Marked(e));
        Walk(e, w, plain);
        w.InlineEnd(Marked(e));
    }

    // Links and template-made elements (they have a class) side by side are separate words ("Ore", "Crafting material" tags);
    // other inline elements side by side can be one word split by an editor ("bu" "t").
    private static bool Marked(HtmlNode e) => e.Name == "a" || e["class"] is not null;

    private static void Item(HtmlNode li, Writer w, bool plain, string prefix)
    {
        w.Break();
        w.Prefix(prefix);
        Walk(li, w, plain);
        w.EndBlock();
    }

    // A table: a layout table (one cell a row, or headings in its cells) is read as blocks; a data table one row a line, each
    // cell named by its column heading ("Season: Spring; Price: 10g"), and a two-cell row as "Key: Value".
    private static void Table(HtmlNode table, Writer w, bool plain)
    {
        var rows = new List<List<HtmlNode>>();
        Rows(table);
        if (table.Children.FirstOrDefault(c => c.Name == "caption") is { } caption && !Skipped(caption)) w.Line(Inline(caption));
        if (rows.Count == 0) return;
        if (rows.All(r => r.Count <= 1) || rows.Any(r => r.Any(c => HasHeading(c))) || table["role"] == "presentation")
        {
            foreach (var cell in rows.SelectMany(r => r))
            {
                if (w.Full) return;
                w.Break();
                Walk(cell, w, plain);
                w.Break();
            }
            return;
        }
        var grid = Grid(rows);
        var headerRows = 0;
        while (headerRows < grid.Count && grid[headerRows].Count >= 2 && grid[headerRows].All(c => c.Header)) headerRows++;
        string[]? headers = null;
        if (headerRows > 0 && headerRows < grid.Count)
        {
            var columns = grid.Take(headerRows).Max(r => r.Count);
            headers = Enumerable.Range(0, columns).Select(k => string.Join(" ", grid.Take(headerRows)
                .Select(r => k < r.Count ? r[k].Text : "").Where(t => t.Length > 0).Distinct())).ToArray();
        }
        else headerRows = 0;
        w.Break();
        foreach (var cells in grid.Skip(headerRows))
        {
            if (w.Full) return;
            var filled = cells.Where(c => c.Text.Length > 0).ToList();
            if (filled.Count == 0) continue;
            string line;
            if (headers is not null && cells.Count == headers.Length && filled.Count > 1)
                line = string.Join("; ", cells.Select((c, k) => (Cell: c, Heading: headers[k])).Where(x => x.Cell.Text.Length > 0)
                    .Select(x => x.Heading.Length > 0 && x.Heading != x.Cell.Text ? x.Heading + ": " + x.Cell.Text : x.Cell.Text));
            else if (filled.Count == 2 && (headers is null || filled[0].Header)) line = filled[0].Text + ": " + filled[1].Text;
            else line = string.Join(" | ", filled.Select(c => c.Text));
            w.Line(line);
        }

        void Rows(HtmlNode node)
        {
            foreach (var child in node.Children)
            {
                if (child.Name == "tr" && !Skipped(child))
                    rows.Add(child.Children.Where(c => c.Name is "td" or "th" && !Skipped(c)).ToList());
                else if (child.Name is "thead" or "tbody" or "tfoot") Rows(child);
            }
        }

        static bool HasHeading(HtmlNode cell)
        {
            var stack = new Stack<HtmlNode>(cell.Children);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.Name is null || node.Name == "table") continue;
                if (node.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" && !Skipped(node)) return true;
                foreach (var child in node.Children) stack.Push(child);
            }
            return false;
        }
    }

    // The table's cells laid out by column: a cell spanning rows is repeated in each row (so each line stands alone), a cell
    // spanning columns fills them (a heading repeated, a value once and then empty).
    private static List<List<(string Text, bool Header)>> Grid(List<List<HtmlNode>> rows)
    {
        var grid = new List<List<(string Text, bool Header)>>();
        var carried = new Dictionary<int, (string Text, bool Header, int Left)>();
        foreach (var row in rows)
        {
            var line = new List<(string Text, bool Header)>();
            var next = 0;
            var column = 0;
            while ((next < row.Count || carried.ContainsKey(column)) && column < 100)
            {
                if (carried.TryGetValue(column, out var carry))
                {
                    line.Add((carry.Text, carry.Header));
                    if (carry.Left <= 1) carried.Remove(column);
                    else carried[column] = carry with { Left = carry.Left - 1 };
                    column++;
                    continue;
                }
                var cell = row[next++];
                var text = Cell(cell);
                var header = cell.Name == "th";
                var across = Span(cell["colspan"], 50);
                var down = Span(cell["rowspan"], 500);
                for (var k = 0; k < across && column < 100; k++, column++)
                {
                    var value = k == 0 || header ? text : "";
                    line.Add((value, header));
                    if (down > 1) carried[column] = (value, header, down - 1);
                }
            }
            grid.Add(line);
        }
        return grid;

        static int Span(string? value, int most) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var span) ? Math.Clamp(span, 1, most) : 1;
    }

    // A cell's or infobox value's text on one line: its lines joined with ", " when short (names), else "; ".
    private static string Cell(HtmlNode cell)
    {
        var writer = new Writer(4_000, bullets: false);
        Walk(cell, writer, plain: true);
        var lines = writer.Result().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(lines.All(l => l.Length < 40) ? ", " : "; ", lines);
    }

    private static string Inline(HtmlNode node)
    {
        var writer = new Writer(1_000, bullets: false);
        Walk(node, writer, plain: true);
        return writer.Result().Replace('\n', ' ');
    }

    private static string Clean(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int TextLength(HtmlNode element)
    {
        var length = 0;
        var stack = new Stack<HtmlNode>();
        stack.Push(element);
        while (stack.Count > 0 && length < 100_000)
        {
            var node = stack.Pop();
            if (node.Name is null)
            {
                length += node.Text.Length - node.Text.Count(char.IsWhiteSpace);
                continue;
            }
            if (node != element && Skipped(node)) continue;
            foreach (var child in node.Children) stack.Push(child);
        }
        return length;
    }

    // Writes the outline: text joined with single spaces, one block a line, a list item's mark before its first text.
    private sealed class Writer(int limit, bool bullets)
    {
        private readonly StringBuilder output = new();
        private readonly StringBuilder line = new();
        private string? prefix;
        private bool space;
        private bool boundary;

        public bool Full => output.Length >= limit;

        public void Text(string text)
        {
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (line.Length > 0) space = true;
                    continue;
                }
                if (char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Control) continue;
                if (line.Length == 0)
                {
                    if (prefix is not null && bullets) line.Append(prefix);
                    prefix = null;
                }
                else if (space) line.Append(' ');
                space = false;
                boundary = false;
                line.Append(c);
            }
        }

        // Two marked inline elements side by side (tags in an infobox, links in a row) read as two words.
        public void InlineStart(bool marked)
        {
            if (marked && boundary && line.Length > 0) space = true;
        }

        public void InlineEnd(bool marked) => boundary |= marked;

        public void Prefix(string mark) => prefix = mark;

        public void Break()
        {
            space = false;
            boundary = false;
            if (line.Length == 0) return;
            if (!Full)
            {
                output.Append(line).Append('\n');
                prefix = null;
            }
            line.Clear();
        }

        public void EndBlock()
        {
            Break();
            prefix = null;
        }

        public void Line(string text)
        {
            Break();
            Text(text);
            Break();
        }

        public void Heading(int level, string text)
        {
            Break();
            prefix = null;
            text = Clean(text);
            if (text.Length == 0 || Full) return;
            output.Append('#', level).Append(' ').Append(text).Append('\n');
        }

        public string Result()
        {
            Break();
            var text = output.ToString().TrimEnd();
            if (text.Length <= limit) return text;
            var cut = text.LastIndexOf('\n', limit);
            return cut > 0 ? text[..cut] : text[..limit];
        }
    }
}
