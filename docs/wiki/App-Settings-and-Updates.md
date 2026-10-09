# App Settings and Updates

![Settings](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/settings.png)

## App updates

Open **Settings › App updates**. GitHub release checks are on by default at launch and then on the configured interval. Turn off **Check GitHub for new versions automatically** to stop automatic checks, or use **Check for updates now**.

Only normal non-draft, non-prerelease releases with `Martlet-<version>-win-x64.exe` are offered.

## Installing updates

**Update now** downloads the installer, verifies GitHub's SHA-256 asset digest, closes Martlet, runs the installer and starts Martlet again. The installer is unsigned; the digest is integrity, not publisher signature.

**Download and install updates automatically** waits until it will not interrupt a reply, speech, pending question, setup task, host update, backup/restore, download or command.

## Host updates

Paired hosts report their version. **Update host** rebuilds that host's gateway from this version, keeping identity, pairings and roles. This PC's own host service follows Martlet's version automatically.

## Tray, startup and closing

Closing the window can keep Martlet running in the notification area. Right-click the icon to open Martlet, talk, pause/resume, end the conversation, show/hide character, toggle startup/closing choices or exit.

Open **Settings › Startup and closing** for **Start with Windows**, **Start in the notification area**, **Keep running when closed**, and startup character/listening/watching choices.

## Appearance

Open **Settings › Appearance**. Palettes include **Pink light**, **Rose dark**, **Character light**, **Character dark** and **Custom**. Choose **Custom** to make your own: start from any palette, then select a part (window background, text, accent and so on) and change its color with a color code, the sliders or one of your character's colors. **Make it easy to read** fixes colors that are hard to read. Windows high contrast overrides decorative colors.

More detail: [Desktop app](https://github.com/throndir2/Martlet/blob/main/docs/DESKTOP_APP.md), [Windows packaging](https://github.com/throndir2/Martlet/blob/main/packaging/windows/README.md), [Troubleshooting](https://github.com/throndir2/Martlet/blob/main/docs/TROUBLESHOOTING.md).
