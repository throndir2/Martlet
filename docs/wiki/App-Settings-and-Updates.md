# App Settings and Updates

![Settings](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/settings.png)

Open **Settings** in the left rail for Martlet's own choices: its look, how it starts and closes, updates and tools. Everything about your companion is on the **Companion** page.

Pages are short. A small round **?** after a heading or a label has more words: point at it to read them, or click it to keep them open.

## Appearance

Choose **Settings › Appearance › Your palette**:

- **Pink light**: blush, cream and berry. This is the default.
- **Rose dark**: deep plum and soft rose.
- **Character light** and **Character dark**: colors from the character you show.
- **Custom**: your own palette.

To make your own palette:

1. Choose **Custom**.
2. To begin from another palette, select it in **Start from**, then choose **Use its colors**.
3. Select a part of Martlet, such as the window background, text or accent.
4. Type its color code, move the **Hue**, **Saturation** and **Lightness** sliders, or select one of your character's colors.
5. If Martlet says that something may be hard to read, choose **Make it easy to read**.

Every window and the character change as you edit. Windows high contrast overrides the palette. Your palette is your own: it follows you to every computer where you are signed in.

## Startup and closing

**Settings › Startup and closing** has these choices. They stay on each computer.

- **Keep Martlet running in the notification area when I close its window** (on by default). The character, sync, updates and your other computers keep working.
- **Start Martlet when I sign in to Windows** (off by default), and **Start in the notification area, without opening the window**.
- **When Martlet starts, show the character and start listening (and watching, while vision is on)** (off by default).
- **Greet me once Martlet has warmed up after it starts** (on by default). Once you start talking or listening and Martlet has warmed up, it says hello in character, once each time it starts. It can mention the time of day, your last conversation, reminders coming up and the apps you have open. These go to your Thinking model with the greeting. The line under the choice says whether Martlet greeted you.

The first time you close the window, Martlet tells you that it keeps running in the notification area by the clock. Right-click its icon to talk, pause or exit. If Martlet can't save one of these choices, the choice applies until Martlet closes.

## The notification area

Click Martlet's icon by the clock to open Martlet. Right-click it to:

- talk to Martlet or show the talk window,
- start or stop listening and watching,
- **Pause Martlet** (a reply, listening and watching stop until you resume) or **End the conversation**,
- show or hide the character, let clicks pass through it, or switch character profiles,
- change **Keep running when closed** and **Start with Windows**,
- **Exit Martlet**, which closes it completely.

## App updates

Martlet gets updates from its GitHub releases. Open **Settings › App updates**:

- **Check for updates automatically** is on by default. Martlet checks when it starts, then at the time in **Check every** (every hour by default).
- **Check for updates now** checks at once. **View release notes** shows what changed.

When Martlet finds a new version, it asks you once. While you are busy, it only says that the update is in Settings. You can also press **Install** *version* **now** in Settings. Then:

1. Martlet downloads the installer and checks it against GitHub's SHA-256 digest.
2. It installs the new version beside the one you use, in the background. Keep using Martlet; this takes a minute or less.
3. It asks to restart. Choose **Restart now**: the restart takes a few seconds. Or choose **Later**: Martlet switches to the new version when it exits, and Settings shows **Restart into** *version*.

If Martlet can't install beside itself, it asks to install the usual way. Then it closes, installs and starts again.

**Install updates automatically** (off by default) does all of this by itself. Martlet restarts into the update as soon as it isn't busy. It waits for a reply, for you while you talk, for a question you haven't answered and for setup work or downloads. The status line says what it waits for. After the restart, the character and listening come back if they were on.

Martlet offers only normal releases. The installer is unsigned: the digest shows that the download is intact, not who published it. Your update choices are the same on all your computers.

## Host updates

Your hosts follow Martlet's version.

- The **Devices** map shows **Update available** on an older host. **Update host** keeps its identity, pairings, roles and data.
- **Update hosts** in **Settings › App updates** updates all of them. **Update paired hosts automatically** keeps them on this PC's version.
- This PC's own host service always follows this PC's version.
- **Settings › Your other computers › Let my other paired computers update Martlet here and manage this PC's host service** lets your other computers update this PC. Their commands go over the pinned, signed connection that you paired. Martlet runs only these: update Martlet and the host service (from official GitHub releases only), install or remove a role, and show status.

## Other settings

- **What this PC is for**: **Use as my companion PC**, **Use as a Martlet host** and **Replay the welcome tour**. You can also switch your other computers here. See [Multiple Computers](Multiple-Computers).
- **Recommended setup preferences**: your answers to the welcome tour's three questions and more. See [First Steps](First-Steps).
- **Your other computers**: whether they can find this PC and ask to use its hosts, and when Martlet looks for a better setup.
- **Tools**: **Troubleshooting**, **Backup / restore**, **Prerequisites** and **Hosts**. See [Troubleshooting](Troubleshooting).
- **Exit Martlet** closes Martlet completely.

The button at the bottom of the left rail shows who uses Martlet now. Click it to switch person, add a person or open your **Account** page. See [Accounts and Household](Accounts-and-Household).

### Backup and restore

**Settings › Tools › Backup / restore** saves your settings to a file on this PC, or puts back settings from a backup that you made on this PC. The backup is a local file: Martlet uploads nothing. It isn't encrypted, so keep it private. It has no API keys or other secrets, conversations, audio, memories or logs.

To make a backup:

1. Choose **Choose file...** and pick where to save the backup, or type the path in **New backup file**.
2. Choose **Create backup...** and confirm.

To restore a backup:

1. Choose **Browse...** and pick the backup, or type its path in **Backup to restore**.
2. Choose **Preview restore**. Nothing changes yet. **Restore changes** lists what changes, and **Settings preview** shows the settings from the backup.
3. Choose **Restore settings...** and confirm. The backup replaces your current settings.
4. Reload setup, and check your destinations, API keys and devices.

A restore doesn't bring back API keys, permissions or audio tests. Martlet keeps a copy of the settings that it replaced.

If you change the backup file after the preview, or the preview changes, Martlet refuses the restore. Choose **Preview restore** again and check the new preview. The same applies after a failed or canceled action.

Only one backup or restore runs at a time. While one runs, **Cancel action** stops it, and you can close the window. If it times out, wait for it to finish, then open the window again. If Martlet says that cleanup is needed, choose **Retry cleanup** before you start another backup or restore. Close **Troubleshooting** before you use backup and restore.

More detail: [Desktop app](https://github.com/throndir2/Martlet/blob/main/docs/DESKTOP_APP.md), [Greeting when Martlet starts](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md#greeting-when-martlet-starts), [UI design](https://github.com/throndir2/Martlet/blob/main/docs/UI_DESIGN.md), [Windows packaging](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md).
