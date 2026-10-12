# Lorebooks

Martlet's lorebooks work like SillyTavern's World Info: each entry has
keywords, and when one comes up in the conversation the entry's text is added
to what Martlet knows for that reply. Always-on entries are added every time.
Open **Companion › Lorebook** (in *Who it is*, between Personality and Memory)
to see which lorebooks are on, turn one on or off, import one, or open the
editor.

## What happens on a reply

1. Martlet reads `lorebooks.json` from its data folder. If the file can't be
   used, the reply goes ahead without lore and the conversation status says why.
2. Only lorebooks that are on for the active persona are scanned: *with every
   persona*, or *only with* the personas checked in the editor.
3. The message being answered and the messages before it, up to the **scan
   depth** (2 by default: your message and Martlet's last reply), are searched
   for each entry's keywords. Screen and camera glances scan their prompt,
   including the window title and the program in front, so a game's lore can
   trigger while you play it.
4. An entry triggers when any keyword matches and its optional **filter
   keywords** pass their rule: *and any*, *and all*, *none of them* or *not all
   of them*. Always-on entries need no keyword. An entry with a **chance** below
   100% is then rolled once.
5. Triggered entries are kept by priority (always-on first, then higher
   **order**) until the **budget** is used (4,096 UTF-8 bytes by default,
   256-12,288). With **recursion** on, triggered entries' text is searched again
   (up to five rounds) so one entry can bring in another.
6. The kept entries are written into the notes on the message (see
   [Conversation › Prompt caching](CONVERSATION.md#prompt-caching-and-the-request-layout))
   as labeled blocks, the **before**-persona block first and then the
   **after**-persona block; within each block lower orders come first, so
   higher orders sit closer to the reply. An entry already in the notes of an
   earlier message the request still carries isn't sent again. `{{char}}`
   becomes the persona's name and `{{user}}` "the user".
7. Everything must fit the same request reservation as the persona, recalled
   memory facts and recent exchanges. Memory facts go first, then the oldest
   exchanges; lore entries are dropped (lowest priority first) only when nothing
   else is left.

Keywords are case-insensitive whole words by default. A keyword written
`/pattern/flags` is a regular expression (`i`, `m` and `s` are honored; an
invalid or slow pattern never matches). Entries can override case sensitivity,
whole-word matching, word forms and scan depth, be excluded from recursion
(*only the conversation can trigger it*) or stop recursion (*its content never
triggers other entries*).

**Match other word forms** (off by default, so keywords match exactly as in
SillyTavern) lets a keyword also match the other forms of its words: *dragon*
finds *dragons*, *red dragon* finds *Red Dragons* and *Drache* finds *Drachen*.
Martlet compares the words by their search stems (the same
[word rules](MEMORY.md#lexical-retrieval-and-reranking) as memory, with the
light rules of Spanish, French, German, Portuguese or Italian when the
conversation is in one of them), the words of a keyword in the same order. A
case-sensitive keyword keeps its capital letters (*Rose* finds *Roses*, never
*the sun rose*). A `/regex/` keyword, and a keyword in Chinese, Japanese,
Korean or Thai, matches exactly as before. Turn it on for all lorebooks in
the scan settings, or for one entry in its *More entry options* (*Other word
forms*: a filled box overrides the scan setting).

## Privacy and cost

Lorebooks are the same on all your computers: they travel as a
[shared setting](CLUSTER.md#one-martlet-on-every-computer) through your paired
hosts. Only triggered entries are sent to a model, inside the
already-authorized LLM request to the Thinking model you chose, so they count
toward that request's size (and any per-request cost). The conversation
window's disclosure says so before each action, and its status line lists how
many entries were used or left out and their titles (never their text).
Lorebooks are user content: they are not permissions and can't change routing,
consent, tools or safety rules.

## Editor

**Edit lorebooks** opens the editor: create, rename, delete and scope
lorebooks; add, duplicate, find and edit entries (title, keywords, filter
keywords and rule, content, enabled, always on, placement, order, chance and
the per-entry options); change the scan settings shared by all lorebooks; and
**Try it**: type a message to see which entries it would trigger and exactly
what would be added, without sending anything. There is no Save button: every
edit saves on its own (a moment after typing stops, at once for adding,
duplicating, deleting or importing), the footer says *All changes saved.* or
why the latest edit isn't saved yet (such as an invalid scan depth), and
closing saves anything still waiting. A save never overwrites a file that
changed elsewhere (the footer says to reopen the editor), and a file Martlet
can't read is never overwritten.

## SillyTavern compatibility

**Import** (or drop a file on the editor) reads:

- SillyTavern World Info JSON (`{"entries": {...}}`),
- the `character_book` inside a SillyTavern/Chub character card: PNG/APNG
  (`ccv3` or `chara` chunk), JSON or CHARX, V2 or V3 (`use_regex` keys become
  `/key/i`),
- standalone character books and NovelAI lorebooks.

An imported lorebook is on for every persona until you change it. Entries
without content, or without keywords unless always on, are skipped and
counted. **Export for SillyTavern** writes World Info JSON that SillyTavern
imports under World Info.

These carry over both ways: keywords, filter keywords and rule
(`selectiveLogic`), content, title (`comment`), always on (`constant`),
enabled (`disable`), order, placement (SillyTavern position 0 is *before the
persona*; every other position, including @depth, becomes *after*), chance
(`probability`/`useProbability`), scan depth, case sensitivity, whole words and
the two recursion options. An entry's own *Other word forms* choice is written
as `matchWordForms`, which SillyTavern doesn't use and Martlet reads back; an
entry without its own choice is exported exactly as before. SillyTavern's inclusion groups, sticky/cooldown/delay
timers, @depth roles, vector matching and automation IDs are not used.

**Importing a character card as a persona** (Companion › Personality) keeps the
card's always-on entries in the persona text, as before, and puts its
keyword-triggered entries in a lorebook named after the card, used only with
that persona. It is saved together with the persona; importing the same card
again for the same persona replaces that lorebook's entries.

## Limits

- At most 1,000 lorebooks, 100,000 entries each, 64 keywords per entry (256
  characters each) and 32,768 characters of content per entry. A lorebook can
  be on for up to 1,000 personas (as many as Martlet keeps).
- `lorebooks.json` and import files are at most 256 MB each, so a lorebook you
  can import can also be saved (character cards: 8 MB of card text). Martlet
  reads the whole file at once, so this limit keeps one damaged or very large
  file from using all the memory. A file that is too large is refused with a
  message, and the import message counts the entries it skipped (empty ones,
  and any past 100,000, which it names as the reason).
- Scan depth 0-1,000 messages. Only the messages the conversation has are
  searched, so a deep scan costs no more than the conversation is long. Each
  reply searches every entry that is on, so a very large lorebook makes each
  reply a little slower to start (about 0.15 s for 100,000 entries on a
  desktop PC).
