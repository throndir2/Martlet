# Vision

Vision lets Martlet watch your screen or a camera with you, read the text on it and hear what your PC plays. She stays quiet most of the time and says something when it is worth a word, like a friend in the room.

![Companion Vision](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion-vision.png)

## Start and stop watching

Vision is on by default on **Companion › Vision**, but that only allows looking. Martlet starts to look when you press **Start watching** on Home, in the talk window or from Martlet's icon in the notification area. She stops when you press **Stop watching**, Stop or Esc, or end the conversation. When you lock Windows, she stops until you unlock it.

## What she looks at

Choose it under **What Martlet looks at**:

- **My whole screen** (the default): every monitor, with the taskbar and pop-up notifications. When a notification pops up or a taskbar button flashes, she looks at once.
- **My active window**: only the window you use.
- **A camera**: a webcam, a capture card, or your phone's camera through Phone Link, DroidCam, Camo or a similar app. Press **Find cameras** to list them. The camera opens only while Martlet looks.
- **A phone or network camera address**: a snapshot, video stream or `rtsp://` address on your network, such as `http://192.168.1.20:8080/shot.jpg`. A password in the address is used for this session only and is never saved.
- **Use a Home Assistant camera**: a camera from your [Smart Home](Smart-Home). Press **Use a Home Assistant camera**, then pick one in the list.

With both screen choices, Martlet greys out her own windows, password managers and private browser windows. She skips minimized windows and protected video. **Companion › Replies › Personal content** set to **Everything** lets her also see password managers and private windows (see [Privacy](#privacy)).

Full-screen games work too. Martlet uses a normal Windows screen copy and never hooks into the game, so anti-cheat has nothing to flag.

### Camera problems

| Message | What to do |
| --- | --- |
| *Windows doesn't see any cameras* | Connect a camera, or choose **A phone or network camera address**. |
| *Windows is blocking camera access* | Open Windows **Settings › Privacy & security › Camera**. Turn on **Let desktop apps access your camera**. |
| *Another app is using the camera* | Close the other app (a video call, for example), or choose another camera. |
| *The camera didn't answer in time* or *Couldn't reach the camera* | Make sure that the camera is on and on the same network as this PC. Check the address, then try again. |
| *Windows can't open that address* | Use a snapshot address (one picture, such as `.../shot.jpg`), an MJPEG stream or a webcam. |
| *Windows can't read this video format* | Set the camera to MJPEG, a snapshot or H.264 video. |
| *Couldn't reach Home Assistant* | Make sure that Home Assistant runs and that Martlet is connected to it ([Smart Home](Smart-Home)), then try again. |

## How chatty she is

**How often it comments** can be **Quiet**, **Normal**, **Chatty** or **Martlet decides**. When Martlet decides, she gets quiet when you are focused or on a call, and chattier when you invite her remarks. She never takes another look while nothing changes on your screen.

Most looks end silently. She never interrupts you while you talk. The same choice also sets how often she reacts to what this PC plays (**Companion › Listening › Watch along**, see [Hearing what your PC plays](#hearing-what-your-pc-plays)).

## What she knows about your screen

- **The app in front**: she knows which app you use and whether it is full screen. So she talks about your game or show, not its menus or your setup.
- **Your message**: everything you say or type goes with the newest picture. Ask *"what do you think of this?"* and she sees what you see.
- **What changed**: **Screen summary over time** (on by default) sums up the last moments for her next reply. It needs an image model or a [Thinking pool](Thinking#thinking-pool) member that sees.
- **Where you are in a game**: while you play, she keeps a short note of where you are and what happened lately. It needs [App guides](App-Guides), screen summaries and a Thinking pool member.
- **Notifications**: with **My whole screen**, a new pop-up or a flashing taskbar button makes her look at once. She gives a heads-up only for a message, call or reminder you would want to know about.

No reply waits for any of this.

### Screen summary over time

Each reply gets only the newest picture, so she starts to speak as fast as without vision. With **Screen summary over time** on, Martlet also:

1. Keeps the last few pictures that changed, for about 25 seconds, in memory only. They are never saved.
2. Every 15 seconds while they change, or when you start to talk, sends a small sheet of them with the text read on them. It goes to your image model, or else to a [Thinking pool](Thinking#thinking-pool) model that sees. It never goes to the model that your conversation uses.
3. Adds the one or two lines that come back (*"They switched from VS Code to a boss fight"*) to your next message as a note.

A reply never waits for a summary. Each summary is one more request with a picture, so it may cost more with a paid provider. The status line under the check box names the model that makes the summaries. When no model can, it says *Off for now*: choose an image model on **Companion › Vision**, or add a model that sees to the Thinking pool.

## Reading the text on your screen

**Companion › Reading** reads the text on your screen while she watches, small text too: a score, a health value, a chat line. New text makes her more likely to look. Choose where it reads, in order:

- **This PC** with Windows OCR (the default). It is fast, free and private.
- Martlet's **Reading role** with PP-OCRv5 (the most accurate) on this PC or another of your computers. It runs on the processor or on an NVIDIA graphics card.

The first place in the list that is free reads. When it is busy or doesn't answer, the next one reads. With nothing on, Martlet doesn't read your screen. The Reading list stays on this PC. Press **Settings** on a place to choose how it reads.

New text (a score, *Victory*, a new message) makes a look more likely, and each look gets the text that was read. Replies never wait for reading, and the text is never kept in the conversation.

### How often it reads

**Read the screen** sets how often it reads: from every 10 seconds to twice a second (every 3 seconds by default). Martlet takes a full-size picture of your screen and reads it only when it changed. A screen that stays the same is read again after 9 seconds. A read that takes longer sets the pace, because only one read runs at a time. Reading more often uses more of the processor or graphics card that reads; a graphics card in your Reading list keeps up best.

**Read my screen now** reads the screen at once and shows the text. Martlet's own windows are never read. When they cover most of the screen, make the Martlet window smaller or move it aside, then try again.

### Windows OCR

Windows OCR (text recognition) on **This PC** reads a screenshot in a fraction of a second, and nothing leaves this PC. It needs a text recognition language in Windows:

1. Open Windows **Settings › Time & language › Language & region**.
2. Add your language, or open the options of a language that you have.
3. Make sure that its **Optical character recognition** feature is installed. It comes with the language.

### Set up the Reading role

Martlet's **Reading role** runs on this PC or on another of your computers. In the place's **Settings**, choose the model and press **Set up**. Martlet sets it up in Docker, as its other roles, and shows the progress.

| Model | Runs on | Notes |
| --- | --- | --- |
| **PP-OCRv5 mobile** (recommended) | The processor | Never takes the graphics card from the voice or thinking. Reads a 1920 x 1080 screenshot in about 2 s and a 4K one in about 4 s. |
| **PP-OCRv5 mobile** on a graphics card | An NVIDIA graphics card (driver 580 or newer) | As accurate, in much less time, so Martlet can read more often. Shares the card with the voice and thinking. |
| **PP-OCRv5 server** | An NVIDIA graphics card (driver 580 or newer) | The most accurate. On a processor it takes about a minute a screenshot. |
| **RapidOCR** (PP-OCRv4) | The processor | The smallest, but it misses more small text. |

What setup downloads:

- PP-OCRv5: a container from python:3.12 with hash-pinned RapidOCR 3.10.0 (Apache-2.0), ONNX Runtime 1.31.0 (MIT) and OpenCV (Apache-2.0). PaddleOCR's PP-OCRv5 mobile and server models (Apache-2.0, about 190 MB) come from RapidOCR's ModelScope release or an identical Hugging Face copy. Each file is SHA-256 checked.
- On a graphics card, also NVIDIA's CUDA 13 and cuDNN libraries from PyPI (NVIDIA's license).
- RapidOCR: a container from python:3.12 with hash-pinned RapidOCR 1.4.4 (Apache-2.0) and its PP-OCRv4 models (Apache-2.0, about 15 MB), ONNX Runtime (MIT) and OpenCV (Apache-2.0).

When another computer reads, this PC sends it screenshots while Martlet watches your screen. They are read in memory there and are not kept.

If setup stops, its run window has the details. If setup finishes but Martlet can't see the role yet, wait a minute and look at the place again.

## Hearing what your PC plays

On **Companion › Listening**, under **Watch along**, tick **Hear what this PC plays** (off by default). While always listening runs, she also hears videos, streams, calls and games. She knows where each sound comes from, such as a YouTube video in Chrome, a Discord call or Spotify. She never takes those words for yours.

**Describe PC sounds** also tells her about sound without words: music and its mood, game sounds, laughter, applause and alarms. What your PC plays is never saved, remembered or used to learn voices.

To hear *how* you say things, see [Listening › Audio model](Listening-and-Voice-ID#audio-model).

## Glances at your screen

**Glances at your screen** on **Companion › Vision** sets where the character's eyes go:

- **Keep its usual gaze** (the default): the eyes do what **Companion › Eyes › Where the character looks** says. By default, they follow your mouse. See [The Character](The-Character#where-the-character-looks).
- **Martlet decides**: with each new screenshot, the character keeps its usual gaze or glances at something interesting on the screen. That is something that just popped up or moved, or what she is about to remark on.

Martlet decides only while vision watches your active window or whole screen and the character shows. With a camera, the character keeps its usual gaze. Screenshots are compared on this PC. The Thinking model chooses only during the looks that Martlet already takes, so nothing extra is sent.

## While you play

Tell Martlet that you play games on a PC (the welcome tour asks, or use **Your preferences** in Recommended setup). While a game runs there, she moves her thinking off that PC's graphics card to another of your computers. When the game ends, she moves it back.

## Image models

By default, the Thinking model looks at the pictures itself. Gemma 4 E2B, Martlet's default on this PC, can see. To talk with a model that only reads text, add an image model to the list on **Companion › Vision**. It can be Ollama on this PC, one of your computers, a server you run or a cloud provider. The first model in the list that is free describes each picture in words. Thinking still writes every reply, and a reply never waits for the image model.

The page lists every model that gets your pictures and what for. **Test vision** shows a model one word drawn on this PC (never your screen) and checks that it reads the word.

To use a second model in Ollama on this PC, Ollama must be installed. If it isn't, set it up on **Companion › Thinking › This PC** first (see [Ollama on this PC](Thinking#ollama-on-this-pc)). Ollama runs each model in its own process, so the image model works while Thinking answers, but only while both fit on the graphics card. Martlet checks before each job. Pictures and recordings for Ollama on this PC stay on this PC.

When you choose a cloud provider, a model app or another server as the image or audio model, Martlet asks you to agree first. Pictures of your screen or camera (or recordings of your voice and of what this PC plays) and a few lines of the conversation go there, and requests to a cloud provider may cost money.

If Martlet can't read its list of image and audio models (`pools-local.json`, for example from a newer Martlet or a damaged file), the text model takes pictures and recordings. A change to the list replaces the file.

### Test vision and Test hearing

**Test vision** sends the model one picture of a single word, drawn on this PC (never your screen), and asks which word it shows. **Test hearing** (on **Companion › Hearing**, or **Hear how you say it** for the text model) sends one short recording of a single word, made by Windows speech (never your voice), and asks which word it heard.

- The tests ask a model on an OpenAI-compatible endpoint directly, also Ollama on this PC. Test vision also reaches an image model on one of your computers, through its paired, pinned connection; the test waits while a reply needs that computer.
- Martlet can't test other models. It goes by the model's name, tries it, and remembers when the model refuses a picture or a recording.
- For a cloud model, a test is one small request with your API key that may cost a little, so Martlet asks first.
- *By its name* means that Martlet guesses from the model's name; the test makes sure. *Martlet doesn't know yet* means that the name doesn't say; the test finds out.

## Privacy

- Screenshots stay in memory only. They are never saved, logged, remembered or put in support files.
- Martlet's own windows are greyed out. Password managers and private browser windows are greyed out or skipped. Protected video shows black and is skipped.
- **Companion › Replies › Personal content** decides what she may do with personal things she sees. **Keep private** (the default) never reads out your messages. **Read my messages** lets her read them. **Everything** also lets her see password managers and private windows.
- Pictures go only to the models that **Companion › Vision** lists. A cloud machine in the Thinking pool gets them only when you tick **May receive pictures and recordings** for it.
- With a camera, tell anyone in view that the camera is watching.

More detail: [Screen commentary](https://github.com/throndir2/Martlet/blob/main/docs/SCREEN_COMMENTARY.md), [Reading](https://github.com/throndir2/Martlet/blob/main/docs/READING.md), [Image and audio models](https://github.com/throndir2/Martlet/blob/main/docs/SENSE_MODELS.md), [Hearing what this PC plays](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md#hearing-what-this-pc-plays).
