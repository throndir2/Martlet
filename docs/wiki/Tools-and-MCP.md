# Tools and MCP

Give Martlet tools, and she helps on your PC and in your smart home while you
talk. She always asks before a tool from outside Martlet does something.

Her tools come from three places:

- **MCP servers** that you add. MCP (Model Context Protocol) is an open
  standard that lets an AI model use other programs and services, such as
  files, browsers or calendars.
- The built-in **Terminal**, which runs commands on your PC.
- Martlet's own **built-in tools**, such as reminders, pictures and web research.

## What you need

- A Thinking model that can call tools. Use OpenAI or a Chat Completions
  endpoint, such as Ollama or LM Studio on your PC, OpenRouter or NVIDIA Build.
  **Test tools** on **Companion › Thinking** checks your model.
- When a model refuses tools, Martlet stops offering them to that model on this
  PC for a week.
- Martlet uses tools only in replies to what you say or type. Screen looks,
  camera looks and memory work never use tools.

## MCP servers

Open **Companion › Tools**. The **MCP servers** card lists your servers.

To add a server from a directory:

1. Press **Browse MCP directory**.
2. Choose the **GitHub MCP Registry** (the most popular servers first) or the
   **Official MCP Registry** (everything, A to Z).
3. Search, then choose a server. Martlet shows what it does, what it needs and
   how it runs.
4. Fill in the fields. Secret fields, such as API keys, go to Windows
   Credential Manager, not into a file.
5. Press **Install and start**.

What you type in the search box goes to the directory you chose.

- A server that runs on this PC needs its runtime, such as Node.js or Python's
  `uvx`. When the runtime isn't on this PC, install it and restart Martlet
  first. The first start downloads the server, which can take a minute.
- A server at an online address gets what your tool calls send. Martlet can't
  yet sign in to a server through a browser (OAuth); it needs a key.
- In any field you can type `${env:NAME}` to use an environment variable.
- Installing a server again under the same name replaces it. When `mcp.json`
  has another server with that name, Martlet asks first.
- When Martlet can't install a server by itself, it says why. The server's page
  may describe other ways to run it, which you can add in `mcp.json`.

To add a server by hand, press **Edit servers** and paste its configuration
into `mcp.json`. The file uses the same format as Claude Desktop, Cursor and
VS Code. Martlet runs servers that are programs on this PC (stdio) and servers
at a Streamable HTTP address. It does not support the older SSE transport.
Martlet saves `mcp.json` as plain text, so keep secrets in environment
variables. **Insert example** adds a file server for your Documents folder,
which needs Node.js.

Each server has **On**, **Run without asking**, **Restart** and **Remove**.
**Remove** also forgets the secrets that Martlet kept for it.
Servers don't start when Martlet starts. They start when you open a talk window
or press **Start servers now**. A server that another feature adds (such as
Smart home) says *Managed by* that feature; change its permissions there. When
a feature wants a server name that `mcp.json` already uses, rename or remove
that entry in `mcp.json`.

A server is a program that runs with your permissions, or an online service
that gets what you send it. Install only servers you trust. Your servers stay
on each computer, because they are programs installed there.

## Confirmations

Before a server's tool runs, the talk window shows **Allow this tool?** with the
tool, its server and the exact details it sends. Choose:

- **Allow once**
- **Always allow**: this tool runs without asking from now on.
- **Deny**

No answer in 60 seconds means Deny. Home Assistant's tools never offer
**Always allow** (see [Smart Home](Smart-Home)).

**Recent tool use** on the Tools page lists each call and its result until you
close Martlet. It is never saved.

Skip confirmations only for tools you understand. The Thinking model chooses
what to send, and text that a tool reads (a web page, a file or an email) can
try to steer it. What a tool gets and returns goes to your Thinking model.

## Terminal

**Companion › Tools › Terminal** lets Martlet run commands on this PC when you
ask, for example "How much space is left on my D: drive?"

1. Turn on **Let Martlet run terminal commands**. It is off until you turn it on.
   It needs a Thinking model that can call tools (see [What you need](#what-you-need)).
2. Choose the **Shell**: Windows PowerShell, PowerShell 7 or Command Prompt.
3. Choose where it **Starts in**: your home folder or a folder you choose.
4. Choose the **Time limit** for one command: 15 seconds, 30 seconds or 1 minute.

**Ask before every command** is on by default. The talk window shows each
command with **Allow once** and **Deny**. You can turn asking off, but Martlet
asks you to confirm it first, because a command can do anything you can.

Each command runs hidden, one at a time, in a new shell. It runs as you and
never as administrator. A command that waits for typing ends at once. The
command and what it prints go to your Thinking model. The Terminal setting
stays on this PC; your other computers keep their own.

## Reminders

Say "Remind me to stretch at three" or "Remind me in an hour to check the
oven". Martlet sets, lists and cancels your reminders.

- When a reminder is due, Martlet says it on the companion PC you used last,
  so you hear it once, where you are.
- When no talk window is open, she starts a conversation in the background to
  say it.
- When Thinking isn't set up, a Windows notification shows the reminder.
- A reminder that came due while Martlet was off is said late, when Martlet
  starts within 12 hours.
- Each person in your household has their own reminders. See
  [Accounts and household](Accounts-and-Household).

## Web look-ups

When Martlet isn't sure of a fact, such as a price, a date or how to get
something in a game, she does a quick look-up on the web instead of guessing.
She tells you in a minute or two. Ask her to research something, and she writes
a full report. See [Web research](Pictures-and-Creations#web-research).

## Background tasks

Martlet keeps working while you talk. Two places show what she does:

- In the talk window, the task button at the top opens the **Background tasks**
  list. It shows thinking longer, look-ups, research, pictures, songs and due
  reminders, where each one runs and how long it took. **Cancel** stops one.
  Stop (Esc) does not stop them.
- The **Background tasks** page in the main window shows long steps, such as
  setting up a computer, downloads and updates. When you hide a step's window,
  the step keeps running there.

The **Thinking requests** page shows each job of your
[Thinking pool](Thinking#thinking-pool).

## Built-in tools

Martlet's own tools need no server, and only the Terminal asks first. A reply
gets only the tools for the features you set up.

| Tools | What they do |
| --- | --- |
| `think_longer`, `cancel_thinking`, `research` | Think it over, look it up or research it in the background ([Thinking](Thinking)) |
| `manage_memories`, `search_conversations`, `reminders` | Remember and forget facts, find earlier conversations, set reminders ([Memory](Memory)) |
| `draw_picture`, `sing_song`, `play_song`, `stop_singing` | Draw pictures, and write and sing songs ([Pictures](Pictures-and-Creations), [Singing](Singing)) |
| `list_creations`, `perform_creation` | Show or sing something she made before |
| `body_motion` | Move a VRM character's body ([The Character](The-Character)) |
| `read_up_on`, `search_guide`, `skip_guide` | Read up on games and apps ([App guides](App-Guides)) |
| `call_on_discord`, `set_camera_background` | Call a Discord friend, or change her camera background ([Discord](Discord)) |
| `run_terminal_command` | Run a command, when the Terminal is on |

Some of these can run right after the reply, on your Thinking pool, so her reply
starts sooner. See [Check-ins](Thinking#check-ins).

With **Allow flexible smart-home requests** on, Home Assistant's own MCP server
also shows on the Tools page, managed by Smart home.

## Martlet's own MCP server

Martlet also has its own MCP server, which lets AI assistants such as GitHub
Copilot, Claude and VS Code set her up. See [Martlet MCP Server](Martlet-MCP-Server).

More detail: [MCP](https://github.com/throndir2/Martlet/blob/main/docs/mcp/apps.md#tools-while-you-talk-mcp-client), [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md#reminders), [Smart home](https://github.com/throndir2/Martlet/blob/main/docs/SMART_HOME.md).
