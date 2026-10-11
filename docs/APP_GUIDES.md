# App guides: game and app knowledge from the web

Design for **app guides**: Martlet reads up on a game or app the user runs (its
fan wiki, its help pages) and keeps what it read on this PC as a searchable
guide. When the user then asks *"hey Martlet, where do I find iron ore?"* or
*"how do I mask a layer in Photoshop?"*, Martlet answers from that guide.

This came from a community request: a gaming assistant that answers "where do
I find...?" and "what is this?", that scrapes game wikis into a local
knowledge base, that notices when you start a game and asks *"I see you
started Elden Ring. Want me to read up on it?"*, that lets you add your own
apps and wiki pages, and that ranks what it finds well (a reranker).

## What the user sees

1. Turn on **Companion › App guides** (off by default, because reading up
   sends the app's name to a search engine and the wiki sites see the
   requests).
2. Start a game. When Martlet sees a game (or an app on the list) in front and
   it has no guide yet, it asks once, in character: *"I see you started Elden
   Ring. Want me to read up on it so I can help?"* A yes starts a background
   job, *Reading up on Elden Ring*; a no is remembered and Martlet doesn't ask
   about that app again.
3. Or say *"read up on Stardew Valley"*, or add an app on the App guides page
   with its name, optional program names and optional wiki addresses.
4. While the app is in front (or the user names it), each question the user
   asks is searched in its guide. The best matching sections go with the
   message as reference notes, and the reply uses them.
5. The App guides page lists every guide (pages, size, when it was read, the
   sites), with **Read again**, **Delete** and **Add an app**.

## How it works

```mermaid
flowchart LR
    A[Game or listed app in front] -->|no guide yet| B[Martlet offers to read up]
    B -->|yes| C[Background job: find the wiki]
    U[User: read up on X / Add an app] --> C
    C --> D[Crawl the wiki: MediaWiki API or same-site links, robots.txt, caps]
    D --> E[Chunk pages by heading]
    E --> F[Guide on this PC: guides\\key.guide.json]
    F --> G[Index in memory: BM25F + reranker]
    Q[User asks a question while the app is in front] --> G
    G -->|sure matches only| N[Notes on the user's message]
    G --> T[search_guide tool for a deeper look]
```

### Finding and reading the wiki (no model needed)

Reading up needs no Thinking or Deep thinking model, so it never competes with
the conversation for a graphics card.

- **Start pages**: the addresses the owner gave. Otherwise Martlet tries the
  usual wiki hosts by name (`<name>.fandom.com`, `<name>.wiki.gg`) and then a
  web search for *"<name> wiki"*, and prefers wiki hosts (Fandom, wiki.gg,
  Fextralife, official wikis) over shops and video sites.
- **MediaWiki sites** (Fandom, wiki.gg and most game wikis) are read through
  their `api.php`: the most linked and largest content pages first, as clean
  HTML without menus. Other sites are crawled breadth first through links on
  the same site and under the same path, with navigation, edit, talk, user,
  file and special pages left out.
- **Politeness and safety**: `robots.txt` is obeyed, one request at a time per
  site with a delay between them, an honest user agent, and the same safe web
  client as web research (`WebAccess`: public internet addresses only, every
  redirect checked, size and time caps).
- **Caps**: 60 pages, 12 MB downloaded, 2 sites and 15 minutes a guide by
  default (`GuideBuildLimits`).
- **Text**: each page keeps its headings as Markdown lines, so sections stay
  together when the page is cut into chunks of at most 1,200 characters.

### Searching a guide (on the reply's path, with no added wait)

[Never add conversation latency](../AGENTS.md#never-add-conversation-latency)
decides the design: the search runs in this process, in well under a
millisecond, and no model is asked.

- **Words**: `SearchTerms` (Martlet.Core) makes the words: lower case, common
  grammar words left out, light English stems (*swords* finds *sword*,
  *mining* finds *mined*).
- **First stage, BM25F**: BM25 with the page title and section heading
  weighted above the body, so a question that names a page or section finds
  it.
- **Second stage, the reranker**: the best candidates are scored again by how
  many of the question's words they have, how close together they are, exact
  phrase matches and title matches, then near-duplicate chunks of one page are
  left out (maximal marginal relevance), so the notes cover different sources.
- **Relevance**: each hit has a relevance from 0 to 1. Only hits at or above
  the threshold go with the message (at most 3 chunks and 2,400 characters),
  so a message that isn't about the app gets nothing and its request is what
  it always was.

**Why no embeddings or vector database (yet).** A guide is a few thousand
chunks, which an in-memory index searches faster than any database call.
Dense embeddings would need an embedding model call on every message, which
adds a wait before the first word, and on a gaming PC it can push the
conversation's model out of the graphics card. Game questions are full of
proper names (items, places, bosses), where lexical search is strong. A later
step can add embeddings made while reading up and used only by the
`search_guide` tool, where a short wait is expected.

### What goes to the model, and what stays in the guide

The question this answers is *what goes straight to the model and what stays
in the database*: everything stays in the guide on disk, and only the few
sections that match the current question go with the message, as notes after
the conversation's cached start. Instructions and earlier messages don't
change, so prompt caches still hold. The notes say the text is reference
material read from the web, which may be wrong, and never instructions.

### Tools

- `read_up_on(app, sites?)`: starts the reading-up job (the user asked, or said
  yes to the offer).
- `search_guide(app, question)`: searches a guide for more than the notes
  carried.

## Files

| Part | Where |
| --- | --- |
| Shared types and interfaces | `src\Martlet.Conversation\Guides\GuideContracts.cs` |
| Search words | `src\Martlet.Core\Text\SearchTerms.cs` |
| Chunker, index and reranker | `src\Martlet.Conversation\Guides\GuideChunker.cs`, `GuideIndex.cs` |
| Store (`guides\library.json`, `<key>.guide.json`) | `src\Martlet.Conversation\Guides\FileAppGuideStore.cs` |
| Wiki reader | `src\Martlet.Conversation\Guides\WebGuideBuilder.cs` |
| Notes on the message | `src\Martlet.Conversation\Guides\GuideRecall.cs` |
| Detection, offer, tools, job, page and MCP | `src\Martlet.Desktop`, `src\Martlet.Mcp.Protocol` |

## Privacy

- Off by default; turning it on says what leaves the PC (the app's name in a
  search, and requests to the wiki sites).
- Detection reads only the program's name and whether it fills the screen
  (`ActiveApp`, `PcActivity`), never window titles or the screen.
- Guides hold public web pages only, never the conversation. The desktop log
  notes counts (pages, chunks, bytes, time), never questions, links or text.

## Delivery

The contract (this document, the types and simple first versions) landed
first. These workstreams then ran in parallel, one pull request each:

1. **Retrieval and store**: BM25F, the reranker, relevance, the chunker and a
   bounded, atomic store.
2. **Wiki reader**: wiki discovery, `robots.txt`, the MediaWiki API, the
   same-site crawl and heading-preserving text.
3. **Desktop**: detection, the offer, the tools, the job, notes on the
   message, the App guides page and MCP.
4. **Memory recall**: the same search words, BM25 and a reranker for
   remembered facts ([Memory](MEMORY.md)).
5. **Past conversations**: the same for earlier conversations.
