# FAQ

## What is Martlet?

An AI companion that lives on your desktop. She talks and listens, shows an animated character on top of your windows, and can watch your game, remember what matters to you and help on your PC. See [Home](Home).

## Which computers work?

Windows 10 (version 2004 or later) or Windows 11, 64-bit. Windows 11 on Arm PCs work through Windows' x64 emulation. Each release also has an early desktop app for Linux and Mac, with fewer features. See [Installing Martlet](Installing-Martlet) and [Linux and Mac](Linux-and-Mac).

## Is Martlet free?

Yes, and no sign-up is needed. Cloud providers can charge for use. Martlet walks you through free keys for NVIDIA Build and Google Gemini.

## Is the installer signed?

No. Martlet's releases are unsigned normal releases, so Windows may warn about an unknown publisher. You don't need administrator rights to install.

## Do I need a graphics card?

No. Any PC works with a cloud provider. A graphics card runs AI models on your own PC. Picture making, singing, Audio2Face and most cloned voices need an NVIDIA card.

## Does Martlet listen or watch when it starts?

No. Listening starts with **Start listening** or push-to-talk, and watching starts with **Start watching**. Only if you turn on **When Martlet starts, show the character and start listening** in **Settings › Startup and closing** does she start listening by herself. See [Privacy and Safety](Privacy-and-Safety) for the few things that do happen without a click.

## Does Martlet save screenshots?

No. What she sees stays in memory only. She can keep short text notes about it in the conversation.

## What are App guides?

Martlet reads the fan wikis and help pages of your games and apps, and answers from them. They are on by default: when you start a game she has no guide for, she reads up on it and tells you. Turn this off in **Companion › App guides**. See [App Guides](App-Guides).

## Where are my API keys?

In Windows Credential Manager, never in settings files or logs. They travel to your other computers over your own paired connections.

## Can everyone at home use Martlet?

Yes. Each person gets their own characters, memories and settings on every computer. Martlet signs you in with your Windows account. Click your name at the bottom of the left rail to switch person or **Add a person**. See [Accounts and Household](Accounts-and-Household).

## Can I share Martlet with friends?

Yes. On the **Share** page, share your hosts with a friend who runs Martlet, or give someone a key for your language models. They never get your settings, memories or API keys. See [Sharing with Friends](Sharing-with-Friends).

## Can another computer do the heavy work?

Yes. Add it to your Martlet network as a host. **Recommended setup** on Home plans who does each job. See [Multiple Computers](Multiple-Computers).

## Can Martlet control my smart home?

Yes, through Home Assistant. Locks, doors, garage doors and alarms are blocked or need your click. See [Smart Home](Smart-Home).

## Can I add my own character or voice?

Yes. Add Live2D or VRM characters in **Companion › Character**, and voices in **Companion › Voice**. Use only characters and voices that you have the rights to use.

## How do I update Martlet?

Martlet checks for updates and asks you. It installs them in the background, and the restart takes a few seconds. See [App Settings and Updates](App-Settings-and-Updates).

## What is MCP?

Model Context Protocol, a standard way for an AI to use tools. Martlet can use MCP tool servers ([Tools and MCP](Tools-and-MCP)). Martlet's own MCP server also lets GitHub Copilot, Claude or VS Code set Martlet up for you ([Martlet MCP Server](Martlet-MCP-Server)).

More detail: [README](https://github.com/throndir2/Martlet/blob/main/README.md), [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md).
