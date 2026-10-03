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
   including the window title, so a game's lore can trigger while you play it.
4. An entry triggers when any keyword matches and its optional **filter
   keywords** pass their rule: *and any*, *and all*, *none of them* or *not all
   of them*. Always-on entries need no keyword. An entry with a **chance** below
   100% is then rolled once.
5. Triggered entries are kept by priority (always-on first, then higher
   **order**) until the **budget** is used (4,096 UTF-8 bytes by default,
   256-12,288). With **recursion** on, triggered entries' text is searched again
   (up to five rounds) so one entry can bring in another.
6. The kept entries are written into the LLM instructions as labeled blocks
   **before** or **after** the persona; within each block lower orders come
   first, so higher orders sit closer to the reply. `{{char}}` becomes the
   persona's name and `{{user}}` "the user".
7. Everything must fit the same request reservation as the persona, recalled
   memory facts and recent exchanges. Memory facts go first, then the oldest
   exchanges; lore entries are dropped (lowest priority first) only when nothing
   else is left.

Keywords are case-insensitive whole words by default. A keyword written
`/pattern/flags` is a regular expression (`i`, `m` and `s` are honored; an
invalid or slow pattern never matches). Entries can override case sensitivity,
whole-word matching and scan depth, be excluded from recursion (*only the
conversation can trigger it*) or stop recursion (*its content never triggers
other entries*).

## Privacy and cost

Lorebooks stay on this PC. Only triggered entries are sent, inside the
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
the two recursion options. SillyTavern's inclusion groups, sticky/cooldown/delay
timers, @depth roles, vector matching and automation IDs are not used.

**Importing a character card as a persona** (Companion › Personality) keeps the
card's always-on entries in the persona text, as before, and puts its
keyword-triggered entries in a lorebook named after the card, used only with
that persona. It is saved together with the persona; importing the same card
again for the same persona replaces that lorebook's entries.

## Limits

- At most 64 lorebooks, 2,000 entries each, 64 keywords per entry (256
  characters each) and 32,768 characters of content per entry; `lorebooks.json`
  is at most 16 MB. Import files are at most 32 MB (character cards 8 MB of card
  text).
- Scan depth 0-17 messages. The conversation keeps only recent exchanges (up to
  eight from the last two minutes), so deeper scans find nothing more.
