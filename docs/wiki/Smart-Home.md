# Smart Home

Martlet connects to Home Assistant through **Companion › Smart home**.

## What works

Connect one Home Assistant by address/token, browser sign-in, network discovery or setup flow. Tokens live in Windows Credential Manager.

With **Let Martlet control and check my home when I ask** on, user-started turns go to Home Assistant's built-in Assist Conversation API. Martlet tells the Thinking model what Home Assistant did or answered.

## Setup and management

The Smart home page can find Home Assistant on the network, sign in, set up a new owner on an unowned instance, install Home Assistant on an eligible Linux Martlet host, list discovered devices, connect MQTT, back up, restart, update and open Home Assistant.

## Safety

Status questions about locks, doors, garage doors, gates, alarms and valves can go through. Operating them is blocked unless **Also locks, doors, garage doors, gates, alarms and valves** is on, and then asks **Yes, send it** every time.

## MCP tools

With **Let the Thinking model use Home Assistant's tools** on, Martlet registers Home Assistant's MCP server as a managed tool server. Sensitive tools block or ask; status tools can be approved by policy.

## Cameras

**Companion › Vision › Use a Home Assistant camera** can use a Home Assistant camera snapshot as a Vision source.

More detail: [Smart home](https://github.com/throndir2/Martlet/blob/main/docs/SMART_HOME.md), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md), [Cluster](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md).
