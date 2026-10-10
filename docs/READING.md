# Reading the text on your screen

Martlet can read the text on your screen while it watches your screen with you.
This is OCR (optical character recognition). Martlet uses it together with the
vision model. The vision model sees the whole picture. OCR reads small text
exactly: a health value, a score, a kill feed, "VICTORY", a chat line.

Set it up in **Companion › Reading**, an optional extra. The page lists the
places that read your screen, in order: this PC's [Reading list](#the-reading-pool).
The list stays on this PC (`pools-local.json` in Martlet's data folder).

The page has the standard order: **Now** (what Martlet reads with, the newest
read and **Read my screen now**), then *Where it reads* (the list), then
**Compare them**. Compare them shows Windows OCR and the Reading role side by
side: where it runs, the download, the read time, the processor threads, the
languages, how it reads game fonts, the cost and where screenshots go.

## Where Martlet reads

Each member of the list reads in one of these ways:

| Member | What it uses | Where it runs | Data |
| --- | --- | --- | --- |
| **This PC** with Windows OCR (default, recommended) | Windows' own text recognition (`Windows.Media.Ocr`) in your Windows languages | This PC's processor, about 140 ms for a full-size 1920 x 1080 screenshot | Nothing leaves this PC |
| **This PC** with Martlet's Reading role, or **another of your computers** (or one of its graphics cards) | PaddleOCR's PP-OCRv5 models with RapidOCR 3.10.0, or RapidOCR 1.4.4 with its PP-OCRv4 models, in Docker | That computer's processor, or an NVIDIA graphics card for PP-OCRv5 server | Screenshots go to that computer. It reads them in memory and does not keep them |

With nothing in the list, or nothing on, Martlet doesn't read your screen
(off). There is no separate on/off switch.

Windows OCR is fast and free. It needs a Windows language that has text
recognition. Most languages include it. If Windows has none, This PC's
settings tell you what to add in Windows Settings › Time & language ›
Language & region, and the next member of the list reads instead.

The Reading role is often better with game fonts. It has three models:

| Model | Where it runs | Download | Read time (24-thread processor) | Accuracy on a drawn 4K desktop |
| --- | --- | --- | --- | --- |
| **PP-OCRv5 mobile** (recommended) | The processor | About 700 MB | About 2 s for 1920 x 1080, 4 s for 4K | All 192 lines |
| **PP-OCRv5 server** | An NVIDIA graphics card (driver 580 or newer) | About 3.1 GB | Not measured on a card; about a minute on a processor | The most accurate |
| **RapidOCR** (PP-OCRv4) | The processor | About 200 MB | About 1 to 3 s | 112 of 192 lines |

Windows OCR read 145 of the same 192 lines. PP-OCRv5 reads Chinese, English
and Japanese. RapidOCR reads Chinese and English.
The server model is for an NVIDIA graphics card only. On a processor, it is too
slow for the gateway's 15 s limit, so the page disables it on a computer without
one.

To set up the Reading role on a computer:

1. Add the computer to the list (**Add to the list**), or use **This PC**.
2. Press the member's **Settings**. For This PC, choose **Martlet's Reading role**.
3. Choose the model, then press **Set up**. When the role already runs another
   model there, **Switch to** changes it.

That uses the same `martlet-host add ocr` flow as every other role. The member
then keeps the model in its settings. The role is described in
[Reading host role](OCR_HOST.md).

## The Reading pool

Companion › Reading's list is the Reading pool (`PoolAreas.Reading`, see
[Pools](CLUSTER.md#pools-one-ordered-list-of-members-per-area)). Each read goes
through Martlet's work queue (`WorkQueue`, lane `reading`, see
[Sharing work between your computers](CLUSTER.md#sharing-work-between-your-computers))
at background priority:

1. The read goes to the first member that is on. While it is free, nothing
   changes: no extra request.
2. When it is busy (it reads for another computer), does not answer, does not
   run the Reading role, or is Windows OCR without an OCR language, the read
   goes at once to the next member that is on.
3. When every one is busy, the read waits up to 3 seconds and goes to the
   first that frees. After that it gives up quietly, and the next screenshot
   tries again.
4. A computer that is in the list twice (as a computer and as one of its
   cards, or as This PC with the Reading role and as this PC's host service)
   is tried once. A card member uses its computer's Reading role: the computer
   picks the card.
5. A member that is off, kept for another companion PC or not paired with this
   PC is left out.
6. A host that a friend shares with you reads only when it is in your list
   (Devices › *Use for reading* puts it first). When its owner needs it, the
   read goes to the next member, or waits for a later screenshot.

Each member keeps its own settings: This PC's `engine` (`windows-ocr` or `ocr`
for the Reading role on its own host service) and a computer's `model` (the
model set up there). A member that is off keeps its place and settings.

The list is this PC's own and is never shared, as the screen it reads is this
PC's. The first time the page opens, Martlet makes the list from the older
choice (`reading.json`): Windows OCR becomes This PC; the Reading role on a
computer becomes that computer, then your other computers that the shared plan
says run the role; off becomes an empty list. Until then, reads use that same
list without saving it.

Martlet keeps the connection to each computer open between reads, and drops it
when that computer fails. The JPEG of a screenshot is made only when a computer
reads it. The desktop log (`Sharing work: Reading went to m3-host (1 busy).`)
and the Devices card's status line say when a read went past the first member.
**Qualification:** the list, its order and the queue are checked locally
(`ReadingPoolTests` in Martlet.Core.Tests and Martlet.Desktop.Tests, MCP
`reading_check` `pool` and `poolCheck`, and Companion › Reading through
`-Desktop`); reads between real hosts are **NOT RUN**.

## What Martlet does with the text

1. Martlet captures the screen every 3 seconds while it watches
   ([Watch my screen](SCREEN_COMMENTARY.md)).
2. When the picture changed, or 9 seconds passed, Martlet reads the newest
   screenshot. It does one read at a time, off the UI thread and away from any
   reply. Nothing waits for a read.
3. Martlet reads the screen at full size (at most 8192 pixels on the long
   edge). The vision model's look is downscaled to at most 1024 pixels for each
   monitor, and that makes normal 12-pixel text about 6 pixels high. OCR cannot
   read text that small. So for each read, Martlet copies the same screen again
   at full size, reads that copy and then clears it. In a test on a drawn
   1920 x 1080 desktop with 108 lines of 12-pixel text, Windows OCR read 102
   lines at full size in 138 ms, and no lines at 1024 x 576.
4. Martlet compares the words with the last read. New text makes a look more
   likely, even when the picture barely changed. The same words add nothing.
5. When Martlet takes a look on its own, the newest text (at most 10 seconds
   old) goes at the end of that look's message, after the glance prompt. The
   prompt is **Text on screen** on Companion › Prompts. Empty it to send no text.
   At most 60 lines and 1,500 characters go, in reading order.
6. The conversation keeps only the look's short `[Screen]` line, never the text
   that was read.

Rules that keep conversations fast:

- What you type or say never carries the read text. Your replies do not get
  longer requests or wait for a read.
- The text goes only at the end of a look's request. The start of the request
  stays the same, so prompt caches still work.
- Lorebook keywords are matched against the glance prompt only, not the read
  text, so a look does not pull in different lore each time.

Only the screen is read, never a camera. Martlet's own windows are painted over
before a read, as for every look, and a private window in front stops the read.

## Read my screen now

**Companion › Reading › Read my screen now** takes one full-size picture of the
whole screen and reads it with the list. It shows the number of lines,
the engine (with the Reading role's model), the time, the size of the
screenshot and the text. With the Reading
role, the picture goes to that computer as a JPEG. A large screenshot is sent at
a lower JPEG quality when it would be more than the role's 4 MiB limit. The
talk window's vision tooltip also shows the newest read while you watch.

## Checking it

The MCP tool `reading_check` reads this PC's Reading list (`pool`) and reads a drawn test picture
with known text through Windows OCR. It also reads a drawn 1920 x 1080 desktop
of small text at full size and at 1024 x 576, to show why Martlet reads at full
size, and a drawn 4K desktop. With `endpoint` it also sends the same pictures to
a Reading worker on loopback. See [MCP](MCP.md#reading).
