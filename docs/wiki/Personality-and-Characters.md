# Personality and Characters

Make Martlet your own: write her personality, import character cards and lorebooks, edit her prompts, and switch her look, voice and personality in one click.

![Companion Personality](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-personality.png)

## Personas

A persona says who Martlet is and how she talks. Open **Companion › Personality** and press **Edit personality** to create, edit or switch personas. Write the tone you want (helpful, sarcastic, silly or teasing) in your own words, as a character card does. Changes save on their own, and the next message uses the chosen persona. The talk window calls your character by the persona's name.

The persona text is the character's part of the system prompt (the instructions the model reads first). Martlet adds its own instructions after it (reply length, tools, what it hears) and tells the model to treat your headings and rules as the character's own. You don't need a separate system prompt: see or edit Martlet's part in **Companion › Prompts › Persona**. Martlet greets you in character when it starts, and **Check-ins › Staying in character** reminds it when it drifts.

You can keep up to 1,000 personas, and one persona can be up to 100,000 characters long, so big character cards fit. **Delete** asks first, because you can't undo it.

### Where the voice pauses

Martlet speaks a reply a piece at a time while it's written, so the voice starts sooner. Each piece is said on its own, so a break in the wrong place can sound awkward. Under **Where the voice pauses**, choose which sentence ends this persona's voice may break at: periods, question marks and exclamation marks. Commas, semicolons and dashes never break it.

**Say a short ending with the words before it** keeps a short ending in one go. For example, with **1 word**, "That was a wonderful idea. Cutie!" is said in one go instead of pausing before "Cutie!". A piece that has grown long still breaks at the next sentence end, so the voice can say it.

## Character profiles

A profile switches look, voice and personality together. Open **Companion › Profiles** and press **New profile...** to save what Martlet uses now as a profile. A new profile starts as what Martlet uses now: change any part, then save. Switch from **Companion › Profiles**, Home's **Character** box or the **Character profile** menu of Martlet's icon in the notification area. A part that can't switch yet stays as it was, and Martlet says why. For example, the look stays while the **Character settings** window is open: close it and use the profile again.

**Remove** on a profile removes only the profile, on all your computers. Its look, voice and personality stay.

On each computer, a profile also keeps where the character stands, how big she is, where she looks and which touches stop her while she talks. Switch back to a profile and they come back, also after a switch on another computer. Emotes, touch zones and face measurements belong to each look, and the touch temperament belongs to each persona.

To make a whole character from a name, use **Find a character...** on **Companion › Character** (see [The Character](The-Character)).

## Sharing a character with your household

When several people use Martlet, each person has their own characters. On **Companion › Profiles**, each of your profiles has a sharing choice:

- **Private** (the default): only you see and use it.
- **Share a copy with the household**: other people see it under **Household characters** and can choose **Use a copy**. They get its personality, look, voice, emotes and lorebooks as their own character. The copy has its own memories, and changes to it are theirs alone.
- **Share together with the household**: other people choose **Talk to it**, and the character joins their profiles. Everyone talks to the same character. It remembers everyone it talks to in its own memories, and it knows whose each fact is. When you change its personality, look, voice or lorebooks, everyone gets your change. Only you can edit it. Others can choose **Leave** to take it out of their profiles.

When you make a character private again, copies that people already made stay theirs. A character shared together leaves the other people's profiles. See [Accounts and Household](Accounts-and-Household).

## Character cards

On **Companion › Personality**, press **Import a character card**. In the **Edit personality** window, **New from card...** adds a persona and **Update from card...** updates the one you edit; you can also drop a card on that window. Martlet reads cards from SillyTavern, Chub (CharacterHub), RisuAI and other Tavern-compatible apps, as PNG, JSON or CHARX files. The card becomes a persona. Its always-on entries go into the persona text, and its keyword entries become a lorebook used only with that persona.

A PNG card carries the character's details inside the image file. A resaved, converted or screenshotted copy loses them, so use the original PNG, or the card's JSON or CHARX export. When a card's data is damaged, download it again or use its JSON or CHARX export. When there isn't enough room for a card, shorten or delete another persona first.

## Lorebooks

Lorebooks work like SillyTavern World Info. Each entry has keywords. When a keyword comes up in the recent conversation, the entry's text goes to the Thinking model as a note. Always-on entries go every time.

Open **Companion › Lorebook** to turn lorebooks on or off, import one or press **Edit lorebooks**. In the editor you create, import, scope and edit lorebooks and entries. **Try it** shows which entries a message would trigger, without sending anything. Edits save on their own.

- Import SillyTavern World Info JSON, a character card's `character_book`, standalone character books and NovelAI lorebooks. **Export for SillyTavern** writes World Info JSON.
- **Match other word forms** (off by default) lets *dragon* also find *dragons*.
- Each person has their own lorebooks, the same on all their computers.

### Lorebook settings

- **Scan depth (messages)**: how many of the latest messages Martlet searches for keywords, counting the one it answers.
- **Budget per reply**: the most entry text that goes into one reply. Entries with a higher **Order** go first. An entry that doesn't fit is skipped, and **Try it** lists it.
- **Match other word forms**: keywords also match other forms of their words: *dragon* finds *dragons*, *Drache* finds *Drachen*. Keywords written as regular expressions don't change.
- **More entry options** on an entry overrides these settings for that entry only. A box left unfilled uses the setting for all lorebooks.

### Try it

Type a sample message, choose a persona and press **Show matches**. Martlet shows which entries would be added, and why. Nothing is sent to a model. Entries with a chance below 100% show as if they were added.

### When a lorebook can't be saved

The bottom of the Lorebooks window tells you if every change is saved. When a change can't be saved, Martlet tries again with your next change.

- **Your lorebooks were changed somewhere else**: another window or computer saved them meanwhile. Close **Lorebooks**, open it again and make your change again.
- **Couldn't be written** or **could not be saved**: check that Martlet can write to its data folder and that the drive has free space.
- **Too large to save**: the lorebooks file can't be bigger than its limit. Remove some entries.
- **Fix or remove lorebooks.json**: the file is damaged or not valid lorebook JSON. Martlet doesn't use or overwrite it. Fix the file or move it away, then open **Lorebooks** again.
- You can keep a limited number of lorebooks. To add or import one more, delete one first.

## Prompts

**Companion › Prompts** shows the requests Martlet sends to the Thinking model and the notes around what it knows: the persona wrapper, lorebook, memory, screen looks, check-ins and more. The rules a reply follows (reply length, tools, always listening and the others) are [skills](#skills). Edit any prompt, and your text is used instead of the built-in one.

The top card, **What one reply sends**, says what every reply to you carries (the persona prompt, the pinned skills and One moment, with their size in tokens) and which skills your last reply also carried because their feature is on. Under each group's title, a line says when Martlet sends it: only the persona prompt goes with every reply. Each prompt shows an estimate of its size in tokens. **Use built-in text** puts one prompt back, and **Use all built-in prompts** puts all of them back. Some prompts need a fixed line format, because Martlet reads the answer.

Press the **?** after a prompt's name to see when Martlet sends it and which words in braces it fills in.

- **How a request is built**: Martlet builds each request from these prompts plus your persona, the lorebook entries that match, remembered facts and the recent conversation.
- **Words in braces**: Martlet fills in words such as `{name}` or `{silent}` when it sends the prompt. Keep them where you want that text. `{silent}` is the word the model answers to stay quiet.
- **Empty prompts**: empty a prompt to send nothing for it. The page says **Empty: nothing is sent for this prompt**.
- **Fixed line formats**: Martlet reads the answers to some prompts, such as **Remembering** (its REMEMBER, UPDATE and FORGET lines) and **Learning names**. Each check-in adds its own answer format, such as a REMIND: or SAY: line, or OK. Keep these line formats, or Martlet can't read the answer.
- **Saving**: edits save on their own a moment after you stop typing, and apply to every new reply, glance and memory check. Reload an open conversation to use them.
- **Prompt caches**: many prompts stay the same from message to message, so the model's prompt cache can reuse them and replies start sooner. Facts that change, such as the time or what the character shows now, go in the notes of the newest message instead.
- **Notes**: some prompts go in the notes of one request only. Martlet sends them with that message and never keeps them in the conversation.

### Token counts

Token counts are Martlet's own estimate: about one token for every three bytes of text. Martlet uses the same rule to keep requests within the model's limit, so the model itself may count somewhat fewer. Each count is for the prompt as written; the text filled in for words in braces adds to it when Martlet sends it. **All prompts together** adds up every prompt, but each request sends only the prompts it needs. The built-in prompts are short on purpose: all of them together are under 10,000 tokens, and a reply with every feature on sends under 2,000 tokens of them.

Each of Martlet's messages in the talk window shows its token line beside **What was sent**, such as *3,412 input tokens · instructions about 1,150*: how many input tokens the Thinking model read for that message (Martlet's estimate when the model didn't say), and how much of it was Martlet's instructions. **What was sent** shows every request section by section, with the size of each.

## Skills

**Companion › Skills** lists instructions Martlet uses only when it needs them, so every reply stays short and quick. Each skill says how a reply gets it: **Pinned** (every reply to you), **Attached now** (your last reply carried it), **Attached when** its feature is on (such as spoken replies, or while adult content is on), or **Loaded on demand by the director**. By default Look into it, Keep the conversation going and Reply length are pinned. You can edit any skill and add your own; edits save as you type and are the same on all your computers. Empty a skill's text to send nothing for it. Skills is an advanced page: turn on **Show advanced settings** (Settings › What Martlet shows) to see it.

## Replies

**Companion › Replies** sets how long her replies can be and how much of the conversation she keeps in mind (**Context size**). **Thinking steps** (off by default) lets a reasoning model think step by step before it answers. See [Thinking › Reply settings](Thinking#reply-settings).

## Adult content (18+)

**Companion › Replies › Adult content** is off by default. Turn it on only if you are 18 or older. Then an adult character can flirt, be sexual and react to your touches explicitly, always true to her personality. She never does anything sexual with a character under 18, and never in a Discord call. Many cloud models refuse such content; models you run yourself usually don't.

## Make characters with your AI assistant

Martlet's own MCP server lets GitHub Copilot, Claude or VS Code set Martlet up for you. Ask, for example, *"Make a Martlet character called Ava, a cheerful astronomer"*. Your assistant can make characters from a description or a SillyTavern or Chub card, change, switch and delete them, and change Martlet's settings. A running Martlet follows the changes at once. See [Martlet MCP Server](Martlet-MCP-Server).

More detail: [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Lorebooks](https://github.com/throndir2/Martlet/blob/main/docs/LOREBOOKS.md), [Avatars › Character profiles](https://github.com/throndir2/Martlet/blob/main/docs/AVATARS.md#character-profiles), [Use Martlet from an AI assistant](https://github.com/throndir2/Martlet/blob/main/docs/mcp/start-here.md#use-martlet-from-an-ai-assistant).
