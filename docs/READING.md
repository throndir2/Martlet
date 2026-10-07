# Reading the text on your screen

Martlet can read the text on your screen while it watches your screen with you.
This is OCR (optical character recognition). Martlet uses it together with the
vision model. The vision model sees the whole picture. OCR reads small text
exactly: a health value, a score, a kill feed, "VICTORY", a chat line.

Set it up in **Companion › Reading**. The choice stays on this PC
(`reading.json` in Martlet's data folder).

## Where Martlet reads

| Choice | What it uses | Where it runs | Data |
| --- | --- | --- | --- |
| **Windows OCR on this PC** (default, recommended) | Windows' own text recognition (`Windows.Media.Ocr`) in your Windows languages | This PC's processor, about 15 ms for a 1024 x 576 screenshot | Nothing leaves this PC |
| **Martlet's Reading role** | RapidOCR 1.4.4 with its PaddleOCR PP-OCRv4 models, in Docker | The processor of this PC or of another of your computers. No graphics card is needed | Screenshots go to that computer. It reads them in memory and does not keep them |
| **Off** | Nothing | | |

Windows OCR is fast and free. It needs a Windows language that has text
recognition. Most languages include it. If Windows has none, the page tells you
what to add in Windows Settings › Time & language › Language & region.

The Reading role is often better with game fonts. It takes about 0.5 to 1 s for
one screenshot on a desktop processor (measured: 0.9 s on a 24-thread processor).
Set it up from **Companion › Reading › Martlet's Reading role › Set up**. That
uses the same `martlet-host add ocr` flow as every other role. The role is
described in [Reading host role](OCR_HOST.md).

## What Martlet does with the text

1. Martlet captures the screen every 3 seconds while it watches
   ([Watch my screen](SCREEN_COMMENTARY.md)).
2. When the picture changed, or 9 seconds passed, Martlet reads the newest
   screenshot. It does one read at a time, off the UI thread and away from any
   reply. Nothing waits for a read.
3. Martlet compares the words with the last read. New text makes a look more
   likely, even when the picture barely changed. The same words add nothing.
4. When Martlet takes a look on its own, the newest text (at most 10 seconds
   old) goes at the end of that look's message, after the glance prompt. The
   prompt is **Text on screen** on Companion › Prompts. Empty it to send no text.
5. The conversation keeps only the look's short `[Screen]` line, never the text
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

**Companion › Reading › Read my screen now** takes one picture of the whole
screen and reads it with the saved choice. It shows the number of lines, the
engine, the time and the text. With the Reading role, the picture goes to that
computer. The talk window's vision tooltip also shows the newest read while you
watch.

## Checking it

The MCP tool `reading_check` reads `reading.json` and reads a drawn test picture
with known text through Windows OCR. With `endpoint` it also sends the same
picture to a Reading worker on loopback. See [MCP](MCP.md#reading).
