# App Guides

Martlet reads up on the games and apps you use: their fan wikis and help pages. Then she answers your questions from what she read. Ask *"where do I find iron ore?"* while the game is in front, or *"how do I mask a layer?"* in your photo editor.

![Companion App guides](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-app-guides.png)

## Turn it on or off

Open **Companion › App guides**. Two choices control it:

- **Read up on games and apps** turns App guides on or off. It is on by default.
- **Read up when I start a game or app** lets Martlet read up on her own. It is on by default too.

If you turned App guides off in an earlier version of Martlet, they stay off. Tick the box to turn them on.

If Martlet can't read your guide library (for example a damaged file), it doesn't change it. Turning App guides on then starts a new library and keeps a copy of the old file. When a newer Martlet wrote the file, Martlet leaves it alone; update Martlet on this PC.

## Reading up when you start a game

When you start a game (or an app on your list) that she has no guide for, Martlet reads up on it on her own, once a session:

1. She sends the game's name to DuckDuckGo to find its wiki.
2. She reads up to 200 of the wiki's pages, the pages about that game first. On a big wiki this takes about 5 minutes and 10 MB. She skips pages about other games in the same series.
3. She tells you in character, for example *"I see you started Elden Ring. I'm reading up on it so I can help."* She tells you again when she is done.
4. When a [Thinking pool](Thinking#thinking-pool) member can take research, she then reads up to 300 more pages. The member chooses them from the wiki's links, with what you do now in mind.

You can ask about the game while she still reads. The guide grows as she reads.

Martlet knows a game by its program. The program is in a game library folder, has a game engine's files (such as Unity or Unreal) or runs in exclusive full screen. This works for a game in a window too.

## Stop her

- Tell her no. She stops and doesn't read up on that app on her own again. **Allow again** on the page undoes this.
- Press **Stop reading** on the page. She stops and keeps what she already read. **Cancel** in the talk window's task list does the same.
- Untick **Read up when I start a game or app** to stop all reading up on her own.
- Untick **Read up on games and apps** to turn App guides off.

## Ask her to read up, or add an app

Say *"read up on Stardew Valley"*. Or add an app yourself:

1. Open **Companion › App guides › Add an app**.
2. Type its **Name**.
3. Optional: type its **Program names** as Windows shows them, separated by commas, when they differ from the name.
4. Optional: type the **Wiki pages** to read. Without them, she finds the wiki herself.
5. Press **Add and read up** to read at once, or **Add** to only add it.

## Your apps

**Your apps** lists each app with its pages, size, when it was read, the sites it reads from and its last problem. When a guide can't be used (for example a damaged file), press **Read again** to make a new one. Each app has:

- **Read up now**, or **Read again** for an app with a guide. Read again also makes a very large guide from an older Martlet smaller.
- **Read more** reads up to 300 more pages of the same wiki each time. A Thinking pool helper chooses them when one can; otherwise Martlet takes the pages that the guide links to most. You can also press **Read more** in the talk window or on the speech bubble.
- **Delete** removes the app and its guide.
- **Allow again**, after you told her no.

The page also says which app is in front now, as App guides sees it.

## How she uses a guide

While an app is in front, or when you name it, the parts of its guide that best match your question go with your message. A message that isn't about the app gets nothing. With a Thinking model that uses tools, she can also search the guide more deeply. That search also finds parts that say the same thing in other words, with **Finding by meaning** on **Companion › Memory**.

- **The game brief**: after each read, a Thinking pool member writes a short brief about the game: what it is, how it plays, early tips and common mistakes. She keeps it in mind when you talk about the game. Ask her to refresh it any time.
- **Where you are**: with screen summaries on ([Vision](Vision)), she notices where you are in the game. When her guide has little about that place, she looks it up in the background on wikis, Reddit search results and the web. She does this for at most 4 places an hour.

No reply waits for reading, the brief or these look-ups.

## What is sent where

- The game's name goes to DuckDuckGo. The wiki sites see the requests and this PC's internet address.
- Martlet obeys each site's rules for programs (`robots.txt`), reads one page at a time and pauses between pages.
- To choose more pages, a Thinking pool member gets the game's name and the titles of the linked pages. It also gets short notes on what you do now: your screen, sounds and recent words. These notes are not kept in the guide.
- To notice a game, Martlet looks only at the program's name and whether it fills the screen. It never reads window titles or the screen for this.
- On a paid cloud pool member, the brief and the look-ups can cost money. On your own computers they cost nothing.

## Where guides are kept

Guides stay on this PC, in the `guides` folder of Martlet's data folder. They hold public web pages only, never your conversation. Martlet keeps only the guides it can use now in memory, so a big guide doesn't take memory your game needs. The log notes counts and times only, never questions, links or text.

More detail: [App guides](https://github.com/throndir2/Martlet/blob/main/docs/APP_GUIDES.md), [Where you are in a game](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md#where-you-are-in-a-game), [Finding by meaning](https://github.com/throndir2/Martlet/blob/main/docs/SEARCH.md#finding-by-meaning).
