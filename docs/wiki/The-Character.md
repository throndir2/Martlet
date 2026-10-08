# The Character

Martlet can show a Live2D or VRM character as a transparent always-on-top desktop overlay.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/character-bubble-dark.png">
  <img src="https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/character-bubble-light.png" alt="Martlet character with speech bubble">
</picture>

## Show and add characters

Use **Show character** / **Hide character**. The built-in Hiyori Live2D sample is ready by default.

Open **Companion › Character › Your characters › Add a character...** to add a Live2D `.model3.json` or VRM `.vrm`. Martlet copies it and can share it with paired computers.

## Character settings

**Character settings** chooses the built-in character or local model, lip-sync mode and show-at-startup. Choices save automatically.

## Bubbles and subtitles

Speech bubbles follow the character and show each sentence. Optional subtitles can show the same text. If voice fails or is muted, the bubble can still show the reply.

## Lip-sync

Automatic mode tries local Audio2Face, then a paired host with Audio2Face, then loudness from Martlet's generated voice. No microphone or upload is used for lip-sync, and voice never waits for animation.

## Emotes and motions

**Companion › Character › Emotes and motions** lists model actions and gestures. Martlet can name them with Thinking. Reply tags such as `{nod}` are removed from chat and performed by the character.

## Where the character looks

Right-click the character and open **Eyes**, or use **Companion › Character › Where the character looks**, to choose what its eyes usually do: as its personality decides (the default), follow your mouse, follow your mouse only when it's near, look straight ahead, or watch the window you're using. Watching the window, its eyes follow what you do there: where you move the mouse or type in it, and its middle until you do. With **Let the character change it** on, the character can change that in its replies, and it stays that way until it changes it again.

## Touch temperament

When you save a personality, Thinking decides how the character reacts to touches on each part of its body, and where its eyes usually go. A shy, passive character can ignore your mouse and most touches, then blush and look at your mouse when you touch it somewhere it cares about. Edit it under **Companion › Character › Touch temperament**. Each body area is a category that lists its parts, and intimate parts (lips, ears, neck, chest and breasts, waist, hips, groin, buttocks and inner thighs) have a category of their own.

You can also make your own custom temperaments: name one, change it as you like, and choose it under **Uses** for any persona. Changing a custom temperament changes it for every persona that uses it.

## Profiles

Character profiles combine look, voice and personality. Switch from **Companion › Profiles**, Home's **Character** box or the tray icon menu.

More detail: [Avatars](https://github.com/throndir2/Martlet/blob/main/docs/AVATARS.md), [Screen commentary](https://github.com/throndir2/Martlet/blob/main/docs/SCREEN_COMMENTARY.md), [UI design](https://github.com/throndir2/Martlet/blob/main/docs/UI_DESIGN.md).
