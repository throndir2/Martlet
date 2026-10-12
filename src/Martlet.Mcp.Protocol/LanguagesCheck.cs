using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Martlet.Conversation;
using Martlet.Core.Lorebooks;
using Martlet.Core.Text;
using Martlet.Memory;

namespace Martlet.Mcp;

/// <summary>search_languages_check: search in languages other than English and lorebook word forms (docs/MEMORY.md, "Lexical
/// retrieval and reranking"; docs/CONVERSATION.md, past conversations; docs/LOREBOOKS.md). It rehearses the production code on
/// synthetic text in disposable folders: SearchTerms (English terms unchanged, the language of a text, pairs of characters for
/// Chinese, Japanese and Korean, light stems), memory recall (Martlet.Memory) and past-conversation recall (ConversationHistory
/// and PastConversations) in Spanish, French, German, Portuguese, Italian, Chinese, Japanese and Korean, and LorebookScanner with
/// word forms off and on, with its SillyTavern export. It never opens the owner's data. Nothing leaves this PC.</summary>
internal static class LanguagesCheck
{
    private static readonly (string Language, string Fact, string Question)[] Facts =
    [
        ("Spanish", "El perro del usuario se llama Toby y le encantan las pelotas.", "¿Cómo se llama mi perro?"),
        ("French", "Les chevaux de l'utilisateur vivent dans une écurie près de la rivière.", "Où est-ce que mon cheval vit ?"),
        ("German", "Die Katzen des Nutzers schlafen gern auf der Heizung.", "Wo schläft meine Katze am liebsten?"),
        ("Portuguese", "O usuário tem dois cachorros e adora os animais do jardim.", "Qual é o meu cachorro favorito?"),
        ("Italian", "L'utente ha due gatti che dormono sul divano.", "Dove dorme il mio gatto?"),
        ("Chinese", "用户最喜欢的电影是千与千寻。", "我最喜欢哪部电影？"),
        ("Japanese", "ユーザーの猫はタマという名前です。", "私の猫の名前は何ですか？"),
        ("Korean", "사용자는 매일 아침 공원에서 고양이를 산책시킨다.", "내 고양이는 어디에서 산책해?"),
        ("English", "The user's sister lives in Lisbon and teaches piano.", "Where does my sister live?")
    ];

    private static readonly (string Language, string Said, string Reply, string Asked)[] Exchanges =
    [
        ("Spanish", "Ayer planté tres tomateras en el huerto.", "¡Qué bien! Riégalas por la mañana.", "¿Te acuerdas de lo que hablamos sobre las tomateras?"),
        ("French", "Je prépare un voyage à Lisbonne en juin.", "Juin est parfait pour Lisbonne.", "Tu te souviens de mon voyage à Lisbonne ?"),
        ("German", "Mein Fahrrad hat einen platten Reifen.", "Flick den Schlauch oder kauf einen neuen.", "Weißt du noch, was wir über mein Fahrrad gesagt haben?"),
        ("Portuguese", "Comprei uma guitarra nova para aprender.", "Que legal! Comece com acordes simples.", "Você lembra do que eu disse sobre a guitarra?"),
        ("Italian", "Sto leggendo un romanzo di fantascienza.", "Bello! Come si intitola?", "Ti ricordi il romanzo di cui abbiamo parlato?"),
        ("Chinese", "我周末去爬了黄山。", "黄山的日出很美！", "你还记得我们聊过的黄山吗？"),
        ("Japanese", "週末に富士山に登りました。", "すごいですね！", "富士山の話、覚えてる？"),
        ("Korean", "주말에 제주도에 다녀왔어.", "제주도 바다 정말 예쁘지!", "제주도 얘기 기억나?")
    ];

    // Messages that are not about an earlier conversation, in each language: recall must stay out of their way.
    private static readonly string[] Ordinary =
    [
        "¿Qué tiempo hace hoy?", "Tu peux m'aider avec ce code ?", "Wie spät ist es?", "Qual é a capital da Austrália?",
        "Che ore sono adesso?", "今天天气怎么样？", "今日はいい天気ですね。", "오늘 날씨 어때?", "Remember to buy milk."
    ];

    internal static async Task<object> RunAsync(CancellationToken cancellation)
    {
        var steps = new List<object>();
        var failures = new List<string>();
        void Step(string name, bool passed, object detail)
        {
            steps.Add(new { name, passed, detail });
            if (!passed) failures.Add(name);
        }
        var root = Path.Combine(Path.GetTempPath(), "martlet-languages-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Terms(Step);
            await MemoryAsync(Path.Combine(root, "memory"), Step, cancellation);
            await HistoryAsync(Path.Combine(root, "history"), Step, cancellation);
            Lorebooks(Step);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return new
        {
            passed = failures.Count == 0,
            failures,
            steps,
            note = "Synthetic text in disposable folders with the production code (SearchTerms, Martlet.Memory, ConversationHistory, " +
                "PastConversations and LorebookScanner). It never opens your data."
        };
    }

    private static void Terms(Action<string, bool, object> Step)
    {
        var english = SearchTerms.Of("Where do I find the Iron ORE? The player's swords!");
        Step("English text gets the terms it always got", english.SequenceEqual(["find", "iron", "ore", "player", "sword"]), new { terms = english });
        var links = "Download it from https://developer.android.com/develop/os and see the docs at example.de/la";
        Step("English text with web addresses stays English (\"com\", \"os\", \"de\" and \"la\" are not a sign of another language)",
            SearchTerms.LanguageOf(SearchTerms.Words(links)) == SearchLanguage.English, new { terms = SearchTerms.Of(links) });
        foreach (var (language, text) in new[]
        {
            (SearchLanguage.Spanish, "Ayer hablamos de los perros de mi hermana."),
            (SearchLanguage.French, "Tu te souviens de mon chat et des chevaux ?"),
            (SearchLanguage.German, "Erinnerst du dich an meine Katze und die Drachen?"),
            (SearchLanguage.Portuguese, "Você lembra do meu cachorro e dos animais?"),
            (SearchLanguage.Italian, "Ti ricordi il mio gatto e gli amici?")
        })
        {
            var found = SearchTerms.LanguageOf(SearchTerms.Words(text));
            Step($"\"{text}\" reads as {language}", found == language, new { language = found.ToString(), terms = SearchTerms.Of(text) });
        }
        Step("Italian, German, French and Spanish word forms share a stem (gatti/gatto, Drachen/Drache, dernières/dernier, perros/perra)",
            SearchTerms.Stem("gatti", SearchLanguage.Italian) == SearchTerms.Stem("gatto", SearchLanguage.Italian) &&
            SearchTerms.Stem("drachen", SearchLanguage.German) == SearchTerms.Stem("drache", SearchLanguage.German) &&
            SearchTerms.Stem("dernieres", SearchLanguage.French) == SearchTerms.Stem("dernier", SearchLanguage.French) &&
            SearchTerms.Stem("perros", SearchLanguage.Spanish) == SearchTerms.Stem("perra", SearchLanguage.Spanish),
            new { italian = SearchTerms.Stem("gatti", SearchLanguage.Italian), german = SearchTerms.Stem("drachen", SearchLanguage.German) });
        var tokyo = SearchTerms.Of("東京大学");
        Step("Chinese and Japanese text without spaces is read as pairs of characters (\"東京大学\" is \"東京\", \"京大\" and \"大学\")",
            tokyo.SequenceEqual(["東京", "京大", "大学"]), new { terms = tokyo, japanese = SearchTerms.Of("ポケモンセンターに行きました"), korean = SearchTerms.Of("고양이를 좋아해요") });

        // Speed: the terms of English text, which every search reads on the reply's path.
        var corpus = Enumerable.Range(0, 400).Select(i =>
            $"Message {i}: the player found {i % 7} iron swords near the old mill, and asked where the crafting table and the blue potions were.").ToArray();
        for (var round = 0; round < 30; round++)
            foreach (var text in corpus) _ = SearchTerms.Of(text);
        var best = double.MaxValue;
        for (var round = 0; round < 10; round++)
        {
            var timer = Stopwatch.StartNew();
            foreach (var text in corpus) _ = SearchTerms.Of(text);
            best = Math.Min(best, timer.Elapsed.TotalMicroseconds);
        }
        var characters = corpus.Sum(text => text.Length);
        Step("reading the terms of English text stays fast (under 50 µs per 1,000 characters on a desktop PC)",
            best / characters * 1000 < 50, new { microsecondsPer1000Characters = Math.Round(best / characters * 1000, 2), characters });
    }

    private static async Task MemoryAsync(string folder, Action<string, bool, object> Step, CancellationToken cancellation)
    {
        var preview = MemoryStoreActivationPreview.Create(folder);
        using var store = MemoryStore.Open(preview, preview.Authorize(MemoryConsentDecision.Allow));
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (language, fact, _) in Facts)
            ids[language] = (await store.SaveAsync(new()
            {
                Content = fact,
                Provenance = MemoryProvenance.UserEntry(Guid.NewGuid(), DateTimeOffset.UtcNow),
                Retention = MemoryRetention.UntilDeleted()
            }, cancellation)).Fact.Id;
        foreach (var (language, fact, question) in Facts)
        {
            var hits = MemoryQuery.TryFromBoundedSource(question, 5) is { } bounded ? (await store.RetrieveAsync(bounded, cancellation)).Hits : [];
            Step($"memory: \"{question}\" ({language}) recalls \"{fact}\" first", hits.Count > 0 && hits[0].Fact.Id == ids[language],
                new { first = hits.Select(hit => hit.Fact.Content).FirstOrDefault(), results = hits.Count });
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task HistoryAsync(string folder, Action<string, bool, object> Step, CancellationToken cancellation)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Check", TimeSpan.FromHours(-7), "Check", "Check");
        // Saturday 2026-10-03, 15:00 in the check's zone.
        var now = new DateTimeOffset(2026, 10, 3, 22, 0, 0, TimeSpan.Zero);
        var clock = new Clock(now);
        var store = new ConversationHistory(folder, clock);
        await store.LoadAsync(cancellation);
        var said = new Dictionary<string, HistoryExchange>(StringComparer.Ordinal);
        for (var i = 0; i < Exchanges.Length; i++)
        {
            clock.Now = now.AddDays(-2 - i);
            said[Exchanges[i].Language] = await store.AppendAsync(Guid.NewGuid(), HistoryInputKind.Typed, Exchanges[i].Said, Exchanges[i].Reply,
                null, cancellation);
        }
        clock.Now = now;
        foreach (var (language, user, _, asked) in Exchanges)
        {
            var recalled = PastConversations.Recall(store, asked, now, zone, null);
            Step($"past conversations: \"{asked}\" ({language}) brings back \"{user}\"",
                PastConversations.RefersToPast(asked) && recalled.Count > 0 && recalled.Any(exchange => exchange.Id == said[language].Id),
                new { refersToPast = PastConversations.RefersToPast(asked), recalled = recalled.Select(exchange => exchange.User) });
        }
        var mentioned = Ordinary.Where(PastConversations.RefersToPast).ToArray();
        Step("ordinary messages in those languages (and \"Remember to buy milk.\") don't look in the record", mentioned.Length == 0,
            new { checkedMessages = Ordinary.Length, mentioned });
        var times = new (string Text, int From, int To)[]
        {
            ("hace 3 días", 3, 2), ("ayer", 1, 0), ("il y a 3 jours", 3, 2), ("vor 3 Tagen", 3, 2), ("há 3 dias", 3, 2), ("3 giorni fa", 3, 2),
            ("3天前", 3, 2), ("昨天", 1, 0), ("3日前", 3, 2), ("昨日", 1, 0), ("3일 전", 3, 2), ("어제", 1, 0)
        };
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var wrong = times.Where(time => PastConversations.Window(time.Text, now, zone) is not { } span ||
            TimeZoneInfo.ConvertTime(span.From, zone).Date != today.AddDays(-time.From) ||
            TimeZoneInfo.ConvertTime(span.To, zone).Date != today.AddDays(-time.To)).Select(time => time.Text).ToArray();
        Step("times in those languages name the right days (\"hace 3 días\", \"vor 3 Tagen\", \"3天前\", \"어제\"...)", wrong.Length == 0,
            new { times = times.Length, wrong });
        var lastWeek = PastConversations.Window("la semana pasada", now, zone);
        var english = PastConversations.Window("last week", now, zone);
        Step("\"la semana pasada\", \"上周\" and \"letzte Woche\" are last week", lastWeek == english &&
            PastConversations.Window("上周", now, zone) == english && PastConversations.Window("letzte Woche", now, zone) == english, new { });

        foreach (var message in Ordinary.Concat(Exchanges.Select(exchange => exchange.Asked)))
            for (var round = 0; round < 50; round++) _ = PastConversations.RefersToPast(message);
        var timer = Stopwatch.StartNew();
        var messages = 0;
        for (var round = 0; round < 50; round++)
            foreach (var message in Ordinary.Concat(Exchanges.Select(exchange => exchange.Asked)))
            {
                _ = PastConversations.RefersToPast(message);
                messages++;
            }
        var each = timer.Elapsed.TotalMicroseconds / messages;
        Step("recognizing a reference to an earlier conversation stays quick (under 50 µs a message)", each < 50,
            new { microsecondsPerMessage = Math.Round(each, 2), messages });
    }

    private static void Lorebooks(Action<string, bool, object> Step)
    {
        LorebookLibrary Library(bool wordForms, params LorebookEntry[] entries) => LorebookLibrary.Create() with
        {
            MatchWordForms = wordForms,
            Books = [new Lorebook { Id = Guid.NewGuid(), Name = "Check", Entries = entries }]
        };
        LorebookEntry Entry(int uid, string key, bool? wordForms = null, bool? caseSensitive = null) =>
            new() { Uid = uid, Keys = [key], Content = "Lore " + uid, MatchWordForms = wordForms, CaseSensitive = caseSensitive };
        string[] Triggered(LorebookLibrary library, string message) =>
            [.. LorebookScanner.Scan(library, new(message, []), _ => 0).Included.Select(hit => hit.Entry.Content)];

        var dragon = Entry(1, "dragon");
        Step("word forms off: \"dragon\" doesn't match \"dragons\" (exactly as SillyTavern)",
            Triggered(Library(false, dragon), "Two dragons flew over the castle.").Length == 0, new { });
        Step("word forms on: \"dragon\" matches \"dragons\"",
            Triggered(Library(true, dragon), "Two dragons flew over the castle.").SequenceEqual(["Lore 1"]), new { });
        Step("an entry's own choice wins over the scan setting",
            Triggered(Library(false, Entry(1, "dragon", wordForms: true)), "Two dragons flew over the castle.").SequenceEqual(["Lore 1"]) &&
            Triggered(Library(true, Entry(1, "dragon", wordForms: false)), "Two dragons flew over the castle.").Length == 0, new { });
        Step("several words in order: \"red dragon\" matches \"Red Dragons\"",
            Triggered(Library(true, Entry(2, "red dragon")), "The Red Dragons attacked.").SequenceEqual(["Lore 2"]), new { });
        var rose = Entry(3, "Rose", caseSensitive: true);
        Step("a case-sensitive key keeps its capital letters: \"Rose\" matches \"Roses\", never \"the sun rose\"",
            Triggered(Library(true, rose), "The Roses are coming to dinner.").SequenceEqual(["Lore 3"]) &&
            Triggered(Library(true, rose), "The sun rose early.").Length == 0, new { });
        Step("German text: \"Drache\" matches \"Drachen\"",
            Triggered(Library(true, Entry(4, "Drache")), "Die Kinder sahen zwei Drachen über dem Dorf.").SequenceEqual(["Lore 4"]), new { });
        Step("a /regex/ key is unchanged with word forms on",
            Triggered(Library(true, Entry(5, "/^dragon$/i")), "dragons").Length == 0 &&
            Triggered(Library(true, Entry(5, "/^dragon$/i")), "Dragon").SequenceEqual(["Lore 5"]), new { });

        var plain = Encoding.UTF8.GetString(SillyTavernLorebooks.Export(Library(false, dragon).Books[0]));
        var chosen = Library(false, Entry(1, "dragon", wordForms: true)).Books[0];
        var json = SillyTavernLorebooks.Export(chosen);
        var back = SillyTavernLorebooks.Import(new MemoryStream(json), "Check").Book.Entries[0];
        Step("SillyTavern export: unchanged without the choice, and an entry's choice comes back on import",
            !plain.Contains("matchWordForms", StringComparison.Ordinal) && back.MatchWordForms == true, new { });

        static string Letters(int i) => new([.. i.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(digit => (char)('k' + digit - '0'))]);
        var big = Library(true, [.. Enumerable.Range(0, 2_000).Select(i => Entry(i, "creature" + Letters(i)))]);
        var message = string.Join(' ', Enumerable.Range(0, 60).Select(i => "word" + Letters(i))) + " creature" + Letters(1999) + "s";
        for (var round = 0; round < 5; round++) _ = LorebookScanner.Scan(big, new(message, []), _ => 0);
        var timer = Stopwatch.StartNew();
        var included = LorebookScanner.Scan(big, new(message, []), _ => 0).Included.Count;
        Step("2,000 entries with word forms on scan a message in a few milliseconds", timer.Elapsed.TotalMilliseconds < 50 && included == 1,
            new { milliseconds = Math.Round(timer.Elapsed.TotalMilliseconds, 2), included });
    }
}
