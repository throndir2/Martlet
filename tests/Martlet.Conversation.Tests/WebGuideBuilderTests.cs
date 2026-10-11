using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Martlet.Conversation.Guides;

namespace Martlet.Conversation.Tests;

public sealed class WebGuideBuilderTests
{
    private static readonly GuideBuildLimits Fast = new() { Delay = TimeSpan.Zero };

    private const string IronOre =
        "<div class=\"mw-parser-output\"><aside class=\"portable-infobox\"><h2 class=\"pi-item pi-title\">Iron Ore</h2>" +
        "<figure class=\"pi-item pi-image\"><img alt=\"Iron Ore.png\" src=\"x.png\"/><figcaption>A lump</figcaption></figure>" +
        "<div class=\"pi-item pi-data\"><h3 class=\"pi-data-label\">Type</h3><div class=\"pi-data-value\">Resource</div></div>" +
        "<div class=\"pi-item pi-data\"><h3 class=\"pi-data-label\">Sell price</h3><div class=\"pi-data-value\"><span style=\"display: none\">sort 10</span>10g</div></div>" +
        "</aside><p><b>Iron Ore</b> is a common ore found deep underground and smelted into bars at a furnace.<sup class=\"reference\"><a href=\"#cite_note-1\">[1]</a></sup></p>" +
        "<p><span class=\"tag\">Ore</span><span class=\"tag\">Material</span><s>Old name</s> mined wi<span>th</span> a pick<span>axe</span>.</p>" +
        "<div class=\"mw-heading mw-heading2\"><h2 id=\"Locations\">Locations</h2><span class=\"mw-editsection\">[<a href=\"/wiki/Iron_Ore?action=edit\">edit</a>]</span></div>" +
        "<ul><li>The <a href=\"/wiki/Northern_Caves\" title=\"Northern Caves\">Northern Caves</a> below floor 40</li><li>Bought from the <a href=\"/wiki/Hidden_Gem\" title=\"Hidden Gem\">blacksmith</a></li></ul>" +
        "<h3><span class=\"mw-headline\">Prices</span></h3>" +
        "<table class=\"wikitable\"><tr><th>Season</th><th>Shop</th><th>Price</th></tr>" +
        "<tr><td rowspan=\"2\">Spring</td><td>Blacksmith</td><td>75g</td></tr><tr><td>Traveling Cart</td><td>100g</td></tr></table>" +
        "<p>See <a href=\"/wiki/User:Bob\">Bob</a>, <a href=\"/index.php?title=Missing_Page&amp;action=edit&amp;redlink=1\" class=\"new\" title=\"Missing Page\">a missing page</a>.</p>" +
        "<ul class=\"gallery\"><li>Gallery picture caption</li></ul>" +
        "<table class=\"navbox\"><tr><td>Navbox Ores Copper Gold</td></tr></table>" +
        "<div class=\"references\"><ol><li>Reference text</li></ol></div></div>";

    [Fact]
    public void OutlineKeepsHeadingsListsTablesAndInfoboxFactsAndDropsTheRest()
    {
        var text = HtmlOutline.Text(HtmlParser.Parse(IronOre), "Iron Ore");
        var lines = text.Split('\n');
        Assert.Equal("# Iron Ore", lines[0]);
        Assert.Contains("Type: Resource", lines);
        Assert.Contains("Sell price: 10g", lines);
        Assert.Contains("Iron Ore is a common ore found deep underground and smelted into bars at a furnace.", lines);
        Assert.Contains("Ore Material mined with a pickaxe.", lines);
        Assert.Contains("## Locations", lines);
        Assert.Contains("- The Northern Caves below floor 40", lines);
        Assert.Contains("### Prices", lines);
        Assert.Contains("Season: Spring; Shop: Blacksmith; Price: 75g", lines);
        Assert.Contains("Season: Spring; Shop: Traveling Cart; Price: 100g", lines);
        foreach (var gone in new[] { "edit", "[1]", "Navbox", "Reference text", "Gallery", "sort 10", "A lump", "Iron Ore.png" })
            Assert.DoesNotContain(gone, text, StringComparison.Ordinal);
        Assert.True(lines.IndexOf("## Locations") < lines.IndexOf("### Prices"));

        var chunks = GuideChunker.Chunk(new GuidePage("https://w.example/wiki/Iron_Ore", "Iron Ore", text, 1));
        Assert.Contains(chunks, c => c.Section == "Iron Ore › Locations" && c.Text.Contains("Northern Caves", StringComparison.Ordinal));
    }

    [Fact]
    public void OutlineOfAWholePageReadsItsMainPartAndUnclosedMarkup()
    {
        const string intro = "Brewing makes tea from leaves and hot water in a kettle over a steady fire, and the best brews take patience, " +
            "clean water, fresh leaves and a warm pot that was rinsed with hot water first.";
        const string page = "<!DOCTYPE html><html><head><title>Brewing | Kettle Wiki</title><link rel=\"canonical\" href=\"/wiki/Brewing\"></head>" +
            "<body><header><a href=\"/\">Logo</a></header><nav><ul><li><a href=\"/wiki/Menu\">Menu link</a></li></ul></nav>" +
            "<main><h1>Brewing</h1><p>" + intro + "<p>Steep it for" +
            " three minutes &amp; serve.<ol><li>Boil water<li>Add <b>leaves</b><li>Wait</ol><dl><dt>Tip<dd>Use soft water</dl>" +
            "<table><tr><th>Leaf<td>Green<tr><th>Time<td>3 min</table><div hidden>Secret</div><div aria-hidden=\"true\">Hidden too</div></main>" +
            "<aside class=\"sidebar\">Sidebar words</aside><footer>Footer words</footer><script>var x = '<p>no</p>';</script></body></html>";
        var root = HtmlParser.Parse(page);
        var content = HtmlOutline.Content(root);
        Assert.Equal("main", content.Name);
        Assert.Equal("Brewing", HtmlOutline.Title(root, content));
        Assert.Equal(new Uri("https://k.example/wiki/Brewing"), HtmlOutline.Canonical(root, new Uri("https://k.example/wiki/Brewing?x=1")));
        Assert.Equal(
            "# Brewing\n" + intro + "\nSteep it for three minutes & serve.\n" +
            "1. Boil water\n2. Add leaves\n3. Wait\nTip\nUse soft water\nLeaf: Green\nTime: 3 min",
            HtmlOutline.Text(content, "Brewing | Kettle Wiki"));
        var links = HtmlOutline.Links(root, content, new Uri("https://k.example/wiki/Brewing"));
        Assert.Equal("https://k.example/", links[0].Url.AbsoluteUri);
        Assert.Contains(links, l => l.Url.AbsoluteUri == "https://k.example/wiki/Menu");
    }

    [Fact]
    public void RobotsRulesFollowTheMartletGroupLongestMatchAndSignals()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /\n\nUser-agent: Martlet/1.0\nUser-agent: other\nDisallow: /private\n" +
            "Allow: /private/open$\nDisallow: /*.pdf$\nDisallow: /*?action=\nCrawl-delay: 2.5\n# comment\nDisallow:\n");
        Assert.True(rules.Allows(new Uri("https://s.example/wiki/Page")));
        Assert.False(rules.Allows(new Uri("https://s.example/private/x")));
        Assert.True(rules.Allows(new Uri("https://s.example/private/open")));
        Assert.False(rules.Allows(new Uri("https://s.example/private/open/more")));
        Assert.False(rules.Allows(new Uri("https://s.example/files/a.pdf")));
        Assert.True(rules.Allows(new Uri("https://s.example/files/a.pdf.html")));
        Assert.False(rules.Allows(new Uri("https://s.example/index.php?action=edit")));
        Assert.Equal(TimeSpan.FromSeconds(2.5), rules.CrawlDelay);

        var everyone = RobotsRules.Parse("\uFEFFUser-agent: GPTBot\nDisallow: /\n\nUser-agent: *\nDisallow: /w/File%3A\nAllow: /w/\nDisallow: /w/\n");
        Assert.True(everyone.Allows(new Uri("https://s.example/w/Iron_Ore")));
        Assert.False(everyone.Allows(new Uri("https://s.example/w/File:Iron.png")));
        Assert.True(everyone.Allows(new Uri("https://s.example/robots.txt")));
        Assert.Null(everyone.CrawlDelay);

        Assert.False(RobotsRules.Parse("User-agent: *\nContent-Signal: search=yes, ai-input=no\nAllow: /\n").Allows(new Uri("https://s.example/a")));
        Assert.True(RobotsRules.Parse("User-agent: *\nContent-Signal: search=yes,ai-train=no,use=reference\nAllow: /\n").Allows(new Uri("https://s.example/a")));
        Assert.False(RobotsRules.DisallowAll.Allows(new Uri("https://s.example/a")));
        Assert.False(RobotsRules.Parse("\uFEFFUser-agent: *\nDisallow: /\n").Allows(new Uri("https://s.example/a")));
        Assert.True(RobotsRules.Parse("").Allows(new Uri("https://s.example/a")));
    }

    [Fact]
    public void DiscoveryMakesSlugsAndPrefersWikisAboutTheApp()
    {
        Assert.Equal(["theelderscrollsvskyrim", "the-elder-scrolls-v-skyrim", "elderscrollsvskyrim"], WikiDiscovery.Slugs("The Elder Scrolls V: Skyrim"));
        Assert.Equal(["baldursgate3", "baldurs-gate-3"], WikiDiscovery.Slugs("Baldur's Gate 3"));
        Assert.Equal(["pokemon"], WikiDiscovery.Slugs("Pokémon"));
        Assert.True(WikiDiscovery.IsAbout("The Elder Scrolls V: Skyrim", "Skyrim Wiki", "elderscrolls.fandom.com"));
        Assert.True(WikiDiscovery.IsAbout("Adobe Photoshop", "Photoshop User Guide", "helpx.adobe.com"));
        Assert.False(WikiDiscovery.IsAbout("Elden Ring", "Ring Fit Adventure Wiki"));
        Assert.Equal(new Uri("https://terraria.wiki.gg/de/api.php"), WikiDiscovery.FarmApi(new Uri("https://terraria.wiki.gg/de/wiki/Erz")));
        Assert.Equal(new Uri("https://x.fandom.com/api.php"), WikiDiscovery.FarmApi(new Uri("https://x.fandom.com/wiki/Iron")));

        var ranked = WikiDiscovery.Rank([
            new("Elden Ring - Wikipedia", "https://en.wikipedia.org/wiki/Elden_Ring", ""),
            new("Elden Ring on Steam", "https://store.steampowered.com/app/1245620/ELDEN_RING/", ""),
            new("Elden Ring all bosses", "https://www.youtube.com/watch?v=1", ""),
            new("r/Eldenring", "https://www.reddit.com/r/Eldenring/", ""),
            new("Elden Ring Wiki | Fextralife", "https://eldenring.wiki.fextralife.com/Elden+Ring+Wiki", ""),
            new("Elden Ring Wiki | Fandom", "https://eldenring.fandom.com/wiki/Elden_Ring_Wiki", ""),
            new("Elden Ring Wiki Guide - IGN", "https://www.ign.com/wikis/elden-ring/", ""),
            new("Elden Ring forum", "https://forums.example.com/elden-ring", ""),
            new("Dark Souls Wiki", "https://darksouls.fandom.com/wiki/Dark_Souls_Wiki", ""),
            new("Elden Ring for PS5 | Amazon", "https://www.amazon.co.uk/dp/B09", ""),
            new("Elden Ring review", "https://news.example.com/elden-ring-review", ""),
        ], "Elden Ring");
        Assert.Equal(["eldenring.fandom.com", "eldenring.wiki.fextralife.com", "www.ign.com"], ranked.Select(u => u.Host));
    }

    [Fact]
    public async Task AWikiFarmSiteIsReadThroughItsApiBestPagesFirst()
    {
        var web = new FakeWeb();
        var wiki = new FakeWiki("ironvalley.fandom.com", "Iron Valley")
        {
            MainLinks = ["Iron Ore", "Gold", "Mining", "Disambig", "Stub", "Version 1.2", "Missing"],
            MostLinked = ["User:Admin", "Iron Ore", "Gold", "Redirected"],
            Redirects = { ["Redirected"] = "Copper Ore" },
            Disambiguations = { "Disambig" },
            Categories = { ["Iron Ore"] = ["Category:Ores"], ["Gold"] = ["Category:Ores"], ["Copper Ore"] = ["Category:Ores"], ["Mining"] = ["Category:Skills"] },
        };
        wiki.Add("Iron Ore", IronOre, 9_000);
        // Gold scores above Mining, but Mining is picked first: Iron Ore was already picked from Gold's category.
        wiki.Add("Mining", Article("Mining", "Mining needs a pickaxe."), 40_000);
        wiki.Add("Gold", Article("Gold", "Gold is money."), 6_000);
        wiki.Add("Copper Ore", Article("Copper Ore", "Copper is soft."), 5_000);
        wiki.Add("Hidden Gem", Article("Hidden Gem", "The blacksmith sells gems."), 4_500);
        wiki.Add("Northern Caves", Article("Northern Caves", "Caves in the north."), 4_000);
        wiki.Add("Stub", Article("Stub", "Short."), 300);
        wiki.Add("Disambig", Article("Disambig", "Many things."), 3_000);
        wiki.Add("Version 1.2", Article("Version 1.2", "Patch."), 5_000);
        web.Sites.Add(wiki.Respond);
        var progress = new List<string>();

        var outcome = await new WebGuideBuilder(web, web) { Wait = NoWait }.BuildAsync(new("Iron Valley", [], Fast), new Recorder(progress), default);

        Assert.Null(outcome.Problem);
        Assert.Equal(["Iron Ore", "Mining", "Gold", "Copper Ore", "Hidden Gem", "Northern Caves"], outcome.Pages.Select(p => p.Title));
        Assert.Equal("https://ironvalley.fandom.com/wiki/Iron_Ore", outcome.Pages[0].Url);
        Assert.Contains("Type: Resource", outcome.Pages[0].Text, StringComparison.Ordinal);
        Assert.Equal(["ironvalley.fandom.com"], outcome.Sites);
        Assert.Equal(0, outcome.Failed);
        Assert.Equal(web.Bytes, outcome.Bytes);
        Assert.Equal("Looking for its wiki", progress[0]);
        Assert.Contains("Reading pages (1 of at most 60)", progress);
        Assert.All(progress, p => Assert.DoesNotContain("http", p, StringComparison.Ordinal));
        Assert.DoesNotContain(web.Requests, r => r.Contains("Missing_Page", StringComparison.Ordinal) || r.Contains("User%3ABob", StringComparison.Ordinal));
        Assert.Equal(["Iron Valley wiki"], web.Queries);
    }

    [Fact]
    public async Task OtherSitesAreCrawledOnTheSameSiteAndFolderObeyingRobots()
    {
        var web = new FakeWeb();
        web.Searches["Kettle Quest wiki"] =
        [
            new("Kettle Quest - YouTube", "https://www.youtube.com/watch?v=k", ""),
            new("Kettle Quest - Wikipedia", "https://en.wikipedia.org/wiki/Kettle_Quest", ""),
            new("Teapot Saga Wiki", "https://teapot.fandom.com/wiki/Teapot_Saga", ""),
            new("Kettle Quest Wiki", "https://kq.example.org/wiki/Main", ""),
        ];
        const string site = "https://kq.example.org";
        web.Robots[site] = "User-agent: *\nDisallow: /wiki/Leaves\n\nUser-agent: Martlet\nDisallow: /wiki/Cups\n";
        var menu = "<nav><a href=\"/wiki/Kettles\">K</a><a href=\"/blog/news\">News</a></nav>";
        web.Html[site + "/wiki/Main"] = Page("Kettle Quest Wiki", menu + "<a href=\"/wiki/Kettles\">Kettles</a> <a href=\"/wiki/Kettles#history\">again</a> " +
            "<a href=\"/wiki/Brewing\">Brewing</a> <a href=\"/wiki/Teapots\">Teapots</a> <a href=\"/wiki/Leaves\">Leaves</a> " +
            "<a href=\"/wiki/Cups\">Cups</a> <a href=\"/wiki/Special:Random\">Random</a> <a href=\"/wiki/Kettles?action=edit\">Edit</a> " +
            "<a href=\"/wiki/Talk:Kettles\">Talk</a> <a href=\"/images/a.png\">Picture</a> <a href=\"https://elsewhere.example.com/x\">Away</a>");
        web.Html[site + "/wiki/Kettles"] = Page("Kettles", "<a href=\"/wiki/Brewing\">Brewing</a>");
        web.Html[site + "/wiki/Brewing"] = Page("Brewing", "");
        web.Html[site + "/wiki/Teapots"] = Page("Teapots", "", canonical: "/wiki/Kettles");
        web.Html[site + "/wiki/Leaves"] = Page("Leaves", "");
        web.Html[site + "/wiki/Cups"] = Page("Cups", "");

        var outcome = await new WebGuideBuilder(web, web) { Wait = NoWait }.BuildAsync(new("Kettle Quest", [], Fast), null, default);

        Assert.Null(outcome.Problem);
        Assert.Equal(["Kettle Quest Wiki", "Kettles", "Brewing", "Leaves"], outcome.Pages.Select(p => p.Title));
        Assert.Equal(["kq.example.org"], outcome.Sites);
        Assert.StartsWith("# Kettle Quest Wiki\n", outcome.Pages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("News", outcome.Pages[0].Text, StringComparison.Ordinal);
        foreach (var never in new[] { "/wiki/Cups", "/blog/", "Special", "action=edit", "Talk", ".png", "elsewhere", "youtube", "wikipedia", "teapot.fandom" })
            Assert.DoesNotContain(web.Requests, r => r.Contains(never, StringComparison.Ordinal) && !r.EndsWith("robots.txt", StringComparison.Ordinal));
        Assert.Equal(1, web.Requests.Count(r => r == site + "/robots.txt"));
    }

    [Fact]
    public async Task HelpPagesAreSearchedWhenNoWikiIsFoundAndTheOwnersPagesComeFirst()
    {
        var web = new FakeWeb();
        web.Searches["Pixel Painter wiki"] = [new("Pixel Painter on Steam", "https://store.steampowered.com/app/1", "")];
        web.Searches["Pixel Painter help"] = [new("Pixel Painter Help Center", "https://help.pixelpainter.example.com/docs/start.html", "")];
        web.Html["https://help.pixelpainter.example.com/docs/start.html"] = Page("Getting started", "<a href=\"layers.html\">Layers</a>");
        web.Html["https://help.pixelpainter.example.com/docs/layers.html"] = Page("Layers", "");
        var builder = new WebGuideBuilder(web, web) { Wait = NoWait };

        var outcome = await builder.BuildAsync(new("Pixel Painter", [], Fast), null, default);
        Assert.Equal(["Getting started", "Layers"], outcome.Pages.Select(p => p.Title));
        Assert.Equal(["Pixel Painter wiki", "Pixel Painter help"], web.Queries);
        Assert.Contains(web.Requests, r => r.StartsWith("https://pixelpainter.fandom.com/", StringComparison.Ordinal));

        // The owner's page: a MediaWiki site found from its page's EditURI link, read from that article first; no search, no probes.
        var owner = new FakeWeb();
        var wiki = new FakeWiki("kettlewiki.example.net", "Kettle", articlePath: "/$1", api: "/mediawiki/api.php") { MainLinks = ["Mining"] };
        wiki.Add("Iron Ore", IronOre, 9_000);
        wiki.Add("Mining", Article("Mining", "Mining needs a pickaxe."), 8_000);
        owner.Sites.Add(wiki.Respond);
        owner.Html["https://kettlewiki.example.net/Iron_Ore"] = "<html><head><link rel=\"EditURI\" type=\"application/rsd+xml\" " +
            "href=\"https://kettlewiki.example.net/mediawiki/api.php?action=rsd\"/></head><body>Iron Ore</body></html>";
        outcome = await new WebGuideBuilder(owner, owner) { Wait = NoWait }.BuildAsync(
            new("Kettle", ["https://kettlewiki.example.net/Iron_Ore"], Fast), null, default);
        Assert.Equal(["Iron Ore", "Mining"], outcome.Pages.Select(p => p.Title));
        Assert.Equal("https://kettlewiki.example.net/Iron_Ore", outcome.Pages[0].Url);
        Assert.Empty(owner.Queries);
        Assert.DoesNotContain(owner.Requests, r => r.Contains("fandom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWikiThatForbidsItsApiIsReadFromItsPages()
    {
        var web = new FakeWeb();
        const string site = "https://blockwiki.example.org";
        web.Robots[site] = "User-agent: *\nDisallow: /*api.php\nDisallow: /*?action=\n";
        web.Html[site + "/w/Main_Page"] = "<html><head><meta name=\"generator\" content=\"MediaWiki 1.43.1\"/></head><body><div class=\"mw-parser-output\">" +
            "<p>" + Words("Welcome to the wiki about blocks and crafting tables.") + "</p><a href=\"/w/Stone\">Stone</a> <a href=\"/w/Wood\">Wood</a> " +
            "<a href=\"/w/Special:Random\">Random</a> <a href=\"/w/File:Stone.png\">File</a></div></body></html>";
        web.Html[site + "/w/Stone"] = Page("Stone", "");
        web.Html[site + "/w/Wood"] = Page("Wood", "");

        var outcome = await new WebGuideBuilder(web, web) { Wait = NoWait }.BuildAsync(new("Block Game", [site + "/w/Main_Page"], Fast), null, default);

        Assert.Equal(3, outcome.Pages.Count);
        Assert.DoesNotContain(web.Requests, r => r.Contains("api.php", StringComparison.Ordinal) || r.Contains("Special", StringComparison.Ordinal) ||
            r.Contains("File", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PagesBytesAndTimeAreCappedAndCancellingStopsAtOnce()
    {
        var pages = await BuildWikiAsync(new GuideBuildLimits { Delay = TimeSpan.Zero, Pages = 2 });
        Assert.Equal(2, pages.Pages.Count);

        var bytes = await BuildWikiAsync(new GuideBuildLimits { Delay = TimeSpan.Zero, Bytes = 3_000 });
        Assert.True(bytes.Bytes <= 3_000);
        Assert.InRange(bytes.Pages.Count, 1, 5);

        var clock = Stopwatch.StartNew();
        var slow = await BuildWikiAsync(new GuideBuildLimits { Delay = TimeSpan.Zero, Time = TimeSpan.FromMilliseconds(600) }, latency: TimeSpan.FromMilliseconds(100));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.InRange(slow.Pages.Count, 0, 5);

        using var cancel = new CancellationTokenSource();
        var progress = new Recorder([], words => { if (words.StartsWith("Reading pages (2", StringComparison.Ordinal)) cancel.Cancel(); });
        clock.Restart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildWikiAsync(Fast, progress: progress, token: cancel.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EachSiteGetsTheLongerOfTheDelayAndItsCrawlDelay()
    {
        var waits = new List<TimeSpan>();
        Task Record(TimeSpan pause, CancellationToken _)
        {
            waits.Add(pause);
            return Task.CompletedTask;
        }
        await BuildWikiAsync(new GuideBuildLimits { Delay = TimeSpan.FromSeconds(1), Pages = 2 }, robots: "User-agent: *\nCrawl-delay: 7\n", wait: Record);
        Assert.NotEmpty(waits);
        Assert.All(waits, w => Assert.InRange(w, TimeSpan.FromSeconds(6.5), TimeSpan.FromSeconds(7)));

        waits.Clear();
        await BuildWikiAsync(new GuideBuildLimits { Delay = TimeSpan.FromSeconds(3), Pages = 2 }, robots: "User-agent: *\nCrawl-delay: 1\n", wait: Record);
        Assert.All(waits, w => Assert.InRange(w, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3)));

        // A crawl delay longer than the job may take ends the job instead of waiting.
        waits.Clear();
        var late = await BuildWikiAsync(new GuideBuildLimits { Delay = TimeSpan.Zero, Time = TimeSpan.FromMinutes(1) }, robots: "User-agent: *\nCrawl-delay: 3600\n", wait: Record);
        Assert.Empty(waits);
        Assert.Equal("reading up took too long", late.Problem);
    }

    [Fact]
    public async Task WithoutDocumentsTheBestSearchResultIsReadAndAFailedSearchIsAProblem()
    {
        var web = new SearchOnly();
        var outcome = await new WebGuideBuilder(web, web).BuildAsync(new("Skyrim", []), null, default);
        Assert.Equal(["https://elderscrolls.fandom.com/wiki/Skyrim"], outcome.Pages.Select(p => p.Url));

        var broken = new FakeWeb { SearchBroken = true };
        outcome = await new WebGuideBuilder(broken, broken) { Wait = NoWait }.BuildAsync(new("Nothing Here", [], Fast), null, default);
        Assert.Equal("the web search didn't work (it answered 503)", outcome.Problem);
        Assert.Empty(outcome.Pages);
        Assert.Equal(1, broken.SearchCalls);
    }

    [Fact]
    public async Task ASiteThatSaysStopRedirectsElsewhereOrAnswersOddlyIsLeftAlone()
    {
        // 429 on the page list: nothing more is asked of that site.
        var busy = new FakeWeb { Status = url => url.Host == "capped.wiki.gg" && url.Query.Contains("generator=links", StringComparison.Ordinal) ? 429 : null };
        var wiki = new FakeWiki("capped.wiki.gg", "Capped") { MainLinks = ["A"] };
        wiki.Add("A", Article("A", "Page A."), 5_000);
        busy.Sites.Add(wiki.Respond);
        await new WebGuideBuilder(busy, busy) { Wait = NoWait }.BuildAsync(new("Capped", ["https://capped.wiki.gg/wiki/A"], Fast), null, default);
        var refused = busy.Requests.FindIndex(r => r.Contains("generator=links", StringComparison.Ordinal));
        Assert.True(refused > 0);
        Assert.DoesNotContain(busy.Requests.Skip(refused + 1), r => r.Contains("capped.wiki.gg", StringComparison.Ordinal));

        // A redirect to a site whose robots.txt forbids Martlet: the page isn't kept.
        var moved = new FakeWeb();
        moved.Redirects["https://old.example.org/guide"] = "https://new.example.org/guide";
        moved.Html["https://new.example.org/guide"] = Page("Guide", "");
        moved.Robots["https://new.example.org"] = "User-agent: Martlet\nDisallow: /\n";
        var outcome = await new WebGuideBuilder(moved, moved) { Wait = NoWait }.BuildAsync(new("Some Tool", ["https://old.example.org/guide"], Fast), null, default);
        Assert.DoesNotContain(outcome.Pages, p => p.Url.Contains("new.example.org", StringComparison.Ordinal));
        Assert.Contains("https://new.example.org/robots.txt", moved.Requests);

        // An api.php that answers JSON of another shape is no wiki, not a crash.
        var odd = new FakeWeb();
        odd.Sites.Add(url => url.Host == "weird.fandom.com" && url.AbsolutePath == "/api.php" ? ("application/json", "[]") : null);
        outcome = await new WebGuideBuilder(odd, odd) { Wait = NoWait }.BuildAsync(new("Weird", ["https://weird.fandom.com/wiki/X"], Fast), null, default);
        Assert.Equal("no page about it could be read", outcome.Problem);

        Assert.Equal("/", SiteCrawler.Folder(new Uri("https://h.example//guide/x.html"), []));
        Assert.Equal("/docs/", SiteCrawler.Folder(new Uri("https://h.example/docs/a.html"),
            [.. Enumerable.Range(0, 5).Select(i => new Uri($"https://h.example/docs/{i}.html"))]));
    }

    [Fact]
    public async Task WebAccessReadsDocumentsWithTheGuideAgentUpToTheirCap()
    {
        var body = "{\"x\":\"" + new string('a', 3_000) + "\"}";
        using var server = new LoopbackServer(path => path switch
        {
            "/data.json" => (200, "application/json; charset=utf-8", body),
            "/page" => (200, "text/html", "<p>Hi</p>"),
            _ => (404, "text/html", "no"),
        });
        using var web = new WebAccess(allowLoopback: true);
        var document = await web.ReadAsync(new Uri(server.Url + "data.json"), 1_000, default);
        Assert.True(document.Cut);
        Assert.Equal(1_000, document.Bytes);
        Assert.Equal("application/json", document.MediaType);
        Assert.Equal(body[..1_000], document.Body);
        Assert.Equal(WebAccess.GuideAgent, server.Agents[^1]);
        var whole = await web.ReadAsync(new Uri(server.Url + "page"), 1_000, default);
        Assert.False(whole.Cut);
        var missing = await Assert.ThrowsAsync<WebResearchException>(() => web.ReadAsync(new Uri(server.Url + "nothing"), 1_000, default));
        Assert.Equal(404, missing.Status);
        // Research's page reader is unchanged: no JSON, its own agent.
        await Assert.ThrowsAsync<WebResearchException>(() => web.FetchAsync(new Uri(server.Url + "data.json"), default));
        Assert.Contains("web research", server.Agents[^1], StringComparison.Ordinal);
    }

    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

    private static async Task<GuideBuildOutcome> BuildWikiAsync(GuideBuildLimits limits, TimeSpan latency = default, IProgress<string>? progress = null,
        CancellationToken token = default, string? robots = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        var web = new FakeWeb { Latency = latency };
        var wiki = new FakeWiki("capped.wiki.gg", "Capped") { MainLinks = ["A", "B", "C", "D", "E", "F"] };
        foreach (var title in wiki.MainLinks) wiki.Add(title, Article(title, "Page " + title + "."), 5_000);
        web.Sites.Add(wiki.Respond);
        if (robots is not null) web.Robots["https://capped.wiki.gg"] = robots;
        return await new WebGuideBuilder(web, web) { Wait = wait ?? NoWait }.BuildAsync(
            new("Capped", ["https://capped.wiki.gg/wiki/Capped_Wiki"], limits), progress, token);
    }

    private static string Words(string sentence) => string.Join(' ', Enumerable.Repeat(sentence, 6));

    private static string Article(string title, string text) =>
        $"<div class=\"mw-parser-output\"><p>{Words(text)}</p><h2>About {title}</h2><p>{Words(title + " facts and details are listed in this section.")}</p></div>";

    private static string Page(string title, string links, string? canonical = null) =>
        $"<html><head><title>{title} | Site</title>{(canonical is null ? "" : $"<link rel=\"canonical\" href=\"{canonical}\">")}</head><body>" +
        $"<main><h1>{title}</h1><p>{Words(title + " is described here in plain words.")}</p>{links}</main><footer>Footer</footer></body></html>";

    private sealed class Recorder(List<string> reports, Action<string>? then = null) : IProgress<string>
    {
        public void Report(string value)
        {
            reports.Add(value);
            then?.Invoke(value);
        }
    }

    private sealed class FakeWeb : IWebSearch, IWebFetch, IWebDocuments
    {
        public List<Func<Uri, (string Type, string Body)?>> Sites { get; } = [];
        public Dictionary<string, string> Html { get; } = [];
        public Dictionary<string, string> Robots { get; } = [];
        public Dictionary<string, IReadOnlyList<WebSearchResult>> Searches { get; } = [];
        public List<string> Requests { get; } = [];
        public List<string> Queries { get; } = [];
        public Dictionary<string, string> Redirects { get; } = [];
        public Func<Uri, int?>? Status { get; init; }
        public int SearchCalls { get; private set; }
        public long Bytes { get; private set; }
        public TimeSpan Latency { get; init; }
        public bool SearchBroken { get; init; }

        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            SearchCalls++;
            if (SearchBroken) throw new WebResearchException("it answered 503", 503);
            Queries.Add(query);
            return Task.FromResult(Searches.TryGetValue(query, out var results) ? results : []);
        }

        public Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken) => throw new InvalidOperationException("documents are read instead");

        public async Task<WebDocument> ReadAsync(Uri url, int maxBytes, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(url.AbsoluteUri);
            if (Latency > TimeSpan.Zero) await Task.Delay(Latency, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Status?.Invoke(url) is { } status) throw new WebResearchException($"it answered {status}", status);
            if (Redirects.TryGetValue(url.AbsoluteUri, out var target)) url = new Uri(target);
            (string Type, string Body)? response = null;
            if (url.AbsolutePath == "/robots.txt")
                response = Robots.TryGetValue(url.GetLeftPart(UriPartial.Authority), out var robots) ? ("text/plain", robots) : null;
            else if (Html.TryGetValue(url.AbsoluteUri, out var html)) response = ("text/html", html);
            else foreach (var site in Sites) response ??= site(url);
            if (response is not { } found) throw new WebResearchException("it answered 404", 404);
            var bytes = Encoding.UTF8.GetBytes(found.Body);
            var cut = bytes.Length > maxBytes;
            var kept = cut ? maxBytes : bytes.Length;
            Bytes += kept;
            return new(url.AbsoluteUri, found.Type, Encoding.UTF8.GetString(bytes, 0, kept), kept, cut);
        }
    }

    private sealed class SearchOnly : IWebSearch, IWebFetch
    {
        public Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WebSearchResult>>([new("Skyrim on Steam", "https://store.steampowered.com/app/489830", ""),
                new("Skyrim Wiki | Fandom", "https://elderscrolls.fandom.com/wiki/Skyrim", "")]);

        public Task<WebPage> FetchAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult(new WebPage(url.AbsoluteUri, "Skyrim", "Skyrim is a game.", 20));
    }

    // A MediaWiki api.php answering siteinfo, main page links, Mostlinked, page info and parse like a real wiki.
    private sealed class FakeWiki(string host, string name, string articlePath = "/wiki/$1", string api = "/api.php")
    {
        private readonly Dictionary<string, (string Html, int Length)> articles = [];
        public List<string> MainLinks { get; init; } = [];
        public List<string> MostLinked { get; init; } = [];
        public Dictionary<string, string> Redirects { get; } = [];
        public HashSet<string> Disambiguations { get; } = [];
        public Dictionary<string, string[]> Categories { get; } = [];

        public void Add(string title, string html, int length) => articles[title] = (html, length);

        public (string Type, string Body)? Respond(Uri url)
        {
            if (!url.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || url.AbsolutePath != api) return null;
            var query = url.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
            object body = query.GetValueOrDefault("action") switch
            {
                "parse" => Parse(query["page"]),
                "query" when query.ContainsKey("meta") => SiteInfo(),
                "query" when query.GetValueOrDefault("generator") == "links" => new { query = new { pages = MainLinks.Select(Info).ToArray() } },
                "query" when query.GetValueOrDefault("list") == "querypage" => new
                {
                    query = new { querypage = new { results = MostLinked.Select((t, i) => new { value = 100 - i, ns = t.StartsWith("User:", StringComparison.Ordinal) ? 2 : 0, title = t }).ToArray() } }
                },
                "query" when query.ContainsKey("titles") => Titles(query["titles"].Split('|')),
                _ => new { error = new { code = "badvalue" } },
            };
            return ("application/json", JsonSerializer.Serialize(body));
        }

        private object SiteInfo() => new
        {
            batchcomplete = true,
            query = new
            {
                general = new { mainpage = name + " Wiki", sitename = name + " Wiki", generator = "MediaWiki 1.43.0", server = "https://" + host, articlepath = articlePath },
                namespaces = new Dictionary<string, object>
                {
                    ["0"] = new { id = 0, name = "", content = true },
                    ["2"] = new { id = 2, name = "User", canonical = "User", content = false },
                    ["6"] = new { id = 6, name = "File", canonical = "File", content = false },
                },
                namespacealiases = new[] { new { id = 6, alias = "Image" } },
                statistics = new { articles = 500 },
            },
        };

        private object Info(string title)
        {
            if (!articles.TryGetValue(title, out var article)) return new { ns = 0, title, missing = true };
            var categories = Categories.GetValueOrDefault(title, []).Select(c => new { ns = 14, title = c }).ToArray();
            return Disambiguations.Contains(title)
                ? new { ns = 0, title, length = article.Length, categories, pageprops = new { disambiguation = "" } }
                : new { ns = 0, title, length = article.Length, categories };
        }

        private object Titles(string[] titles)
        {
            var redirects = titles.Where(Redirects.ContainsKey).Select(t => new { from = t, to = Redirects[t] }).ToArray();
            return new { query = new { redirects, pages = titles.Select(t => Redirects.GetValueOrDefault(t, t)).Distinct().Select(Info).ToArray() } };
        }

        private object Parse(string page)
        {
            var title = Redirects.GetValueOrDefault(page.Replace('_', ' '), page.Replace('_', ' '));
            return articles.TryGetValue(title, out var article) ? new { parse = new { title, pageid = 1, text = article.Html } } : new { error = new { code = "missingtitle" } };
        }
    }

    // A tiny HTTP/1.1 server on 127.0.0.1 that records each request's user agent.
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();

        public LoopbackServer(Func<string, (int Status, string Type, string Body)> respond)
        {
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                    catch (Exception) { return; }
                    using (client)
                    {
                        var stream = client.GetStream();
                        var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var request = await reader.ReadLineAsync() ?? "";
                        string? line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                            if (line.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) lock (Agents) Agents.Add(line[11..].Trim());
                        var (status, type, body) = respond(request.Split(' ')[1]);
                        var bytes = Encoding.UTF8.GetBytes(body);
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(bytes);
                    }
                }
            });
        }

        public string Url { get; }
        public List<string> Agents { get; } = [];

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }
}
