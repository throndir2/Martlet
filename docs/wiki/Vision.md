# Vision

Vision lets Martlet glance at your screen, active window or camera and comment occasionally.

![Companion Vision](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-vision.png)

## Turn it on

Open **Companion › Vision**. Choose **my whole screen**, **my active window** or a camera/source. Vision allows looking, but actual watching starts only when you press **Start watching** on Home, in the talk window or in the tray menu. Stop with **Stop watching**, **Stop**, Esc, Windows lock or ending the conversation.

## Chattiness

**How often it comments** can be Quiet, Normal, Chatty or **Martlet decides**. When Martlet decides, it can quiet down when you are focused or get chatty when invited.

## How it works

Martlet captures a downscaled image on this PC, compares it with the last one, and occasionally sends one screenshot to a Thinking model that can see images. It is told to say `[pass]` unless a remark is worthwhile.

When you ask "what do you think of this?", your message can include the newest picture from the watched source.

## Privacy

Screenshots are kept in memory only: not saved, logged, remembered or put in support bundles. Private/incognito browser and password-manager windows are skipped or greyed by title. Martlet's own windows are greyed out. Protected black video is skipped.

## Cameras

Vision can use webcams, capture cards, phone-as-webcam apps, IP camera sources and Home Assistant camera snapshots.

## Character gaze

**Companion › Vision › Where the character looks** can let Martlet decide where the character looks while Vision watches.

More detail: [Screen commentary](https://github.com/throndir2/Martlet/blob/main/docs/SCREEN_COMMENTARY.md), [Smart home cameras](https://github.com/throndir2/Martlet/blob/main/docs/SMART_HOME.md), [Avatars](https://github.com/throndir2/Martlet/blob/main/docs/AVATARS.md).
