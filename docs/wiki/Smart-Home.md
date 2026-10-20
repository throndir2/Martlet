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
  Assistant yet, a form asks for your name, username and password. See
  [Set up a new Home Assistant](#set-up-a-new-home-assistant).
- Type the **Home Assistant address**, paste a long-lived access token and press
  **Connect with token**. See [Access token](#access-token).

Then do these steps:

1. At the top of the page, under **Smart home**, choose **Home Assistant**.
2. Press **Use Home Assistant when I ask**.

Status questions and actions work only with the smart-home system that you
choose there. **Off** means that Martlet doesn't control your smart home.

Martlet uses only the address that you give it. With a paired Martlet host and
**Keep Martlet the same on all my computers** on, one connection is for all
your computers. See [On all your computers](#on-all-your-computers).

### Find or type the address

Use the address that you open Home Assistant with in your browser, for
example `http://homeassistant.local:8123`.

- **Find on my network** asks your home network once for Home Assistant. It
  shows each Home Assistant that answers. Choose **Use this one**.
- When no Home Assistant answers, type its address. Or install one on a Linux
  Martlet host (see
  [Install Home Assistant on one of your computers](#install-home-assistant-on-one-of-your-computers)).
- Plain `http` addresses work only on your home network. Use an `https`
  address outside your home network.
- When Martlet says that the address answered, but not like Home Assistant,
  check the address. Use the exact address that you open in the browser.

### Sign in or set up

**Sign in with Home Assistant** opens Home Assistant's own sign-in page in your
browser:

1. Type the address, or find it.
2. Press **Sign in with Home Assistant**.
3. Sign in to Home Assistant in your browser. Martlet waits up to 5 minutes.
4. When the browser says **Signed in**, close the tab and go back to Martlet.

Martlet then makes and keeps its own access token. Sign in as the owner (an
administrator) when you want to manage Home Assistant from Martlet.

**Set up a new one** checks the address. When nobody has set up that Home
Assistant yet, the **Set up this Home Assistant** form opens. When it is
already set up, use **Sign in with Home Assistant**.

### Set up a new Home Assistant

The **Set up this Home Assistant** form makes the owner account of a Home
Assistant that nobody has set up yet:

1. Type your name and a username.
2. Type a password of at least 8 characters two times.
3. Press **Set up Home Assistant**.

Martlet then does these things:

- It makes the owner account (an administrator) with this name and password.
- It gives Home Assistant this PC's time zone, country, currency, units and
  language.
- It leaves the usage analytics of Home Assistant off.
- It connects with its own access token.

Your password goes only to Home Assistant, and Martlet doesn't keep it. Sign in
to Home Assistant with this username and password. After the setup, set your
home's location in Home Assistant (**Settings › System › General**).

When someone else made the owner account first, use **Sign in with Home
Assistant** instead.

### Access token

A long-lived access token is a key that lets Martlet use your Home Assistant.
To make one:

1. Open Home Assistant in your browser.
2. Open your profile and then **Security**.
3. Under **Long-lived access tokens**, create a token. Copy it.
4. In Martlet, paste it in **Or paste a long-lived access token**.
5. Press **Connect with token**.

Martlet keeps the token in Windows Credential Manager and sends it only to the
address that you typed. When Home Assistant rejects the token, make a new one,
or sign in again.

### Disconnect

**Disconnect** removes the saved token from this PC, and Martlet stops using
your smart home. With a paired Martlet host and **Keep Martlet the same on all
my computers** on, it removes the token from all your computers, and your
Martlet hosts forget it. When no Martlet host answers, your other computers
disconnect when a host answers again.

Disconnect doesn't delete the token in Home Assistant. Revoke the token in
your Home Assistant profile (**Security**) if you no longer need it.

## Install Home Assistant on one of your computers

Martlet can install Home Assistant on one of your Linux computers that runs
Martlet's host service with Docker Engine. Martlet reaches it over SSH.

1. Under **Home Assistant on your Martlet hosts**, press **Install Home
   Assistant** next to the computer. The **Devices** map has the same command.
2. When it's installed, the setup form opens. Make your owner account.

Docker Desktop on Windows or macOS can't do this, because Home Assistant must
be on your home network. On Windows, use a Linux computer or a Home Assistant
you already have. See [Setting Up a Host](Setting-Up-a-Host).

Each host line shows what the host told Martlet at its last check. After you
change a host, use **Check connection** on the **Devices** map to update it.
When something already uses port 8123 on a host, it is probably another Home
Assistant. Connect to it with **Find on my network** or type its address.

## What Martlet may do

The **What Martlet may do** card sets which devices Martlet may control:

- Status questions always work.
- Lights, switches, climate, media, scenes and blinds run immediately.
- Locks, doors, garage doors, gates, alarms and valves need **Allow locks,
  doors and alarms** and a **Yes** each time. See
  [Locks, doors and alarms](#locks-doors-and-alarms).

Martlet can reach only the devices that are exposed to voice assistants in
Home Assistant (**Settings › Voice assistants › Expose**). Keep sensitive
devices hidden unless you need them.

Screen and camera looks never control your home. Only what you say or type
goes to Home Assistant. Use [Voice ID](Listening-and-Voice-ID) when Martlet
should answer only you.

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

### Recent actions

**Recent actions** at the bottom of **Companion › Smart home** lists what
Martlet did with Home Assistant. The list clears when you close Martlet.

A **Safety problem** line means that Home Assistant operated a lock, door,
garage, gate, alarm or valve without your confirmation. To block this, hide
that device from voice assistants in Home Assistant.

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
Martlet uses Assist (standard commands) as before.

After you turn it on, reload an open talk window to use it. The status line
under the check box tells if Home Assistant's tools are ready. When Martlet
can't connect to them, it uses standard commands.

## Manage Home Assistant

### Add devices

Home Assistant looks for devices on its own network, for example Hue, Sonos,
Chromecast, Shelly, ESPHome, HomeKit and Matter devices. **Devices Home
Assistant found** lists the new devices that it found:

- **Add** adds a device to Home Assistant.
- **Ignore** hides a device. Home Assistant doesn't offer it again. You can
  still add it from the **Integrations** page of Home Assistant.
- When a device needs more details (such as a code, a key or a choice), or
  when Home Assistant can't add it, its **Integrations** page opens. Finish the
  setup there.
- When Home Assistant is still adding a device, press **Check for devices**
  again after a moment.
- **Add MQTT to Home Assistant** connects the MQTT broker on the same
  computer.

New lights, switches, climate, media and blinds become available to Martlet
automatically. Locks stay hidden.

### Updates, backups and restart

**Manage Home Assistant** shows the version, integrations, last backup and
waiting updates. Use **Install update**, **Back up now**, **Restart** and
**Open Home Assistant**.

- **Install update** asks first. Home Assistant can restart to finish the
  update. Press **Refresh** after a few minutes.
- **Back up now** makes a backup on the disk of Home Assistant. Press
  **Refresh** after a minute to see it.
- **Restart** asks first. Your smart home doesn't respond for approximately a
  minute.

These need an administrator's sign-in. Martlet's own setup gives one, and so
does **Sign in with Home Assistant** as the owner.

These run only when you click them. Martlet never gives them to the Thinking
model. When Martlet installed Home Assistant, **Update host** on **Devices**
gets newer Home Assistant versions.

## On all your computers

Home Assistant is one connection for all your computers. With **Keep Martlet the
same on all my computers** on, a connection made on one computer goes to all of
them, and what Martlet may do is the same everywhere. **Disconnect**
disconnects every computer.

Your paired Martlet hosts keep the address and Martlet's access token
privately. They give them only to your paired computers, which then connect
automatically, usually within a minute. **Your other computers** on the page
shows which computers use the connection. **Check now** checks again
immediately.

## Cameras

**Companion › Vision › Use a Home Assistant camera** uses a Home Assistant
camera's picture as a camera for Vision. See [Vision](Vision). When a camera
doesn't work, see [Camera problems](Vision#camera-problems).

More detail: [Smart home](https://github.com/throndir2/Martlet/blob/main/docs/SMART_HOME.md), [MCP](https://github.com/throndir2/Martlet/blob/main/docs/MCP.md#tools-while-you-talk-mcp-client), [Cluster](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md#the-shared-home-assistant-connection).
