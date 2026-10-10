using System.Globalization;

namespace Martlet.Core.Planning;

/// <summary>Works out one answer from every source's answer (docs/MODEL_CATALOG.md, What to do about conflicts). It walks the
/// trust levels in order and uses the first level where a source answers. When sources at that level disagree, the answer is
/// Unknown (<see cref="CatalogFact.Disagree"/>): Martlet then offers a short test instead of guessing. No source overwrites
/// another: every answer stays in <see cref="CatalogFact.Answers"/>.</summary>
public static class CatalogResolver
{
    /// <summary>The source name of a route's level 3 answer: the model's own facts.</summary>
    public const string ModelSource = "model";

    /// <summary>A model fact (what the weights take), by <see cref="CatalogSources.ModelLevel"/>.</summary>
    public static CatalogFact ResolveModel(string key, IReadOnlyList<CatalogAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        return Resolve(key, answers.Select(a => (CatalogSources.ModelLevel(a.Source), a)).ToList(), answers);
    }

    /// <summary>A route fact (what one server takes for one model): 1. a Martlet test on that route, 2. the server's own
    /// metadata (<paramref name="serverSource"/>: OpenRouter's list for an OpenRouter route, NVIDIA's page for an NVIDIA Build
    /// route), 3. the model's own facts (<paramref name="model"/>), 4. models.dev's row for this provider only.</summary>
    public static CatalogFact ResolveRoute(string key, IReadOnlyList<CatalogAnswer> answers, string? serverSource, CatalogFact model)
    {
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(model);
        var all = answers.Where(a => a.Source != ModelSource).ToList();
        if (model.Value is { } value)
            all.Add(new() { Source = ModelSource, Value = value, Note = model.From is { } from ? "from " + CatalogSources.Describe(from) : null });
        int Level(CatalogAnswer a) => a.Source switch
        {
            CatalogSources.MartletTest => 1,
            ModelSource => 3,
            CatalogSources.ModelsDevRows => 4,
            _ when a.Source == serverSource => 2,
            _ => 5
        };
        return Resolve(key, all.Select(a => (Level(a), a)).ToList(), all);
    }

    private static CatalogFact Resolve(string key, IReadOnlyList<(int Level, CatalogAnswer Answer)> leveled, IReadOnlyList<CatalogAnswer> all)
    {
        var kind = CatalogFacts.KindOf(key);
        foreach (var level in leveled.Where(l => Answers(l.Answer.Value)).GroupBy(l => l.Level).OrderBy(g => g.Key))
        {
            var said = level.Select(l => l.Answer).ToList();
            if (!Agree(kind, said))
                return new() { Disagree = true, Answers = all };
            var chosen = kind == CatalogFacts.Kind.Date ? said.OrderByDescending(a => a.Value.Length).First() : said[0];
            return new() { Value = chosen.Value, From = chosen.Source, Answers = all };
        }
        return new() { Answers = all };
    }

    /// <summary>Whether a value is an answer (not empty and not "only some variants").</summary>
    public static bool Answers(string? value) => !string.IsNullOrWhiteSpace(value) && value != CatalogValues.Some;

    private static bool Agree(CatalogFacts.Kind kind, IReadOnlyList<CatalogAnswer> said) => kind switch
    {
        CatalogFacts.Kind.Number => said.Select(a => Number(a.Value)).ToList() is var numbers && numbers.All(n => n is not null) &&
            numbers.Max()!.Value is var most && numbers.All(n => most <= 0 || Math.Abs(most - n!.Value) / most <= 0.05),
        CatalogFacts.Kind.Date => said.Select(a => Month(a.Value)).Distinct().Count() == 1,
        CatalogFacts.Kind.Text => said.Select(a => Plain(a.Value)).Distinct().Count() == 1,
        _ => said.Select(a => a.Value).Distinct(StringComparer.Ordinal).Count() == 1
    };

    private static double? Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string Month(string value) => value.Length >= 7 ? value[..7] : value;

    private static string Plain(string value) =>
        new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()).Replace("license", "", StringComparison.Ordinal);
}
