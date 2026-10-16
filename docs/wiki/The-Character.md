# The Character

Martlet can show a Live2D or VRM character on your desktop. She stands on top of your windows, also over full-screen games. She moves her lips with her voice, shows how she feels and looks where she wants.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/character-bubble-dark.png">
  <img src="https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/character-bubble-light.png" alt="Martlet character with speech bubble">
</picture>

## Show and add characters

The character is off until you press **Show character** on Home or on **Companion › Character**. To show her each time Martlet starts, tick **Show at startup** in **Character settings**. The built-in Hiyori (Live2D) is ready from the start.

To add your own character:

1. Open **Companion › Character**.
2. Under **Your characters**, choose **Add a character...**.
3. Choose a Live2D `.model3.json` file or a VRM `.vrm` file (VRM 0.x or 1.0).

VTube Studio model folders work too: Martlet reads their hotkeys, expressions and idle animation. Martlet keeps its own copy of each character and copies it to your paired computers.

**Find a character...** makes a whole character from a name. Martlet writes the personality with your Thinking model and looks for 3D models on VRoid Hub and Open Source Avatars. Each look shows its creator, its license and every condition of use before you choose it. To use your own VRoid Hub models, link VRoid Hub on your **Account** page.

## Move, zoom and lock

- Drag her anywhere, also to another monitor. A moment later she says something about her new place, unless you talk or type first.
- Turn the mouse wheel over her to zoom. Ctrl+drag or middle-drag pans while you zoom in.
- **Lock position** keeps her in place. **Reset position** and **Reset zoom** put her back.
- **Let clicks pass through** sends your clicks to the window under her, for example while you play. Her eyes still follow your mouse and she still talks. Turn it off on **Companion › Character** or from Martlet's icon in the notification area.

Each character profile keeps, on each computer, where she stands, her size, where she looks and which touches stop her. See [Personality and Characters](Personality-and-Characters).

## Right-click menu

Right-click her for a quick menu: **Talk to Martlet**, **Mute voice**, **Open Martlet**, **Character settings**, **Clear emotes**, **Eyes**, zoom, **Reset position and size**, **Lock position**, **Let clicks pass through**, **Keep on top** and **Hide character**.

## Speech bubbles and subtitles

On **Companion › Speech bubbles**, choose speech bubbles beside her, subtitles at the bottom of the screen, both, or off. Subtitles are hidden from screen capture. When the voice fails or is muted, the bubble still shows the reply.

## Lip-sync

Her mouth follows her voice. NVIDIA **Audio2Face** moves it more naturally, on this PC or on any of your computers that runs it. Put them in order on **Companion › Lip-sync**: when the first one is busy, the next one takes over. With none on, the mouth follows the loudness of Martlet's voice. Lip-sync uses no microphone, and the voice never waits for the animation.

## Emotes, gestures and combos

A reply can write a tag such as `{blush}` or `{nod}`. Martlet removes the tag from the text, and the character does it at that point in the sentence.

- **Emotes**: three strengths of blush, heart eyes, star eyes, tears, sweat drops, anger veins, sparkles, steam, dizzy swirls, a sleepy Zzz and more. Martlet measures her face once with a model that sees, so emotes sit in the right places. **Measure the face again** is on **Companion › Eyes**.
- **Gestures**: she winks, pouts, acts shy, giggles, flinches, leans in for a head pat, looks away, rolls her eyes and gets drowsy.
- **Looks that stay on**: a look with **Stays on**, such as glasses or a blush, stays until a reply turns it off. Several can show at once. **Clear emotes** turns them all off.
- **Combos** play several emotes at once: lovestruck, flustered, fuming, starstruck and more. Make your own from 2 to 6 emotes, motions and gestures.
- **Body reactions**: she laughs, sighs and gasps with her body along with her voice.

**Companion › Emotes and motions** lists each emote with **Try** and a box that says when to use it. **Name them with Thinking** names a model's own emotes for you.

With a computer that runs the **Motion** role (an NVIDIA graphics card with about 24 GB), a VRM character can also cheer, clap, bow and do new whole-body motions. Set it up on **Companion › Motion**.

## Where the character looks

Right-click her and open **Eyes**, or use **Companion › Eyes › Where the character looks**. Choose: as her personality decides (the default), follow your mouse, follow your mouse only when it's near, look straight ahead, or watch the window you're using. With **Let the character change it** on, her replies can change where she looks.

## Touch zones

Click, pat, hold or stroke her, and each part of her body reacts the way her personality would. Open **Companion › Touch**.

![Companion Touch](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-touch.png)

- **Finding zones**: the page shows a first guess at once, with no AI and nothing sent. **Detect zones** then asks a model that sees to find her head, cheeks, hands and more. It also finds things special to her, like a tail, wings, a halo or a hat. Tails, wings, animal ears and ponytails that swing get a zone that follows them.
- **What each zone plays**: each zone has its own list of emotes, gestures, motions and sounds. **Add a reaction...** adds a giggle or a gasp in her own voice, and **▶** plays it. **Defaults** fills the list again.
- **Stroking**: lock her position, then drag across her. Each part reacts as you pass.
- **Touching while she talks**: under **When you touch Martlet while it talks**, choose whether a touch stops her, only an intimate touch does, or she finishes first. After she reacts, she decides whether to carry on.
- **Moods**: when she gets angry with you or warms up to you, she can change how she takes your touches for a few hours. **Changes the character made** shows each change, with **Undo**.
- **Editing zones**: zoom the picture up to 8 times, move and resize boxes, and give a zone several areas with **Add area**. **Add zone** adds a zone, and **Show the zones on the character** draws them over her. **Start over** resets the zones, their reactions, the temperament, her own changes or everything.

Intimate zones react too while **Include intimate zones** is on (the default). With **Martlet notices** on for a zone, her next reply knows how you touched her. When you touch her without words, she answers out loud.

## Touch temperament

When you save a personality, Thinking decides how the character reacts to touches on each part of her body, and where her eyes usually go. A shy character can ignore your mouse and most touches, then blush and look at you when you touch her somewhere she cares about. Edit it under **Companion › Touch › Touch temperament**: each part has a line with **Feels**, **Plays**, **Then**, **Lingers** and **Looks at mouse**. Intimate parts have a category of their own.

You can make your own temperaments: name one, change it, and choose it under **Uses** for any persona. A change to a custom temperament changes it for every persona that uses it. **Re-decide from personality** brings a persona back to its own.

More detail: [Avatars](https://github.com/throndir2/Martlet/blob/main/docs/AVATARS.md), [Screen commentary](https://github.com/throndir2/Martlet/blob/main/docs/SCREEN_COMMENTARY.md#where-the-character-looks), [Motion host](https://github.com/throndir2/Martlet/blob/main/docs/MOTION_HOST.md).
