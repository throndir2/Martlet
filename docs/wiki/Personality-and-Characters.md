# Personality and Characters

Make Martlet your own: write her personality, import character cards and lorebooks, edit her prompts, and switch her look, voice and personality in one click.

![Companion Personality](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-personality.png)

## Personas

A persona says who Martlet is and how she talks. Open **Companion › Personality** and press **Edit personality** to create, edit or switch personas. Write the tone you want (helpful, sarcastic, silly or teasing) in your own words, as a character card does. Changes save on their own, and the next message uses the chosen persona. The talk window calls your character by the persona's name.

You can keep up to 1,000 personas, and one persona can be up to 100,000 characters long, so big character cards fit.

## Character profiles

A profile switches look, voice and personality together. Open **Companion › Profiles** and press **New profile...** to save what Martlet uses now as a profile. Switch from **Companion › Profiles**, Home's **Character** box or the **Character profile** menu of Martlet's icon in the notification area. A part that can't switch yet stays as it was, and Martlet says why.

On each computer, a profile also keeps where the character stands, how big she is, where she looks and which touches stop her while she talks. Switch back to a profile and they come back, also after a switch on another computer. Emotes, touch zones and face measurements belong to each look, and the touch temperament belongs to each persona.

To make a whole character from a name, use **Find a character...** on **Companion › Character** (see [The Character](The-Character)).

## Sharing a character with your household

When several people use Martlet, each person has their own characters. On **Companion › Profiles**, each of your profiles has a sharing choice:

- **Private** (the default): only you see and use it.
- **Share a copy with the household**: other people see it under **Household characters** and can choose **Use a copy**. They get its personality, look, voice, emotes and lorebooks as their own character. The copy has its own memories, and changes to it are theirs alone.
- **Share together with the household**: other people choose **Talk to it**, and the character joins their profiles. Everyone talks to the same character. It remembers everyone it talks to in its own memories, and it knows whose each fact is. When you change its personality, look, voice or lorebooks, everyone gets your change. Only you can edit it. Others can choose **Leave** to take it out of their profiles.

When you make a character private again, copies that people already made stay theirs. A character shared together leaves the other people's profiles. See [Accounts and Household](Accounts-and-Household).

## Character cards

On **Companion › Personality**, press **Import a character card**. Martlet reads SillyTavern and Chub cards as PNG, JSON or CHARX files. The card becomes a persona. Its always-on entries go into the persona text, and its keyword entries become a lorebook used only with that persona.

## Lorebooks

Lorebooks work like SillyTavern World Info. Each entry has keywords. When a keyword comes up in the recent conversation, the entry's text goes to the Thinking model as a note. Always-on entries go every time.

Open **Companion › Lorebook** to turn lorebooks on or off, import one or press **Edit lorebooks**. In the editor you create, import, scope and edit lorebooks and entries. **Try it** shows which entries a message would trigger, without sending anything. Edits save on their own.

- Import SillyTavern World Info JSON, a character card's `character_book`, standalone character books and NovelAI lorebooks. **Export for SillyTavern** writes World Info JSON.
- **Match other word forms** (off by default) lets *dragon* also find *dragons*.
- Each person has their own lorebooks, the same on all their computers.

## Prompts

**Companion › Prompts** shows every instruction Martlet sends to the Thinking model: the persona wrapper, reply length, tools, lorebook, memory, screen looks, check-ins and more. Edit any of them, and your text is used instead of the built-in one. Each prompt shows an estimate of its size in tokens. **Use built-in text** puts one prompt back, and **Use all built-in prompts** puts all of them back. Some prompts need a fixed line format, because Martlet reads the answer.

## Replies

**Companion › Replies** sets how long her replies can be and how much of the conversation she keeps in mind (**Context size**). **Thinking steps** (off by default) lets a reasoning model think step by step before it answers. See [Thinking › Reply settings](Thinking#reply-settings).

## Adult content (18+)

**Companion › Replies › Adult content** is off by default. Turn it on only if you are 18 or older. Then an adult character can flirt, be sexual and react to your touches explicitly, always true to her personality. She never does anything sexual with a character under 18, and never in a Discord call. Many cloud models refuse such content; models you run yourself usually don't.

## Make characters with your AI assistant

Martlet's own MCP server lets GitHub Copilot, Claude or VS Code set Martlet up for you. Ask, for example, *"Make a Martlet character called Ava, a cheerful astronomer"*. Your assistant can make characters from a description or a SillyTavern or Chub card, change, switch and delete them, and change Martlet's settings. A running Martlet follows the changes at once. See [Martlet MCP Server](Martlet-MCP-Server).

More detail: [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Lorebooks](https://github.com/throndir2/Martlet/blob/main/docs/LOREBOOKS.md), [Avatars › Character profiles](https://github.com/throndir2/Martlet/blob/main/docs/AVATARS.md#character-profiles), [Use Martlet from an AI assistant](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md#use-martlet-from-an-ai-assistant).
