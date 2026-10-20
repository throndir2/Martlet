# Vision

Vision lets Martlet watch your screen or a camera with you, read the text on it and hear what your PC plays. She stays quiet most of the time and says something when it is worth a word, like a friend in the room.

![Companion Vision](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-vision.png)

## Start and stop watching

Vision is on by default on **Companion › Vision**, but that only allows looking. Martlet starts to look when you press **Start watching** on Home, in the talk window or from Martlet's icon in the notification area. She stops when you press **Stop watching**, Stop or Esc, or end the conversation. When you lock Windows, she stops until you unlock it.

## What she looks at

Choose it under **What Martlet looks at**:

- **My whole screen** (the default): every monitor, with the taskbar and pop-up notifications.
- **My active window**: only the window you use.
- **A camera**: a webcam, a capture card, or your phone's camera through Phone Link, DroidCam, Camo or a similar app. Press **Find cameras** to list them.
- **A phone or network camera address**: a snapshot, video stream or `rtsp://` address on your network.
- **Use a Home Assistant camera**: a camera from your [Smart Home](Smart-Home).

Full-screen games work too. Martlet uses a normal Windows screen copy and never hooks into the game, so anti-cheat has nothing to flag.

## How chatty she is

**How often it comments** can be **Quiet**, **Normal**, **Chatty** or **Martlet decides**. When Martlet decides, she gets quiet when you are focused or on a call, and chattier when you invite her remarks. She never takes another look while nothing changes on your screen.

## What she knows about your screen

- **The app in front**: she knows which app you use and whether it is full screen. So she talks about your game or show, not its menus or your setup.
- **Your message**: everything you say or type goes with the newest picture. Ask *"what do you think of this?"* and she sees what you see.
- **What changed**: **Screen summary over time** (on by default) sums up the last moments for her next reply. It needs an image model or a [Thinking pool](Thinking#thinking-pool) member that sees.
- **Where you are in a game**: while you play, she keeps a short note of where you are and what happened lately. It needs [App guides](App-Guides), screen summaries and a Thinking pool member.
- **Notifications**: with **My whole screen**, a new pop-up or a flashing taskbar button makes her look at once. She gives a heads-up only for a message, call or reminder you would want to know about.

No reply waits for any of this.

## Reading the text on your screen

**Companion › Reading** reads the text on your screen while she watches, small text too: a score, a health value, a chat line. New text makes her more likely to look. Choose where it reads, in order:

- **This PC** with Windows OCR (the default). It is fast, free and private.
- Martlet's **Reading role** with PP-OCRv5 (the most accurate) on this PC or another of your computers. It runs on the processor or on an NVIDIA graphics card.

**Read the screen** sets how often it reads: from every 10 seconds to twice a second (every 3 seconds by default). **Read my screen now** reads it at once. The Reading list stays on this PC.

## Hearing what your PC plays

On **Companion › Listening**, under **Watch along**, tick **Hear what this PC plays** (off by default). While always listening runs, she also hears videos, streams, calls and games. She knows where each sound comes from, such as a YouTube video in Chrome, a Discord call or Spotify. She never takes those words for yours.

Your **Listening** choice turns what plays into words, the same as your microphone. A cloud Listening provider gets that sound too. The words go to Thinking marked as the PC's, never as yours. On their own, they go to Thinking about every 20 seconds (45 when Martlet is quiet, 12 when she is chatty), and she mostly stays quiet about them. Martlet's own voice is left out where Windows allows it. When another output is in use too, such as a voice changer's virtual cable, she hears only the output you hear and stops hearing it while she speaks.

**Describe PC sounds** also tells her about sound without words: music and its mood, game sounds, laughter, applause and alarms. About every 10 seconds while something plays, she gets one short line for her next reply. The audio model makes it when you have one (Listening › Audio model). Otherwise a Thinking pool model that hears makes it, or a small sound tagger on this PC's processor. A reply never waits for the line.

What your PC plays is never saved, remembered, used to learn voices or sent to Home Assistant or tools.

To hear *how* you say things, see [Listening › Audio model](Listening-and-Voice-ID#audio-model).

## While you play

Tell Martlet that you play games on a PC (the welcome tour asks, or use **Your preferences** in Recommended setup). While a game runs there, she moves her thinking off that PC's graphics card to another of your computers. When the game ends, she moves it back.

## Image models

By default, the Thinking model looks at the pictures itself. Gemma 4 E2B, Martlet's default on this PC, can see. To talk with a model that only reads text, add an image model to the list on **Companion › Vision**. It can be Ollama on this PC, one of your computers, a server you run or a cloud provider. The first model in the list that is free describes each picture in words. Thinking still writes every reply, and a reply never waits for the image model.

The page lists every model that gets your pictures and what for. **Test vision** shows a model one word drawn on this PC (never your screen) and checks that it reads the word.

## Privacy

- Screenshots stay in memory only. They are never saved, logged, remembered or put in support files.
- Martlet's own windows are greyed out. Password managers and private browser windows are greyed out or skipped. Protected video shows black and is skipped.
- **Companion › Replies › Personal content** decides what she may do with personal things she sees. **Keep private** (the default) never reads out your messages. **Read my messages** lets her read them. **Everything** also lets her see password managers and private windows.
- Pictures go only to the models that **Companion › Vision** lists. A cloud machine in the Thinking pool gets them only when you tick **May receive pictures and recordings** for it.
- With a camera, tell anyone in view that the camera is watching. The camera light may turn on.
- **What Martlet sends** on **Companion › Vision** says what she looks at, how often and where the pictures go. Click its **?** for all of it.
- While she watches, what you type or say also goes with the newest picture. This makes each reply larger and can cost more with a paid provider.
- She looks only after you press **Start watching**. **Stop watching**, **Stop**, Esc, **Pause**, locking Windows or ending the conversation stops it.

**Glances at your screen** on **Companion › Vision** can let the character's eyes glance at something new on your screen. Her usual gaze is on [The Character](The-Character#where-the-character-looks).

More detail: [Screen commentary](https://github.com/throndir2/Martlet/blob/main/docs/SCREEN_COMMENTARY.md), [Reading](https://github.com/throndir2/Martlet/blob/main/docs/READING.md), [Image and audio models](https://github.com/throndir2/Martlet/blob/main/docs/SENSE_MODELS.md), [Hearing what this PC plays](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md#hearing-what-this-pc-plays).
