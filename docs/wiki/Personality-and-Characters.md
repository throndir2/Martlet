# Personality and Characters

Martlet's personality comes from personas, prompts, profiles, cards and lorebooks.

![Companion Personality](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-personality.png)

## Personas

Open **Companion › Personality**. Personas describe who Martlet is and how it talks. Write the tone you want (helpful, sarcastic, silly or teasing) in the persona text, as a character card does.

## Prompts

Open **Companion › Prompts** to edit internal prompts: persona wrapper, response length, tools, Thinking longer, who is talking, lorebook, memory, past conversations, screen/camera glance instructions and smart-home notes. Some prompts have required line formats because Martlet parses the response.

## Character profiles

Open **Companion › Profiles**. A profile switches look, voice and personality together. A part that cannot switch yet stays as it was and Martlet says why.

## Sharing a character with your household

When several people use Martlet, each person has their own characters. On **Companion › Profiles**, each of your profiles has a sharing choice:

- **Private** (the default): only you see and use it.
- **Share a copy with the household**: other people see it under **Household characters** and can choose **Use a copy**. They get its personality, look, voice, emotes and its lorebooks as their own character. The copy has its own memories, and changes to it are theirs alone.
- **Share together with the household**: other people choose **Talk to it**, and the character joins their profiles. Everyone talks to the same character. It remembers everyone it talks to in its own memories, and it knows whose each fact is. When you change its personality, look, voice or lorebooks, everyone gets your change. Only you can edit it. Others can choose **Leave** to take it out of their profiles.

When you make a character private again, copies that people already made stay theirs. A character shared together leaves the other people's profiles.

## Character cards

Martlet can import SillyTavern/Chub-style PNG, JSON and CHARX character cards. Always-on entries can become persona text; keyword entries can become a lorebook scoped to that persona.

## Lorebooks

Open **Companion › Lorebook**. Lorebooks work like SillyTavern World Info: keyword entries trigger when terms appear in the recent conversation, then selected entries are sent to the Thinking model as notes. **Edit lorebooks** creates, imports, scopes and tests entries. Edits save automatically.

Supported import includes SillyTavern World Info JSON, character-card `character_book`, standalone character books and NovelAI lorebooks.

More detail: [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Lorebooks](https://github.com/throndir2/Martlet/blob/main/docs/LOREBOOKS.md), [Avatars](https://github.com/throndir2/Martlet/blob/main/docs/AVATARS.md), [Cluster](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md).
