# Wiki source

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `scripts/Publish-Wiki.ps1`

The pages of the [Martlet Wiki](https://github.com/throndir2/Martlet/wiki). The root README links to them.

1. Edit the pages here. Link to another page by its name, for example `[Vision](Vision)`. Link to a repository file with an absolute `https://github.com/throndir2/Martlet/blob/main/...` address.
2. Check the links with `.\scripts\Publish-Wiki.ps1 -CheckOnly`.
3. After the change merges, publish with `.\scripts\Publish-Wiki.ps1`.

The wiki is generated from these files. A publish replaces edits made on the wiki in the browser. `_Sidebar.md` and `_Footer.md` are navigation fragments, not articles.

## Get started

| Page | Question it answers |
| --- | --- |
| [Home](Home.md) | Where do I start? |
| [Installing Martlet](Installing-Martlet.md) | How do I install the Windows app, and what does the installer do? |
| [First Steps](First-Steps.md) | What does the welcome tour set up? |
| [Linux and Mac](Linux-and-Mac.md) | What does the Linux and macOS companion do? |
| [FAQ](FAQ.md) | What are the short answers to common questions? |

## Talk and listen

| Page | Question it answers |
| --- | --- |
| [Talking to Martlet](Talking-to-Martlet.md) | How do I talk, type and interrupt? |
| [Listening and Voice ID](Listening-and-Voice-ID.md) | How does she hear me and know who speaks? |
| [Voices](Voices.md) | How do I pick or clone a voice? |
| [Singing](Singing.md) | How does she write and sing songs? |

## Her look and mind

| Page | Question it answers |
| --- | --- |
| [The Character](The-Character.md) | How does the on-screen character work? |
| [Vision](Vision.md) | How does she watch my screen or camera? |
| [App Guides](App-Guides.md) | How does she learn the apps and games I use? |
| [Thinking](Thinking.md) | Which model writes her replies, and how do I set it up? |
| [Personality and Characters](Personality-and-Characters.md) | How do I change her personality or import a card? |
| [Memory](Memory.md) | What does she remember, and where is it kept? |

## Extras

| Page | Question it answers |
| --- | --- |
| [Tools and MCP](Tools-and-MCP.md) | Which tools can she use, and how do I add more? |
| [Pictures and Creations](Pictures-and-Creations.md) | How does she paint, write songs and research? |
| [Smart Home](Smart-Home.md) | How do I connect Home Assistant? |
| [Discord](Discord.md) | How do I use Martlet on Discord? |
| [Telegram](Telegram.md) | How do I message her from Telegram or WhatsApp? |

## Computers and people

| Page | Question it answers |
| --- | --- |
| [Multiple Computers](Multiple-Computers.md) | How do my computers work together? |
| [Setting Up a Host](Setting-Up-a-Host.md) | How do I set up a computer that runs jobs? |
| [Sharing with Friends](Sharing-with-Friends.md) | How do I share a host or give keys to other apps? |
| [Accounts and Household](Accounts-and-Household.md) | How do people in a household get their own Martlet? |

## Help

| Page | Question it answers |
| --- | --- |
| [App Settings and Updates](App-Settings-and-Updates.md) | Where are the settings, and how does updating work? |
| [Privacy and Safety](Privacy-and-Safety.md) | What data goes where, and what can she do? |
| [Troubleshooting](Troubleshooting.md) | What do I do when something fails? |

## For developers

| Page | Question it answers |
| --- | --- |
| [Developer Guide](Developer-Guide.md) | Where do I start as a contributor? |
| [Repository Layout](Repository-Layout.md) | Which folder holds what? |
| [Source Projects](Source-Projects.md) | What do the smaller source projects do? |
| [Architecture](Architecture.md) | How do the app, companion and host fit together? |
| [Validation and Testing](Validation-and-Testing.md) | How do I validate a change? |
| [Martlet MCP Server](Martlet-MCP-Server.md) | How do I use Martlet through its own MCP server? |
| [Packaging](Packaging.md) | How are the installers and packages built? |
| [Releasing](Releasing.md) | How do I publish a release? |
| [Roadmap](Roadmap.md) | What is built, and what is planned? |