# Smart Home

Martlet works with Home Assistant. Lights, climate and more answer to her
voice. Locks, doors, garage doors and alarms are blocked or need your click.
Martlet can also find, set up, back up and update Home Assistant for you, and
install it on one of your Linux computers.

Open **Companion › Smart home**.

## Connect Home Assistant

Use one of these under **Home Assistant**:

- **Find on my network** looks for Home Assistant on your home network. Choose
  **Use this one**.
- **Sign in with Home Assistant** opens Home Assistant's own sign-in page in your
  browser. Use this for a Home Assistant you already have.
- **Set up a new one** checks the address you typed. When nobody owns that Home
  Assistant yet, a form asks for your name, username and password. Martlet makes
  your owner account, gives Home Assistant your time zone, country, units and
  language, and connects. Your password goes only to Home Assistant, and
  Martlet doesn't keep it.
- Type the **Home Assistant address**, paste a long-lived access token and press
  **Connect with token**.

Then choose **Home Assistant** at the top of the page and press **Use Home
Assistant when I ask**.

Martlet keeps its access token in Windows Credential Manager and sends it only
to your Home Assistant. Plain `http` addresses work only on your home network.

## Install Home Assistant on one of your computers

Martlet can install Home Assistant on one of your Linux computers that runs
Martlet's host service with Docker Engine. Martlet reaches it over SSH.

1. Under **Home Assistant on your Martlet hosts**, press **Install Home
   Assistant** next to the computer. The **Devices** map has the same command.
2. When it's installed, the setup form opens. Make your owner account.

Docker Desktop on Windows or macOS can't do this, because Home Assistant must
be on your home network. On Windows, use a Linux computer or a Home Assistant
you already have. See [Setting Up a Host](Setting-Up-a-Host).

## What you can say

"Turn off the kitchen lights." "Set the living room to 21 degrees." "Is the
garage door open?"

Martlet sends what you say to Home Assistant's built-in **Assist**. Assist acts
on the sentences it knows, in Home Assistant's language. It works with every
Thinking model, also models that can't call tools. Martlet then tells you, in
her own words, what Home Assistant did. When Assist didn't understand, she
doesn't pretend that something changed.

- Only what you say or type goes to Home Assistant. Screen and camera looks
  never control your home.
- Martlet reaches only the devices you expose to voice assistants in Home
  Assistant.
- The talk window shows a **Home Assistant:** line above her reply. **Recent
  actions** on the page lists the last actions until you close Martlet.
- Use [Voice ID](Listening-and-Voice-ID) when Martlet should answer only you.

## Locks, doors and alarms

Status questions always work: "Is the front door locked?"

To let Martlet operate locks, doors, garage doors, gates, alarms and valves:

1. Under **What Martlet may do**, turn on **Allow locks, doors and alarms**. It
   is off by default. When it is off, Martlet never sends such a request.
2. Each time, the talk window shows **Review smart home request**. Press
   **Send request** or **Don't send**. No answer in 30 seconds means no.

Keep locks and garage doors hidden from voice assistants in Home Assistant
unless you need them.

## Flexible requests

**Allow flexible smart-home requests** lets your Thinking model use Home
Assistant's own tools for harder requests. It needs:

- The **Model Context Protocol Server** integration in Home Assistant.
- A Thinking model that can call tools.

With it on, Martlet sends the names, areas and states of your exposed devices to
your Thinking model when needed. Status tools and ordinary devices run at once.
Locks, doors and alarms are blocked or ask every time. Other tools, such as
scripts, ask every time, and **Always allow** is never offered. Home Assistant's
server shows on **Companion › Tools**, managed by Smart home. Without it,
Martlet uses Assist as before.

## Manage Home Assistant

- **Devices Home Assistant found** lists new devices. **Add** adds one, and
  **Ignore** hides it. A device that needs a code or a choice opens Home
  Assistant. **Add MQTT to Home Assistant** connects the MQTT broker on the
  same computer.
- **Manage Home Assistant** shows the version, integrations, last backup and
  waiting updates. Use **Install update**, **Back up now**, **Restart** and
  **Open Home Assistant**.

These run only when you click them. Martlet never gives them to the Thinking
model. When Martlet installed Home Assistant, **Update host** on **Devices**
gets newer Home Assistant versions.

## On all your computers

Home Assistant is one connection for all your computers. With **Keep Martlet the
same on all my computers** on, a connection made on one computer goes to all of
them, and what Martlet may do is the same everywhere. **Disconnect**
disconnects every computer.

## Cameras

**Companion › Vision › Use a Home Assistant camera** uses a Home Assistant
camera's picture as a camera for Vision. See [Vision](Vision).

More detail: [Smart home](https://github.com/throndir2/Martlet/blob/main/docs/SMART_HOME.md), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md#tools-while-you-talk-mcp-client), [Cluster](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md#the-shared-home-assistant-connection).
