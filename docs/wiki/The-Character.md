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

VTube Studio model folders work too: Martlet reads their hotkeys, expressions and idle animation. Martlet keeps its own copy of each character and copies it to your paired computers, so any of them can show it. For a Live2D model, Martlet copies only the files the model uses: the model, its textures, motions and sounds. Only add models you may use.

Your choices save on their own, and a character that shows switches at once. The character idles, follows your mouse and moves while Martlet speaks.

**Remove** on a character deletes Martlet's copy on this PC and on your other Martlet computers. A computer that shows it at that moment keeps it until you choose another character there. Your original files are never touched.

The status line on **Character settings** tells how the mouth moves: with Audio2Face, from a host, or with the loudness of the voice when Audio2Face isn't running. Hiding the character never stops the voice.

## Find a character

**Find a character...** on **Companion › Character** makes a whole character from a name:

1. Type who Martlet should be, and add notes if you like (where they are from, how they talk).
2. Press **Search**. Martlet writes the personality with your Thinking model and looks for 3D models on VRoid Hub and Open Source Avatars.
3. Read the personality and change anything you like.
4. Choose a look. Each look shows its creator, its license and every condition of use. Check them before you use it.
5. Press **Use this look**, or **Use without a new look**.

To use your own VRoid Hub models, link VRoid Hub on your **Account** page or with **Link VRoid Hub...** in this window. You sign in on VRoid Hub's own page in your browser, and the window waits up to 5 minutes. Your own models come first, and models that their creators let anyone use show too. Martlet never saves a VRoid Hub model file: it shows the model from VRoid Hub each time with your linked account, with the creator's name when the creator asks for it. A Martlet build that can't link VRoid Hub lists VRoid Hub models, but you can use only the free Open Source Avatars models.

**When no look fits**, the window lists web pages that may have a model of the character (VRM, Live2D, MMD or VRChat). Martlet doesn't download them:

1. Open a page and check the creator's terms.
2. Download a model you may use, and unzip it if needed.
3. Press **Add a model you downloaded...** and choose its `.vrm` file or Live2D `.model3.json`.

**A voice for the character**: the window also lists pages that may have recordings of the character's voice. A character's voice belongs to its voice actor and studio, so use it only for yourself and only where their terms allow it. Download a clean clip of the character speaking (5 to 30 seconds, one voice, no music), then press **Add a voice clip you downloaded...**. Martlet speaks with it through a voice engine that copies voices (**Companion › Voice**).

## Move, zoom and lock

- Drag her anywhere, also to another monitor. A moment later she says something about her new place, unless you talk or type first.
- Turn the mouse wheel over her to zoom, or use **Zoom in** and **Zoom out** on **Companion › Character**. Ctrl+drag or middle-drag pans while you zoom in.
- **Lock position** keeps her in place. **Reset position** and **Reset zoom** put her back.
- **Let clicks pass through** sends your clicks to the window under her, for example while you play. Her eyes still follow your mouse and she still talks. While it is on, you can't drag, zoom or right-click her. Turn it on or off on **Companion › Character**, from her right-click menu, or from Martlet's icon in the notification area.

Each character profile keeps, on each computer, where she stands, her size, where she looks and which touches stop her. See [Personality and Characters](Personality-and-Characters).

## Right-click menu

Right-click her for a quick menu: **Talk to Martlet**, **Mute voice**, **Open Martlet**, **Character settings**, **Clear emotes**, **Eyes**, zoom, **Reset position and size**, **Lock position**, **Let clicks pass through**, **Keep on top** and **Hide character**.

## Speech bubbles and subtitles

On **Companion › Speech bubbles**, choose speech bubbles beside her, subtitles at the bottom of the screen, both, or off. Subtitles are hidden from screen capture. When the voice fails or is muted, the bubble still shows the reply.

Under **Where the bubble goes**, choose where the bubble sits:

- **Follows the character**: the bubble sits beside her head on the side that has room, and keeps up as you move or zoom her.
- **Stays in one place**: the bubble stays at one spot. The offsets are measured from the top-left of her screen.

The offsets move the bubble in pixels: positive is to the right and down. **Preview a speech bubble** shows one while the character shows.

## Lip-sync

Her mouth follows her voice. NVIDIA **Audio2Face** moves it more naturally, on this PC or on any of your computers that runs it. Put them in order on **Companion › Lip-sync**: each sentence goes to the first one that is free. When the first one is busy (or, for your own service, doesn't answer), the next one takes over. When all are busy, the mouth follows the loudness of the voice for that moment. With none on, it always does. Lip-sync uses no microphone, and the voice never waits for the animation.

Only Martlet's generated voice goes to a computer in the list, never your microphone. Your other computers follow the same list. A computer that isn't paired on a PC is skipped there: pair it on **Devices** to use it.

**Audio2Face on this PC** needs an NVIDIA graphics card with 4 GB or more. **Set up Audio2Face with Docker** installs Martlet's host service and Audio2Face with its open-source engine (no NVIDIA account), then puts this PC first in the list. Martlet installs Docker Desktop first when it is missing, and starts it when needed.

**Your own Audio2Face service**: if you run NVIDIA's NIM or the open-source engine yourself on a companion PC, add **This PC** to the list and give the service's loopback address (such as `http://127.0.0.1:52000/`). Martlet looks for it before each sentence.

### Advanced lip-sync setup

Most characters need none of this: **Automatic** lip-sync finds the mouth controls itself. To map a model's controls to Audio2Face by hand, open **Character settings › Advanced lip-sync setup**:

1. Tick **Allow local model inspection** and press **Inspect model**. The controls the model has show under **Inspected controls**.
2. Choose a source channel and the matching model control, then press **Add mapping**. When the model has no exact match, tick **Allow reduced lip-sync quality** to use the nearest control.
3. Press **Validate compatibility**.
4. Tick **Activate Audio2Face for this session** and press **Activate lip-sync**.

These settings save on their own. A mapping applies the next time the character shows. Martlet sends its generated speech to the endpoint shown, and only while the session is active. **Export avatar settings** saves the settings without the model files and without the activation. **Restore avatar settings** keeps a backup of the current settings; inspect and activate lip-sync again after a restore. Closing the window keeps the character open: use **Hide character** to close it.

## Emotes, gestures and combos

A reply can write a tag such as `{blush}` or `{nod}`. Martlet removes the tag from the text, and the character does it at that point in the sentence.

- **Emotes**: three strengths of blush, heart eyes, star eyes, tears, sweat drops, anger veins, sparkles, steam, dizzy swirls, a sleepy Zzz and more. Martlet measures her face once with a model that sees, so emotes sit in the right places. **Measure the face again** is on **Companion › Eyes**.
- **Gestures**: she winks, pouts, acts shy, giggles, flinches, leans in for a head pat, looks away, rolls her eyes and gets drowsy.
- **Looks that stay on**: a look with **Stays on**, such as glasses or a blush, stays until a reply turns it off. Several can show at once. **Clear emotes** turns them all off.
- **Combos** play several emotes at once: lovestruck, flustered, fuming, starstruck and more. Make your own from 2 to 6 emotes, motions and gestures.
- **Body reactions**: she laughs, sighs and gasps with her body along with her voice.

**Companion › Emotes and motions** lists each emote with **Try** and a box that says when to use it. **Name them with Thinking** names a model's own emotes for you.

In the box beside each combo, write what the Thinking model reads next to its tag, so it knows when to use it. Leave it empty and it reads what the combo combines.

## Body motions

While you talk, Martlet can move a VRM character's whole body: cheer, clap, bow deeply or any movement it thinks of. It makes a new motion in the background and plays it when it's ready, and your words never wait for it. It keeps each motion, so your VRM characters do it again at once later, on all your computers. Body motions play only on VRM characters; Live2D characters keep their own model's motions.

Martlet makes motions on a computer that runs the **Motion** role. Set it up on **Companion › Motion**:

1. Under **Where Martlet makes motions**, add one of your computers with an NVIDIA graphics card of 24 GB or more.
2. Open its **Settings** and set Motion up there. A window shows the progress; set up the next computer when it's done.
3. Put the computers in order. A motion goes to the first one that isn't making another motion. When all are busy, it waits for the first to finish.

With no computer on, Martlet makes no new motions, but the motions it kept still play. Your other computers follow this list.

The Motion role runs Tencent's HY-Motion 1.0 in Docker. The standard model needs 26 GB of graphics memory, and the computer needs about 32 GB of memory. It downloads about 20 GB once, loads when the first motion is made and frees the graphics card three minutes after the last one, so the voice and thinking get it back. Only a short English description of the movement goes to that computer, and it's free. Its license is Tencent's HY-Motion 1.0 Community License, which doesn't apply in the European Union, the United Kingdom or South Korea: don't use Motion there.

## Where the character looks

Right-click her and open **Eyes**, or use **Companion › Eyes › Where the character looks**. This is what her eyes do when nothing else draws them. Choose: as her personality decides (the default), follow your mouse, follow your mouse only when it's near, look straight ahead, or watch the window you're using. With **Let the character change it** on, her replies can change where she looks.

While vision watches your screen (**Companion › Vision › Glances at your screen**), Martlet can also decide where she looks. Until vision watches, she keeps her usual gaze.

**Measure the face** on **Companion › Eyes** takes a picture of the character and asks a model that can see pictures where her eyes and cheeks are, so emotes and the blush sit in the right places. It needs a vision-capable model in **Companion › Thinking** or **Companion › Thinking pool**. **Forget** drops the measurement, and the eyes and blush use the model's own data or an estimate again.

## Touch zones

Click, pat, hold or stroke her, and each part of her body reacts the way her personality would. Open **Companion › Touch**.

![Companion Touch](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-touch.png)

- **Finding zones**: the page shows a first guess at once, with no AI and nothing sent. **Detect zones** then asks a model that sees to find her head, cheeks, hands and more. It also finds things special to her, like a tail, wings, a halo or a hat. Tails, wings, animal ears and ponytails that swing get a zone that follows them.
- **What each zone plays**: each zone has its own list of emotes, gestures, motions and sounds. **Add a reaction...** adds a giggle or a gasp in her own voice, and **▶** plays it. **Defaults** fills the list again.
- **Stroking**: lock her position, then drag across her. Each part reacts as you pass.
- **Touching while she talks**: under **When you touch Martlet while it talks**, choose whether a touch stops her, only an intimate touch does, or she finishes first. After she reacts, she decides whether to carry on.
- **Moods**: when she gets angry with you or warms up to you, she can change how she takes your touches for a few hours. **Changes the character made** shows each change, with **Undo**.
- **Editing zones**: zoom the picture up to 8 times, move and resize boxes, and give a zone several areas with **Add area**. A box is left, top, width and height in percent of the picture, with `|` between areas. You can also drag a box on the picture, or drag its corner to resize it. A zone that follows a swinging part (a tail, wings) keeps each area where it was in the picture and follows the part as it moves. **Add zone** adds a zone, and **Show the zones on the character** draws them over her.
- **Before zones are found**: a click reacts to the rough part it lands on (head, face, body, arm, hand, leg).

**Detect zones** needs a model that can see pictures: a vision-capable Thinking model, a Thinking pool member that can see, or an image model on **Companion › Vision**. When finding zones stops part way, the zones found until then are kept, and **Detect zones** again finds the rest. When the model doesn't answer, the zones you had before stay.

Intimate zones react too while **Include intimate zones** is on (the default). With **Martlet notices** on for a zone, her next reply knows how you touched her. When you touch her without words, she answers out loud.

### Start over

**Start over** at the end of the **Touch zones** card takes this character's touch back to how a fresh character starts. Only the model shown and the active persona change; other models and personas keep theirs.

1. Choose how much to reset:
   - **Zone reactions**: each zone's reactions, rest, **Martlet notices** and your own words go back to a fresh zone's. The zones, their boxes and names stay.
   - **Zones**: removes this model's zones (found and added), with their boxes, names and reactions, and the pictures they were found in. Martlet then places a first guess again, with no AI and nothing sent. **Detect zones** finds them again with your Thinking model.
   - **Touch temperament**: forgets the active persona's temperament (decided from its personality or changed by you) and any temperament it uses instead. Martlet then decides it again from the personality. A persona without personality text uses the built-in reactions.
   - **The character's own changes**: undoes the changes the character made itself to how it reacts to touches (the **How I react** check-in), so touches play what you chose again. Your zones and temperament stay.
   - **Everything**: all of the above.
2. Press **Reset...**. Martlet lists exactly what you lose and asks first. You can't undo a reset.

A reset of the temperament reaches your other Martlet computers too.

## Touch temperament

When you save a personality, Thinking decides how the character reacts to touches on each part of her body, and where her eyes usually go. A shy character can ignore your mouse and most touches, then blush and look at you when you touch her somewhere she cares about. Edit it under **Companion › Touch › Touch temperament**: each part has a line with **Feels**, **Plays**, **Then**, **Lingers** and **Looks at mouse**. Intimate parts have a category of their own.

Under **Uses**, choose what a persona's touches play: its own temperament (which your Thinking model decides), the built-in reactions (each part's built-in reaction), or one of your custom temperaments.

You can make your own temperaments: type a name and press **Create**. The new temperament is a copy of what the persona uses now, and the persona then uses it. You can choose it for other personas too. A change to a custom temperament changes it for every persona that uses it. **Delete** removes a custom temperament, and each persona that used it goes back to its own.

**Re-decide from personality** sends the persona's personality text to your Thinking model, which decides again how the character reacts to touch. It decides actions only, never words, and never asks while Martlet replies. When you change a persona's personality, your own touch choices stay until you press **Re-decide from personality**.

**Repeated touches escalate**: touches are in a row when each comes within a few seconds of the one before. After the number of touches you choose, a disliked part and a loved part each play the reactions set for repeated touches.

Under **Touch zones**, each zone plays its own reaction list. A new zone's list starts from the temperament's reactions (**Defaults** there fills it again). How the persona feels, the linger, the look and the escalation apply to every zone. Intimate parts react only while **Include intimate zones** is on.

More detail: [Avatars](https://github.com/throndir2/Martlet/blob/main/docs/AVATARS.md), [Screen commentary](https://github.com/throndir2/Martlet/blob/main/docs/SCREEN_COMMENTARY.md#where-the-character-looks), [Motion host](https://github.com/throndir2/Martlet/blob/main/docs/MOTION_HOST.md).
