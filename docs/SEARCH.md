# Search engine

Martlet searches three kinds of knowledge with one search engine:

- remembered facts ([Memory](MEMORY.md#lexical-retrieval-and-reranking)),
- app guides ([App guides](APP_GUIDES.md#searching-a-guide-on-the-replys-path-with-no-added-wait)),
- past conversations ([Memory](MEMORY.md#conversation-history)).

Each kind keeps its own store and its own index. The engine is the shared
code that indexes, scores and picks. It is in `src\Martlet.Core\Search`. A
change to the engine (for example, other languages, extra search words or an
embedding search) is made one time and all three kinds get it.

Lorebooks do not use the engine: their keywords and regular expressions
([Lorebooks](LOREBOOKS.md)) must match exactly as the author wrote them.

A search runs in this process, in memory and without a model. It runs on the
reply's path, so it must stay fast (see
[Never add conversation latency](../AGENTS.md#never-add-conversation-latency)).
The retrieved text goes in the request's notes, after the cached start of the
request.

## The parts

| Part | Type | What it does |
|---|---|---|
| Words | `SearchTerms` (`src\Martlet.Core\Text`) | Makes the search terms of a text: lower-case words, grammar words left out, light English stems. |
| Profile | `SearchProfile` | One source's settings: fields and weights, BM25 k1, positions, candidates, feature weights, cutoff and diversity. |
| Index | `SearchIndex` | The inverted index and the first stage (BM25 or BM25F). |
| Query | `SearchQuery` | The query terms, each with its weight and its concept bits. |
| Ranker | `SearchRanker` | The second stage: scoring, cutoff, fusion and selection. |
| Rules | `ISearchRules` | What a source tells the ranker: its own features, the order of equal scores, similarity, groups and skips. |
| Measures | `SearchMeasures`, `SearchSketch` | Coverage, proximity, phrases, recency, time and similarity. |
| Fusion | `SearchFusion` | Reciprocal rank fusion of two ranked lists. |

## The index

A document has one or more **fields**. A field is a list of search terms.
Each field has a weight and a length normalization (b) for BM25F.

| Source | Fields | Positions |
|---|---|---|
| Memory | `content` (weight 1, b 0.75) | Yes |
| App guides | `title` (2.5, 0.3), `section` (3.5, 0.4), `text` (1.0, 0.6) | Yes |
| Past conversations | `said`: what the user and Martlet said (1, 0.75) | No |

With **positions**, the index keeps the terms of each field in order
(`FieldTerms`). Proximity, phrases and word overlap use them. Past
conversations keep no positions, because a record can hold 100,000 exchanges.
They read the words of the best candidates again when they need them.

A source can index extra text for a document in a second field. For example,
a memory fact can get hidden search words with a lower weight.

An index grows one document at a time (`Add`). An index that is built one
time and then shared is frozen (`Freeze`). A frozen index can be searched on
many threads at the same time. An index with more than one field must be
frozen before a search: freezing calculates the BM25F weight of each posting.
The past-conversation index is not frozen: exchanges are added to it while
Martlet runs, and its searches and additions take turns.

## The first stage

`SearchIndex.Search` scores every document that holds a query term:

- **BM25** with one field (Robertson and Zaragoza, 2009).
- **BM25F** with more fields: each field's term frequency is weighted and
  length-normalized, then the sum is saturated one time (Robertson, Zaragoza
  and Taylor, 2004).

Each query term counts its IDF times its weight. A **concept** is one word of
the query. A concept can have more than one term (for example, *colour* and
*color*). Each document records the concepts it matched, as bits.

A filter can leave documents out (an expired fact, another conversation, a
time outside the window). The best documents go to the second stage: 64 for
memory, 40 for guides and 200 for past conversations. The order is the higher
score first, then the source's tie rule, then the document matched first. A
heap keeps the best, so the cost follows what the query matches, not the size
of the index. A search reuses its working arrays and clears only the
documents that it touched.

## The second stage

`SearchRanker.Rank` scores each candidate again:

1. The engine sets the first-stage score and its share of the best
   candidate's score.
2. The source's rules add their features (`ISearchRules.Score`). A source can
   leave a candidate out here.
3. The profile combines the features (`SearchScoring`):
   - **Add**: the sum of each feature times its weight.
   - **Multiply**: the first-stage score times (1 + weight × feature) for each
     feature.
4. Costly features can be read for the best few only (`Refined`, then
   `ISearchRules.Refine`).
5. A hit below a share of the best score is left out (`Cutoff`).
6. Optional: a second ranked list is fused in (see [Fusion](#fusion)).
7. The hits are picked (see [Selection](#selection)).

The features, each from 0 to 1:

| Feature | Measure | Memory | Guides | Past conversations |
|---|---|---|---|---|
| First stage | share of the best score | (the base) | 0.2 | 1.0 |
| Relevance | the guide's calibrated relevance | | 0.3 | |
| Coverage | IDF-weighted share of the query that matched | ×(1 + 0.5) | (in relevance) | 2.0 (nearby turns count half) |
| Proximity | `ClosestPair`, `MinimumCover` or `Stretch` | ×(1 + 0.25) | 0.1 | 0.5 (best 48) |
| Phrases | query words next to each other, in order | ×(1 + 0.25) | 0.05 | 0.5 (best 48) |
| Recency | halves every *n* days | ×(1 + 0.1), 30 days | | 0.2, about 21 days |
| Time | inside or near the time asked about | | | 1.0 |
| Extra | the source's own features | | headings 0.05, soft words 0.05 | |

Memory multiplies; guides and past conversations add. The three proximity
measures are different because each source was tuned on its own
evaluation:

- `ClosestPair` (memory): 1 divided by the smallest distance between two
  different query terms (Tao and Zhai, 2007).
- `MinimumCover` (guides): the smallest window that holds all matched
  concepts (Büttcher, Clarke and Lushman, 2006).
- `Stretch` (past conversations): the shortest stretch that holds the
  matched terms, scaled by how many of the asked terms it has.

## Selection

The profile's `SearchDiversity` picks the hits:

- **Penalty** (memory): take the best, then the best of the rest. A
  near-duplicate of a hit already taken keeps 0.25 of its score. Scores only
  go down, so a shorter list is always the start of a longer one.
- **MMR**, maximal marginal relevance (guides and past conversations;
  Carbonell and Goldstein, 1998): the next hit is the best by score ÷ best
  score − weight × its greatest similarity to a hit already taken. A
  near-duplicate (similarity 0.8 or more) of a hit already taken is left out.
  - Guides: weight 0.25 (λ 0.8), chunks of one page count 0.35 similar, a
    page gives at most two hits before every other page has had its turn,
    and hits come from the best 4 + 4 × the hits wanted.
  - Past conversations: weight 0.3 and hits come from the best 30. An
    exchange already in the request's notes is not taken, but its
    near-duplicates are left out too (`ISearchRules.Skip`).

Similarity comes from the source: word overlap of the text (guides), of the
said words (past conversations), or a MinHash sketch of the fact's terms
(memory, `SearchSketch`; exact for facts of up to 32 different terms).

Equal scores and equal values go by the source's tie rule, then by the
candidates' order, so a search is deterministic:

- Memory: more matched terms, then the newer fact, then the fact ID.
- Guides: the earlier chunk.
- Past conversations: the newer exchange.

## Fusion

`SearchFusion` merges ranked lists with reciprocal rank fusion (Cormack,
Clarke and Büttcher, 2009): each document scores Σ 1 / (60 + its rank) over
the lists that have it. It needs no calibration between the lists, so a
lexical search and an embedding search can fuse without tuning.

`SearchRanker.Rank` takes an optional second ranked list (`fuse`). It fuses
that list with the reranked order after the cutoff and before selection. A
document that only the second list has is scored as a candidate that matched
nothing. No source gives a second list yet.

## Speed

On the developer PC, median of 200 queries after a warm-up round, Release
build, four alternating runs of `main` and the engine (shared with other work,
so the numbers vary):

| Search | Before the engine | With the engine |
|---|---|---|
| Memory, 512 facts | 18-30 µs | 19-20 µs |
| Memory, 100,000 facts | 1.5-2.6 ms | 0.30-0.34 ms |
| App guide, 5,000 chunks (average) | 176-267 µs | 148-205 µs |

The hot methods are compiled fully optimized from the first call
(`AggressiveOptimization`), because a user sends too few messages for the
runtime's tiered compilation to optimize them later. The engine avoids
generic collections of its own structs on the hot path for the same reason.

## Tests and evaluations

- `SearchEngineTests` (`tests\Martlet.Core.Tests`): the index, BM25 and BM25F,
  the cut, the second field, an index without positions, the combinations,
  the cutoff, both selections, fusion and the measures.
- Each source's evaluation checks how well it ranks:
  `RetrievalTests` and `RerankingTests` (memory),
  `AppGuideRetrievalTests` (guides) and `PastConversationsEvaluationTests`
  (past conversations).
- The MCP checks `memory_recall_check`, `app_guides_check` and
  `conversation_history_check` ([MCP](MCP.md)) run the production code on
  synthetic data and report the search times.

When the engine replaced the three separate rankers, it gave the same
rankings as before on the memory, guide and past-conversation fixtures and on
about 2,000 random queries. The only difference: when more chunks than the
first stage keeps have exactly the same score, the engine keeps the ones
matched first; before, a heap kept an arbitrary set of them.
