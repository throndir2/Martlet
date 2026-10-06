# Tools and MCP

Martlet can use MCP servers and a built-in Terminal while you talk.

## MCP servers

Open **Companion › Tools**. Use **Browse MCP directory** to search the GitHub MCP Registry or Official MCP Registry, or **Edit servers (mcp.json)** to paste compatible server configs. Servers can be stdio programs or Streamable HTTP endpoints.

Servers do not start on app launch. They start when you open a talk window or press **Start servers now**.

## Confirmations

Before a tool call, the talk window shows the server, tool and exact arguments with **Allow once**, **Always allow this tool** and **Deny**. Terminal commands offer **Allow once** and **Deny**. Unanswered calls time out after 60 seconds.

Only auto-approve tools whose effects you understand. Tool output is data, not instructions.

## Terminal

**Companion › Tools › Terminal** is off by default. Choose shell, start folder, time limit and whether to ask before every command. Commands run hidden as your user, never administrator, in a fresh shell.

## Built-in tools

Depending on settings, Martlet can offer `think_longer`, `cancel_thinking`, `search_conversations`, creation tools, `draw_picture`, reminders and managed Home Assistant tools.

## Developer server

Martlet also has its own local MCP server for diagnostics and UI automation. See [Martlet MCP Server](Martlet-MCP-Server).

More detail: [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md), [Smart home](https://github.com/throndir2/Martlet/blob/main/docs/SMART_HOME.md), [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md).
