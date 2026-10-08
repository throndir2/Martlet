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

## Image model

By default the Thinking model looks at the pictures itself, which works best with a model that sees, such as Gemma 4 E2B. If you'd rather talk with a text-only model, choose an **Image model** on **Companion › Vision**: Ollama on this PC, a cloud provider or server, one of your computers, or the same model as your audio model. It describes each picture in words, and Thinking still writes every reply. A reply never waits for the image model.

The card says what looks at the pictures now, what the model is known to do and what is sent where. **Test vision** shows the model one word drawn on this PC (never your screen) and checks that it reads it.

More detail: [Image and audio models](https://github.com/throndir2/Martlet/blob/main/docs/SENSE_MODELS.md).

## Privacy

Screenshots are kept in memory only: not saved, logged, remembered or put in support bundles. Private/incognito browser and password-manager windows are skipped or greyed by title. Martlet's own windows are greyed out. Protected black video is skipped.

## Cameras

Vision can use webcams, capture cards, phone-as-webcam apps, IP camera sources and Home Assistant camera snapshots.

## Character gaze

**Companion › Vision › Glances at your screen** can let Martlet glance at something new on your screen while Vision watches. What the character's eyes usually do is chosen under **Companion › Eyes › Where the character looks**.

More detail: [Screen commentary](https://github.com/throndir2/Martlet/blob/main/docs/SCREEN_COMMENTARY.md), [Smart home cameras](https://github.com/throndir2/Martlet/blob/main/docs/SMART_HOME.md), [Avatars](https://github.com/throndir2/Martlet/blob/main/docs/AVATARS.md).
