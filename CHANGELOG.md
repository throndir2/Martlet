# Changelog

What changed in each Martlet release, newest first. Download any version from
[Releases](https://github.com/throndir2/Martlet/releases).

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Each release's section here is also its notes on GitHub.

## [Unreleased]

## [0.62.1] - 2026-10-08

### Changed

- Reconfigure never deletes models when your computers switch jobs. It now says it **turns a part off** and that its downloads stay, and a model your computer already has (such as Gemma 4) is turned back on without downloading it again or needing more disk space. Recommended setup also prefers a computer that already has the model. ([#664](https://github.com/throndir2/Martlet/pull/664))
- Thinking, Voice, Listening and Lip-sync now list their models, voice engines, apps and providers as short rows you can compare at a glance: each row says what it runs on, how much graphics memory it takes and how fast it is. A long list shows the main choices first, and **Show more** lists the rest. Pick one to see everything about it (such as whether a voice can laugh, which languages it speaks, its license, what a provider costs and where your data goes), or press **Compare them** for a side-by-side table. *If Thinking fails* now has a clear **Off**, and the Optional extras' lists show at most four rows too. ([#660](https://github.com/throndir2/Martlet/pull/660))

## [0.62.0] - 2026-10-08

### Added

- Each machine in the Thinking pool has **Quick jobs** and **Long jobs** boxes. Untick Quick jobs to keep a machine for long thinking and research, or untick Long jobs to keep it free for quick checks while you talk. ([#658](https://github.com/throndir2/Martlet/pull/658))
- Your own check-ins can say which model they need (text, pictures or recordings), so only Thinking pool models that can handle them take the check-in. Each check-in can also take a screenshot, the last seconds of your microphone or of what your PC plays, or what a script of yours prints (for example, the running programs). ([#657](https://github.com/throndir2/Martlet/pull/657))
- Recommended setup now lists every part of Martlet that uses your computers, in priority order: Vision, Reading, Hearing and Smart home join Thinking, the voice, listening, the character, lip-sync, Deep thinking, singing and pictures. Each one says where it runs (with Thinking's own model, a graphics card, a processor or online), or that it's off and where to turn it on. Option details now also say what a choice needs, such as Docker or a Linux computer. ([#656](https://github.com/throndir2/Martlet/pull/656))
- Settings › Appearance has a **Custom** palette: start from any palette (Pink light, Rose dark or your character's), then choose each color with a color code, hue, saturation and lightness sliders, or one of your character's colors. Martlet changes as you go, tells you if anything gets hard to read and can fix it for you, and your other computers use the same colors. ([#655](https://github.com/throndir2/Martlet/pull/655))
- Hover a resource bar on Devices to see what each job takes, what's free, what's kept for the system and what's in use now. Hover one part of the bar to see that job in bold. ([#653](https://github.com/throndir2/Martlet/pull/653))

### Changed

- The Optional extras (Vision, Reading, Hearing, Thinking pool, Smart home, Singing and Pictures) now look alike: **Now** first, then the main choice with a clear **Off**, then their settings. Each choice is one short row with its key facts, such as graphics memory, download size, speed, cost, where your data goes and its license, and **Compare them** puts them side by side. How Martlet hears your tone has its own **Hearing** page, and Singing can now be turned off without removing it. ([#659](https://github.com/throndir2/Martlet/pull/659))
- Companion › Thinking pool is now one simple list of your machines. Each one shows what it reads and writes, its slots, and badges such as *Waits while you talk*, *Costs money* and *Offline*. Tick *Quick jobs*, *Long jobs* or *Backup for slow replies* on each machine, and *In the pool* on each paired computer. A paired computer's slots are set on that computer with **Change model**. Martlet still keeps your replies first by itself. ([#658](https://github.com/throndir2/Martlet/pull/658))
- Every Companion page now starts with **Now**: one line that says what the page uses and what stops it, such as "Tools: 3 MCP servers on, the terminal off." Speech bubbles, People, Tools, Smart home, Discord and Messaging then show their main choice with a clear **Off**, and the settings follow. ([#654](https://github.com/throndir2/Martlet/pull/654))

## [0.61.0] - 2026-10-08

### Added

- Recommended setup lists every part of Martlet in priority order: what it needs, what's optional, where each part runs, and **Off** with the reason. Tick **Off** on an optional part (advanced lip-sync, Deep thinking, singing, pictures) to plan without it. ([#651](https://github.com/throndir2/Martlet/pull/651))

### Changed

- On a companion PC, Recommended setup uses the graphics card for Thinking first, then the voice (Chatterbox Turbo if it fits, otherwise Chatterbox Nano). Everything else runs on the processor or is off, so a PC left alone with no API key gets a local Thinking model first. Reconfigure follows the same priority: it frees the card first and sets up Thinking before anything else. ([#651](https://github.com/throndir2/Martlet/pull/651))

## [0.60.1] - 2026-10-08

### Fixed

- Recommended setup no longer counts a computer that isn't answering: turn your other computers off and it plans for the PC you use right away, instead of keeping their jobs for 10 minutes. A Thinking model a host names like `gemma4-e4b` is now known, so it moves to your PC properly. ([#649](https://github.com/throndir2/Martlet/pull/649))

## [0.60.0] - 2026-10-08

### Added
- Companion › Vision has a new **Image model** card and Companion › Listening an **Audio model** card. Keep the default (the same model as Thinking, which writes every reply), or choose Ollama on this PC, a cloud provider or server, a model app on this PC, or for pictures one of your computers. Each card says where pictures or recordings go, what the model is known to do and what is sent where. **Test vision** checks that a model reads a word drawn on your PC, **Test hearing** now works for the audio model too, and Martlet remembers what each model can do. ([#644](https://github.com/throndir2/Martlet/pull/644))
- While no online provider key is saved, the Recommended setup window, Home's Thinking problems and Companion › Thinking offer a free API key: **Get a free key** opens NVIDIA Build, and **Add your key** opens the right box with NVIDIA Build already chosen. The key keeps Martlet able to reply when your computers are offline or have no room for thinking, and the recommended setup plans again with it. ([#643](https://github.com/throndir2/Martlet/pull/643))
- Martlet can see with a separate image model: when you choose one, it describes your screen or camera in words for your Thinking model, so even a Thinking model that only reads text knows what you see. The image model describes the newest picture while you talk or type, a reply takes its description only when it's ready and never waits for it, and looks describe their picture first. The screen summary over time uses it too, and Companion › Vision and the talk window say where your pictures go. ([#640](https://github.com/throndir2/Martlet/pull/640))
- With an audio model of your own (Companion › Listening › Audio model), Martlet hears how you say things even when your Thinking model reads only text: the audio model listens beside speech-to-text and tells Thinking what the words miss (your tone, a laugh or a sigh, other voices, sounds around you), and it describes the sounds your PC plays too. Your recording never goes to Thinking then, and a reply never waits for the audio model. Never chosen, it hears you only while it runs in Ollama on this PC; anywhere else, tick **Let the audio model hear my voice**. ([#637](https://github.com/throndir2/Martlet/pull/637))
- Detect zones and Measure the eyes can use the image model you chose for pictures when no Thinking pool member can see, so they work with a Thinking model that reads only text. They pause while your conversation needs that model, and go on after. ([#635](https://github.com/throndir2/Martlet/pull/635), [#641](https://github.com/throndir2/Martlet/pull/641))
- Use the model app you already run on your PC for Thinking: Companion › Thinking › This PC › **A model app you already use** finds LM Studio, llama.cpp, KoboldCpp, Jan, vLLM, Lemonade, GPT4All, Docker Model Runner and other OpenAI-compatible apps, lists their models, tests one and switches to it, with no cloud wording and no key unless the app asks for one. Any other app works by typing the address it shows, such as `localhost:5001`, and Home tells you when the app stops answering. The macOS and Linux app gets **Find model apps**. ([#633](https://github.com/throndir2/Martlet/pull/633))

### Changed
- When no computer has room for your voice engine, Martlet now speaks with Chatterbox Nano: on a graphics card when one has room, otherwise on the processor. A hosted voice is used only when you saved its key. When no computer can run a voice, Home says Martlet can't speak yet and how to set up the Martlet host service, instead of going quiet. Your own voice engine comes back when a computer has room for it again. ([#645](https://github.com/throndir2/Martlet/pull/645))
- The Recommended setup window now shows at the top when no computer can do thinking, so Martlet can't reply, with a way to fix it. It says once which computers haven't answered, instead of on every change. ([#643](https://github.com/throndir2/Martlet/pull/643))
- Companion › Thinking › A cloud provider no longer warns that NVIDIA Build's free key may cost money, and the key box now says whose key it wants, such as **Your NVIDIA Build key**. ([#643](https://github.com/throndir2/Martlet/pull/643))
- The setup advisor's **Balanced** plan now keeps Thinking on your own computers, on the graphics card first or the processor when none is free, so a working Martlet needs no account or sign-up. Only **Smartest answers** uses an online model. ([#639](https://github.com/throndir2/Martlet/pull/639))
- Companion's side list now starts with the parts Martlet needs, in order: Thinking, Listening, Voice and Lip-sync. The Thinking pool, Singing, Pictures, Vision and Reading moved to a new **Optional extras** group, and each page says it's optional. ([#639](https://github.com/throndir2/Martlet/pull/639))
- Singing has its own page, Companion › Singing, instead of a card at the bottom of the Voice page. ([#639](https://github.com/throndir2/Martlet/pull/639))
- When no Thinking model is chosen, the Devices map now says Martlet needs one to answer you and suggests a free model on your own computers first. ([#639](https://github.com/throndir2/Martlet/pull/639))
- On a host PC, Home now shows **Switch to companion PC** at the top, so turning it back into your companion PC is one click away instead of a small link at the bottom. ([#636](https://github.com/throndir2/Martlet/pull/636))
- Companion › Thinking › This PC › Ollama now says that any Ollama model works, including a Hugging Face GGUF or a model you made yourself. ([#633](https://github.com/throndir2/Martlet/pull/633))

### Fixed
- Recommended setup now names the right listening model when it removes one (for example Whisper large-v3 turbo, not Whisper small), and when nothing can think it says that no free API key is saved. ([#647](https://github.com/throndir2/Martlet/pull/647))
- Test hearing no longer decides that a model can't hear when the server only says it doesn't have a model whose name mentions audio. ([#644](https://github.com/throndir2/Martlet/pull/644))
- Recommended setup now gives Thinking, the voice, listening and lip-sync your graphics card before optional extras such as Singing. A PC whose other computers are gone now gets its own Thinking model instead of keeping Singing, and with a free provider key saved, Thinking uses the free model when the card has no room for a local one. ([#638](https://github.com/throndir2/Martlet/pull/638))
- The welcome tour's suggested setup now names the right Companion page for each part's backup, instead of always saying Companion › Thinking. ([#639](https://github.com/throndir2/Martlet/pull/639))
- When always listening can't use your microphone, Martlet no longer opens and drops it every 5 seconds. It tries again after 1 second, then waits longer each time (up to 30 seconds), Home says how often it tries, and the log says why the microphone failed. ([#632](https://github.com/throndir2/Martlet/pull/632))

### Removed
- Windows voices are gone: Martlet speaks with a voice engine such as Chatterbox Nano instead, which also runs without a graphics card. If you used a Windows voice, pick a voice engine in Companion › Voice. Discord voice calls now speak only with a voice engine on one of your computers. ([#645](https://github.com/throndir2/Martlet/pull/645))
- Settings no longer has the old Diagnostics section. Its status report is still in Settings › Tools › Troubleshooting, and Martlet creates your settings by itself when you first save a choice. ([#642](https://github.com/throndir2/Martlet/pull/642))

## [0.59.0] - 2026-10-08

### Added
- VRM characters now get the same help as Live2D ones for parts that swing: a tail, wings or animal ears zone follows the character's own spring bones from root to tip, wherever they swing, and a tap on cat ears counts as the ears, not the hair. ([#630](https://github.com/throndir2/Martlet/pull/630))
- On a Live2D character, a tail, wings, animal ears or a ponytail that swings on its own now has a touch zone that follows all of it, from its root to its tip, wherever it swings. This works even for a tail that hangs hidden behind the legs. Martlet finds these parts in the character's own physics, with no AI. ([#629](https://github.com/throndir2/Martlet/pull/629))
- A touch zone can now have several areas: use **Add area** and **Remove area** on its line in Companion › Touch › Touch zones. **Show the zones on the character** draws the zones over your character as it moves. ([#629](https://github.com/throndir2/Martlet/pull/629))
- Share your hosts with friends right from the app: **Devices › Friends** lists each person and the hosts you share with them, with **Share** and **Stop sharing**, and **Sign-in from outside** says which sign-ins are your own computers and which are friends'. A friend's Martlet keeps a host you share under **Devices › Hosts shared with this PC** and uses it for thinking, listening, speaking, lip-sync and reading without ever joining your network, and your own work always comes first. ([#625](https://github.com/throndir2/Martlet/pull/625))
- Companion › Touch › Touch zones can now zoom the character's picture up to 8 times, with **Zoom in**, **Zoom out** and **Reset zoom** or Ctrl+mouse wheel, so you can move and resize small zones such as an eye or the mouth precisely. The boxes' lines and corners stay the same size, so each drag moves a box by a smaller step; zoomed in, drag the picture or scroll to look around it. ([#628](https://github.com/throndir2/Martlet/pull/628))
- **Detect zones** now also gives your character a touch zone for each thing special to it that your Thinking model sees, such as cat ears, a tail, wings, a halo, a hat, a hair bow or what it holds, named the way the model sees it (*Hair bow*). They react like the other extras, and **Detect again** keeps their names. A tail, wings or animal ears that a Live2D character's own files name get a zone too, even in the first guess. ([#627](https://github.com/throndir2/Martlet/pull/627))

### Changed
- Where touch zones overlap, such as where the groin meets a thigh, a touch or stroke there now counts on each of them, and Martlet hears them all: *They poked your groin and your left thigh once.* The best-matching zone still plays its reaction, and a hand held in front of the body still counts only as the hand. ([#626](https://github.com/throndir2/Martlet/pull/626))

### Fixed
- Touch zones stay on the right part of your character while it moves: a tap on its cheek, eye or mouth lands on that zone even while its head follows your mouse, nods or tilts. The whites of the eyes, the lashes and a wide blush now count as their eye or cheek instead of the hair, and a blush near an ear no longer counts as the ear. ([#624](https://github.com/throndir2/Martlet/pull/624))

## [0.58.0] - 2026-10-08

### Added
- Companion › Touch › Touch zones now shows your character and a first guess at its touch zones as soon as you open it, so clicks on the character react right away. Martlet places the guess from the character's own parts and shape, with no AI and nothing sent. Press **Detect zones** and your Thinking model then finds the zones, replacing the first guess as it goes. ([#621](https://github.com/throndir2/Martlet/pull/621))
- Character profiles now also remember, on each computer, where your character stands and how big it is, where it looks, and which touches stop it while it talks. Switch back to a profile, here or on another computer, and they come back; Companion › Profiles shows what each one keeps on this PC. ([#620](https://github.com/throndir2/Martlet/pull/620))
- A new starter voice, **Jenny (Dioco)**: Jenny is a professional Irish voice-over artist who recorded her voice for speech synthesis, so it sounds clean and natural. It joins your voice list on all your computers once; pick it with **Use** in Companion › Voice › Voices, or remove it like any other voice. ([#619](https://github.com/throndir2/Martlet/pull/619))
- Share a host with a friend: on a Linux host, `martlet-host owner-signin-allow ... --access friend` lets a friend sign in with their own account and use only that host's thinking, listening, speaking, lip-sync and reading, never your network, settings or anything else of yours. Your own requests always come first. ([#618](https://github.com/throndir2/Martlet/pull/618))
- New in Companion › Check-ins: **Saying the same things** reads what Martlet said in the last hour, and when, and when it keeps saying the same thing again and again, it reminds Martlet in its next reply to say something new. ([#613](https://github.com/throndir2/Martlet/pull/613))
- Settings › What this PC is for now lists **Your other computers**: each one says whether it is a companion PC or a host PC, with a button to make it a host PC or a companion PC again from where you are. It is the same switch the Devices map has, now easy to find. ([#612](https://github.com/throndir2/Martlet/pull/612))

### Changed
- **Jenny (Dioco)** is now the voice Martlet starts with. If Martlet spoke with the old default, *Annie (cute anime girl)*, it now speaks with Jenny on all your computers; a voice you chose yourself stays. ([#622](https://github.com/throndir2/Martlet/pull/622))
- **Reconfigure** in the recommended setup now runs as a background task. The review closes and a window shows each computer's progress and what its installs print. Hide it and find it again in **Background tasks**, or cancel the changes not made yet. ([#616](https://github.com/throndir2/Martlet/pull/616))
- More long jobs now show in **Background tasks**, with their progress and output, and you can cancel them there: downloading a Martlet update, Parakeet or cloudflared, updating your hosts, keeping this PC's host service on Martlet's version, and the installs and updates your other computers ask this PC to make. ([#616](https://github.com/throndir2/Martlet/pull/616))
- Martlet repeats itself less. Before it speaks up on its own about your screen, what your PC plays, a due reminder or finished work, it now looks at what it said in the last hour, and when, and says something again only when it's worth it. Edit how in Companion › Prompts › **What you said lately**; replies to what you say, type or touch never wait for it. ([#613](https://github.com/throndir2/Martlet/pull/613))
- When you make a host PC your companion PC again, from that PC or from another one, Martlet there brings the character back and starts listening and watching again as it was before it became a host, instead of waiting for Martlet to restart. ([#612](https://github.com/throndir2/Martlet/pull/612))
- When you switch another computer between companion and host PC, the computer you are at now tells you when it has switched. A host PC that has no host service yet now says so, instead of looking like it works for your other computers. ([#612](https://github.com/throndir2/Martlet/pull/612))

### Fixed
- A computer that signs in to one of your hosts from outside home can no longer take over the pairing of another of your computers there, or act as it. Your hosts also now let each signed-in computer do only what its sign-in allows. ([#618](https://github.com/throndir2/Martlet/pull/618))
- While Martlet starts Docker Desktop on a host PC, for example right after you make a companion PC a host PC, the host dashboard's Docker Desktop button is now greyed out and says **Starting Docker Desktop...** instead of offering to start it again. The step ticks as soon as Docker Desktop runs. ([#615](https://github.com/throndir2/Martlet/pull/615))
- **Manage memory** now shows what Martlet remembers the moment it opens, even while Martlet is answering you, and it keeps up on its own: facts Martlet remembers, changes or forgets while it's open show up right away, with no need to press **Refresh**. ([#614](https://github.com/throndir2/Martlet/pull/614))

### Removed
- The two "cute anime girl" voices are gone, because their raised pitch sounded artificial. If Martlet spoke with one, it switches to the voice you chose, or else to Jenny (Dioco), on all your computers. ([#617](https://github.com/throndir2/Martlet/pull/617))

## [0.57.0] - 2026-10-07

### Added
- Touch Martlet while it's talking and it stops to react, like when you talk over it, then decides whether to pick up where it left off. Choose which touches do this in Companion › Touch › Touch zones › **When you touch Martlet while it talks**: any touch, only intimate ones, or none. ([#610](https://github.com/throndir2/Martlet/pull/610))
- New in Companion › Replies: **Adult content (18+)**, off by default. Turned on, an adult character can flirt, be sexual and react to your touches explicitly, always in its own personality, and it takes repeated touches on an intimate part as deliberate. It never does anything sexual with a character under 18, and never in a Discord call. ([#610](https://github.com/throndir2/Martlet/pull/610))
- While Martlet hears what this PC plays, it now knows where each line comes from (a YouTube video in Chrome, a show or movie in Plex, a game, a voice chat in Discord, music in Spotify) and what you seem to be doing, even several things at once. So it no longer answers a video, a game or the people in your voice chat as if you had said it, and each *Playing on this PC* bubble says where it came from. ([#607](https://github.com/throndir2/Martlet/pull/607))
- New in Companion › Check-ins: every few minutes your Thinking pool checks on Martlet and fixes what a small model forgets. It turns off emotes a reply left on that no longer fit (a blush long after the compliment), takes the character's eyes back to their usual gaze, and reminds Martlet in its next reply of a promise it never kept ("I'll remind you in 10 minutes!") or when its replies drift out of character. Add your own check-ins too, such as suggesting a break after hours at the PC: choose what each one gets to know, how often it runs, and whether it reminds Martlet or Martlet brings it up on its own. Check-ins run only on your Thinking pool, never on your conversation model, so replies never wait for them. ([#604](https://github.com/throndir2/Martlet/pull/604))

### Changed
- When you touch or stroke Martlet without saying anything, it now always answers out loud with a sound, such as a gasp or a giggle, or with words, never only an emote. Its reaction also follows how its personality feels about being touched there, and it notices when you keep coming back to the same place. ([#610](https://github.com/throndir2/Martlet/pull/610))
- When you touch Martlet while you talk to it, it now decides what to react to first, so a sudden touch can come before its answer. ([#610](https://github.com/throndir2/Martlet/pull/610))
- The Character page in Companion now opens at once. Its settings are split into a group of their own, **How it looks**: **Character** (your character, its size, position and zoom, and your characters), **Speech bubbles**, **Emotes and motions**, **Eyes** (where it looks and where its eyes are) and **Touch** (touch zones and touch temperament). Long lists of emotes and touch zones fill in a moment after their page shows, so you never wait for them. ([#609](https://github.com/throndir2/Martlet/pull/609))
- Your character now gets fewer, clearer touch zones: **Detect zones** finds only the hair, eyes, ears, nose, mouth, neck, breasts, upper arms, forearms, stomach, hips, groin, thighs, calves and feet, with a left and a right one where there are two. Add any other zone with **Add zone** in Companion › Touch › Touch zones, and **Detect again** looks for it too, or leaves it where you put it when it can't find it. Press **Detect again** to move a character you set up before to the shorter list. ([#605](https://github.com/throndir2/Martlet/pull/605))
- The talk window now calls your character by its personality's name instead of "Martlet": in its title, at the top, in the message box and in the conversation, such as *You touched Ivy* or *Ivy, about your whole screen*. Your Thinking model sees that name on your character's own lines when it recalls earlier conversations or remembers things, and the songs it writes are for your character to sing. ([#606](https://github.com/throndir2/Martlet/pull/606))

### Fixed
- **Eyes › Watch the window you're using** now really watches what you do: your character's eyes follow where you move the mouse or type in the window you're using, instead of staring at the middle of it (which, for most windows, looked like staring off to one side). ([#608](https://github.com/throndir2/Martlet/pull/608))
- When your microphone picks up a video, show, game or music from your speakers while Martlet hears what this PC plays, Martlet no longer takes those words for yours: it shows them as a faded note and hears them only as what the PC played. ([#607](https://github.com/throndir2/Martlet/pull/607))

## [0.56.0] - 2026-10-07

### Added
- Martlet now knows which app you're using and whether it's full screen, like a game, a video or a slide show, and it talks about what you're doing or watching in it instead of your screen's menus, buttons, layout or setup. Hover over the talk window's vision line to see the app it tells your Thinking model about. Replies also stay quick when you switch windows. ([#600](https://github.com/throndir2/Martlet/pull/600))
- Martlet now reacts when you move your desktop character around, not only when you touch it: drag it somewhere new or to another monitor and it says something about it a moment later, unless you talk or type first. ([#597](https://github.com/throndir2/Martlet/pull/597))
- Your character now starts with ready-made combos that show several emotes at once: lovestruck, flustered, overheated, fuming, heartbroken, dozing, starstruck and shocked, and ahegao, which stays off until you turn it on. Change, turn off or remove any of them in Companion › Character › Emotes and motions › Combos; one you remove doesn't come back. A character can now have 24 combos. ([#596](https://github.com/throndir2/Martlet/pull/596))

### Changed
- Martlet no longer puts its own whisper effect on the Chatterbox voices, which could sound creepy. When a reply asks to whisper, Chatterbox Turbo and Nano whisper only the way the voice model itself does, which is now and then, and Chatterbox Original speaks normally. Update your host to get it. ([#599](https://github.com/throndir2/Martlet/pull/599))
- Martlet now notices touches on every touch zone by default, so your pokes, pats and strokes reach your Thinking model and Martlet answers them out loud, not only with a blush. Characters whose zones you never turned **Martlet notices** on for get it on once; turn it off for any zone in Companion › Character › Touch zones. ([#597](https://github.com/throndir2/Martlet/pull/597))
- When you touch Martlet without saying anything, it now always says a sentence or two about it, and its reaction builds when you keep going. When you touch it while you talk, it answers you first and then reacts to the touch too. ([#597](https://github.com/throndir2/Martlet/pull/597))
- A stroke across several parts of your character now reaches Martlet as one path in order, such as *They slowly stroked down from your chest over your stomach to your thighs*, with every part it crossed (not only the first three), which way it went, and left and right sides said together. ([#597](https://github.com/throndir2/Martlet/pull/597))

### Fixed
- Chat bubbles in the talk window are now only as wide as their words, without the empty space that a longer name, time or note next to them used to add. ([#602](https://github.com/throndir2/Martlet/pull/602))
- While Martlet speaks, its voice now always moves your character's mouth, even when an emote sets the mouth, such as an open mouth, `{starstruck}`, `{ahegao}` or one of the character's own expressions (a VRoid emotion, for example). About a second after Martlet stops talking, the emote's mouth comes back. ([#601](https://github.com/throndir2/Martlet/pull/601))
- Martlet's drawings on a Live2D character's face, such as the blush, tears, sweat drops and symbols over the head, now stay in place when she looks down at your mouse or turns and tilts her head, also on characters whose head moves through their own physics. ([#598](https://github.com/throndir2/Martlet/pull/598))

## [0.55.0] - 2026-10-07

### Added
- Emotes drawn over the eyes (heart eyes, star eyes, dizzy swirls) can now fit each character's own eyes. When a model doesn't say where its eyes are, your Thinking model measures them once in a close-up of the face, in the background and never while Martlet replies. Companion › Character › Touch zones › **Eyes** says where the eyes come from, and lets you measure them again or forget the measurement. ([#582](https://github.com/throndir2/Martlet/pull/582))
- Martlet now knows where each eye's iris is and how far the eye is open on every character, as it moves, so drawings on the eyes cover only the iris, fit each character and stay inside the eye. Live2D characters such as Hiyori give it from their own eye meshes, and VRM characters from their eye bones and VRoid eye meshes. ([#583](https://github.com/throndir2/Martlet/pull/583))
- Your character has eight more emotes it can show: heart eyes, star eyes, a tongue sticking out, drool, puffs of steam, dizzy swirls, a light bulb for a bright idea and a speechless "...". The hearts, stars and swirls sit inside the character's eyes. Turn each one on or off in Companion › Character › Emotes and motions; all but the light bulb and the dots stay on until the reply turns them off. ([#572](https://github.com/throndir2/Martlet/pull/572))
- New on Home: **Recommended setup**. Martlet works out the best use of all your computers: companion PCs stay light so your games keep their graphics card, each graphics card runs at most one language model, and the jobs are shared out between your computers. A review shows what changes on each computer and why, and **Reconfigure** sets them all up for you. When one of your computers comes back or stays away, Martlet checks again on its own and, if it finds a better setup, asks on Home. If you choose **Not now**, it doesn't ask again about that same setup on that PC. On a PC by itself, the button runs **Set it all up for me**. ([#589](https://github.com/throndir2/Martlet/pull/589))
- Make your own touch temperaments in Companion › Character › Touch temperament: give one a name, change how the character reacts to each part, and choose it under **Uses** for any persona. Changing it changes it for every persona that uses it, and **Re-decide from personality** brings a persona back to its own. ([#588](https://github.com/throndir2/Martlet/pull/588))
- Touch temperament has a new **Intimate parts** category for the lips, ears, neck, chest and breasts, waist, hips, groin, buttocks and inner thighs, and each category now lists the parts it covers, so every touch zone is in exactly one. Re-decide from personality to have your Thinking model decide it too. ([#588](https://github.com/throndir2/Martlet/pull/588))
- Your computers show **Configuring** while Martlet changes them: when the recommended setup is applied, Home says which computer it works on and which step ("Installing Chatterbox Turbo (2 of 4)"), on every one of your computers, and the Devices map shows that computer as Configuring. A computer also shows Configuring while a role is installed or removed on it from another computer. ([#578](https://github.com/throndir2/Martlet/pull/578))
- Your character can now blush at three strengths: a light blush, a deep blush and a fierce, deep red flush across the face. The deep blush and the flush stay on until a reply turns them off, and they show on every character, over its own blush when it has one. ([#570](https://github.com/throndir2/Martlet/pull/570))
- When one of your other Martlet computers stops answering for 30 seconds, Home on your companion PC says **Working with less** and what that computer did for you (the jobs that moved, the jobs that wait, the Speaking, Listening and Thinking pools that go on without it). When it answers again, Home says it's back. A single missed check says nothing. Settings › Your other computers has a new choice: **Look for a better setup when a computer is away for 10 minutes**, and the Devices map shows how long a computer hasn't answered. ([#573](https://github.com/throndir2/Martlet/pull/573))
- Your character can now keep several looks on at once: its eyes turned up, its mouth open and a blush stay on together, with hearts or other symbols drawn over them, until you or a reply turn each one off. Two new emotes help build such faces: **eyes_up** (only the eyes look up) and **mouth_open** (the mouth stays open, and still moves while Martlet speaks). ([#569](https://github.com/throndir2/Martlet/pull/569))
- You can now let clicks pass through your character, for example while you play a game: right-click it and choose **Let clicks pass through**. Its eyes still follow your mouse and it still talks, but your clicks reach the window under it. Since you can't right-click it then, turn it off with **Turn off click-through** on Home or in Companion › Character, or from Martlet's icon in the notification area. Martlet remembers it on this PC. ([#576](https://github.com/throndir2/Martlet/pull/576))
- Martlet can now speak with **ElevenLabs**: a copy of one of your saved voices that also does tones, such as whispering, happy, sad or angry, and sounds like laughs and sighs. Choose it in Companion › Voice › **A cloud provider**, with your own ElevenLabs API key. Martlet uploads the recording only after you tick the box and press **Clone and use ElevenLabs**, and requests cost money on your ElevenLabs account. Martlet follows ElevenLabs' documentation but hasn't been tried with a live ElevenLabs account yet. ([#575](https://github.com/throndir2/Martlet/pull/575))
- New in Companion › Character › Emotes and motions: **Combos**. Give one tag of your own to 2 to 6 of your character's emotes, motions and gestures, such as blush, hearts and nod, and when a reply uses it, your character does them all at once. Parts that stay on stay until the reply turns the combo off. **Try** shows a combo right there. Combos are yours, for each model, and are the same on all your computers. ([#568](https://github.com/throndir2/Martlet/pull/568))
- When the computer you listen with (or OpenAI) can't hear you, because it's off, its service stopped or its key is gone, a Parakeet model downloaded on this PC hears you instead, on your processor, so the conversation goes on and nothing is sent anywhere. For the next minute Martlet goes straight to Parakeet, then tries your choice again. Companion › Listening says which model stands in, or lets you download one without changing Listening. ([#559](https://github.com/throndir2/Martlet/pull/559))
- New in Companion › Thinking pool: **Backup Thinking**. When your Thinking model is slow to start a reply, Martlet can send the same message to a Thinking pool member you picked, and whichever starts answering first gives the reply, so a busy or loading model doesn't keep you waiting. Tick **May answer for the conversation** on the members it may use (ideally running the same model). It's off by default, and a paid cloud member is only asked when you tick it. ([#557](https://github.com/throndir2/Martlet/pull/557))
- New in Companion › Voice: **Quick sounds while Martlet thinks**. When a reply is slow to start, Martlet first says a quick "Mm," or "Hmm..." in its own voice, and the reply follows it, so you're never left in silence. It never plays when the reply is quick, never twice in one reply and at most once every 20 seconds. It's off by default; the sounds are made once with your voice and kept on this PC, and with a paid cloud voice only when you press **Make quick sounds now**. ([#555](https://github.com/throndir2/Martlet/pull/555))

### Changed
- VRM characters now stand naturally instead of in a stiff A-pose with flat, open hands: their arms hang close to the body with the elbows softly bent and the fingers curled. They also breathe (the shoulders rise and fall about 14 times a minute, more gently while they speak) and sway a little. ([#594](https://github.com/throndir2/Martlet/pull/594))
- **Detect zones** now reads the names a Live2D model gives its own parts, in any language (such as 头 for the head, 腿部 for the legs or 尾巴 for the tail), and tells the Thinking model where they are. Each close-up now holds its whole part, so the lower body always shows the hips and groin, and a box that lands clearly off its part, such as a neck on the midriff, a thigh on a boot or a tail on a ponytail, moves onto it. Models whose parts have no names work as before. ([#592](https://github.com/throndir2/Martlet/pull/592))
- Home's **Get a setup recommendation** link is now called **Plan a setup from scratch**, so it isn't confused with the new **Recommended setup** button. It still opens the setup advisor, which plans for computers you don't have yet. ([#589](https://github.com/throndir2/Martlet/pull/589))
- **Include intimate zones** in Touch zones now names every intimate part, the breasts and the groin too. ([#588](https://github.com/throndir2/Martlet/pull/588))
- The **Use built-in reactions** button in Touch temperament is now a choice under **Uses**, and the persona's own temperament stays there for when you choose it again. ([#588](https://github.com/throndir2/Martlet/pull/588))
- With **Include intimate zones** on, **Detect zones** now always marks the intimate zones: lips, ears, neck, chest, breasts, waist, hips, groin, buttocks and inner thighs. When the Thinking model misses or leaves one out, Martlet asks it again for just those zones, and otherwise works them out from the zones around them. ([#580](https://github.com/throndir2/Martlet/pull/580))
- The Thinking pool now follows which of your computers are on: when one goes offline, its slots leave the pool and background work goes to your other computers, and they come back by themselves when it answers again. Work waiting in line starts there at once, a think that was running there goes on on another computer, and when every computer is off, thinking longer and research use your conversation model if that is allowed. ([#577](https://github.com/throndir2/Martlet/pull/577))
- Chatterbox Nano is faster on a computer without a graphics card: it now starts talking before the whole sentence is ready, and it still sounds just as natural. ([#554](https://github.com/throndir2/Martlet/pull/554), [#560](https://github.com/throndir2/Martlet/pull/560), [#579](https://github.com/throndir2/Martlet/pull/579))
- When Martlet looks at your screen on its own, it now makes a quick guess at what you're doing, from what it sees, what you said and what it hears your PC play, and talks about that, like a friend glancing over. It no longer remarks on your computer itself, such as how many windows or monitors you have or how busy your setup looks. ([#574](https://github.com/throndir2/Martlet/pull/574))
- Your computers with a Thinking model now join the Thinking pool by themselves, so you no longer tick *Join the Thinking pool* when a computer comes online or gets a Thinking model. A computer's Thinking pool role joins with its slots, and its Ollama joins when it doesn't already do your Thinking. Untick **In the Thinking pool** on a computer in Companion › Thinking pool to keep it out; a computer that goes offline stays in the pool and its slots come back when it answers again. ([#571](https://github.com/throndir2/Martlet/pull/571))
- Companion › Character › **Touch temperament** is now one neat table: each part of the body is a line under the headings **Feels**, **Plays**, **Then**, **Lingers** and **Looks at mouse**, a line shows only the choices that apply, and the explanations are shorter. On a narrow window a line's choices wrap neatly beside its name instead of being cut off, and pointing at the status line shows the whole temperament in words. ([#567](https://github.com/throndir2/Martlet/pull/567))
- Web research is now on by default, so asking Martlet to look something up just works, and it researches much more thoroughly, the way a careful person would: up to 30 searches and 60 pages for each thing you ask, taking notes as it reads, with no time limit and no hourly limit (one at a time; Cancel in the talk window stops it). If the model stops partway, you still get its notes and sources. Turn it off in Companion › Thinking pool › **Web research**. ([#564](https://github.com/throndir2/Martlet/pull/564))
- Companion › Character's **Touch zones**, **Touch temperament** and **Emotes and motions** lists are tidier and take much less room: their boxes, choices and buttons are slimmer and line up, each label sits beside what it names, and a zone's box for your own words shows only while **Martlet notices** is on. Text boxes all over Martlet are slimmer too, as tall as the choices beside them. ([#565](https://github.com/throndir2/Martlet/pull/565))

### Fixed
- Touch zones on VRM characters now react to the part you touch: a cheek, the lips, an ear or the top of the head no longer counts as the face, and a calf no longer counts as the knee. **Detect zones** keeps the chest and stomach where they are, and taps on sleeves and gloves no longer miss. ([#593](https://github.com/throndir2/Martlet/pull/593))
- The character no longer stops responding after one slow command (such as taking its picture), which logged *Renderer message length is invalid.* for every move, word and click until Martlet restarted. Messages to and from the character now always go whole, a late answer is dropped, and a character that stops answering is ended cleanly so showing it again starts it fresh. When the character's window closes by itself, the log now says why instead of reporting an unexpected error. ([#587](https://github.com/throndir2/Martlet/pull/587))
- Martlet no longer opens a new network connection to your other computers for every check and sync: it keeps a few open and reuses them, so it puts much less load on this PC's network and on its own host service. The log now says a computer *stopped answering* only when it misses two checks in a row, once, and *answers again* once when it's back, instead of every time one check of a busy computer was slow. When this PC itself runs out of network connections, Martlet says so instead of saying your other computer didn't answer. ([#586](https://github.com/throndir2/Martlet/pull/586))
- Hiding the character or closing Martlet no longer shows *Martlet recovered from an unexpected error* when the character stops answering properly while it speaks or sings. Its mouth just stops moving for that sentence while Martlet's voice goes on, hiding it always works, and moving it no longer fills the log with long errors. ([#584](https://github.com/throndir2/Martlet/pull/584))
- Remembering what you said, screen summaries and the other background jobs now run on your other computers' Thinking pool role again. Martlet 0.54.0 sent those computers a request with two size limits that didn't agree, so they turned away every job as invalid and the work fell back to your conversation's Thinking model. If a computer ever turns Martlet's requests away as invalid (for example when only one of them was updated), Martlet now leaves it out of such jobs for 10 minutes instead of asking again and again, and the log says which computer to update. ([#581](https://github.com/throndir2/Martlet/pull/581))
- When the Thinking model, or the computer it runs on, stops answering part way through **Detect zones**, Martlet now stops at once, says so and keeps your zones from before, instead of replacing them with the few it found. ([#580](https://github.com/throndir2/Martlet/pull/580))
- **Detect zones** now shows its progress and the **Stop** button as soon as you click it. Before, they could stay hidden until it finished. ([#580](https://github.com/throndir2/Martlet/pull/580))
- **Detect zones** (and **Detect again**) now works while your character is hidden, and it never moves the character on your desktop: Martlet draws the character off screen in its rest pose for the picture, larger than before. The button is greyed out only when no model that can see pictures is set up, and a note under it says what to change. ([#566](https://github.com/throndir2/Martlet/pull/566))
- The touch zones picture now always shows your character's whole body, even a model whose legs or tail reach past its own frame. ([#566](https://github.com/throndir2/Martlet/pull/566))
- **Show character** no longer says *Couldn't show the character: A task was canceled.* when the Martlet host you picked for lip-sync doesn't answer. The character shows as before, Martlet tells you the host isn't ready, and the mouth follows Martlet's voice until the host answers again. ([#561](https://github.com/throndir2/Martlet/pull/561))

### Removed
- The **Advanced setup** link and the old setup window it opened are gone from Companion › Thinking, Voice and Listening, because each page already does everything that window did. API keys Martlet kept after you switched to another provider now show under **Keys from before** on the job's page, where you can remove them. ([#563](https://github.com/throndir2/Martlet/pull/563))
- The **Response style** sliders (Helpful, Sarcastic, Silly, Distracted and Playful teasing) are gone from Personality, along with their style prompts in Companion › Prompts. They only added one randomly picked style line to each message, and no character card has them. Your persona's own text, like a character card's, now sets how Martlet talks. Settings from older versions still load. ([#562](https://github.com/throndir2/Martlet/pull/562))

## [0.54.0] - 2026-10-07

### Added
- Martlet answers sooner when it isn't sure you've finished talking: it starts working on its reply in the short pause after you speak and keeps it to itself until you're done, so the reply is ready the moment your turn ends. If you keep talking, it drops that start and tries again at your next pause, and nothing is shown or said before your turn ends. It's on by default for Thinking models on your own computers and needs Parakeet on this PC for listening; to use it with paid cloud models too, turn on **Also for cloud models** in Companion › Listening › Start replies early. ([#550](https://github.com/throndir2/Martlet/pull/550))
- Martlet starts talking sooner: each spoken reply now begins with a few words, such as "Hmm, good question.", so its voice can start before the rest of the reply is written. Turn it off with **Short first sentence** in Companion › Replies, or change the words in Companion › Prompts. ([#549](https://github.com/throndir2/Martlet/pull/549))
- You and your character's personality now choose where its eyes go: follow your mouse, follow it only when it's near, look straight ahead, or watch the window you're using. Pick it in Companion › Character › Where the character looks or by right-clicking the character and opening **Eyes**; by default the personality decides. The character can also change where it looks in its replies, and a touch can make it look at your mouse for a moment, so a shy character can ignore your mouse and most touches, then blush and look at you when you touch it somewhere it cares about. Touch temperament also lets a part have no reaction at all, and Companion › Vision's gaze choice is now called *Glances at your screen*. ([#546](https://github.com/throndir2/Martlet/pull/546))
- Two more Chatterbox voices in Companion › Voice: **Chatterbox Original**, which says each sentence calmly or expressively as the conversation decides (set how calm and how expressive in Companion › Voice), and **Chatterbox Nano**, a small Chatterbox that laughs and sighs and runs even without a graphics card. ([#545](https://github.com/throndir2/Martlet/pull/545))
- Your conversation now comes first: as soon as you say real words to Martlet, or it starts to answer, background work on the computer your conversation uses (screen and sound summaries, remembering, thinking longer and research) waits or pauses, and picks up where it left off once the reply is spoken, so background work no longer slows your replies down. Paired Martlet hosts keep their graphics cards free for your reply too. Companion › Thinking pool says which members wait while you talk. ([#544](https://github.com/throndir2/Martlet/pull/544))
- On a Martlet host where the Thinking pool shares a graphics card with your conversation, replies, voices and listening now always go first: the host stops a Thinking pool job on that card the moment a reply needs it and runs it on another computer or later, so your character's voice no longer slows down while it thinks in the background. *martlet-host status* and the host's log say when the Thinking pool shares a card, and how to give it one of its own. ([#541](https://github.com/throndir2/Martlet/pull/541))
- Companion › Voice now shows a quick rundown for every voice: whether it copies your voice, laughs and sighs, and speaks with emotions, and whether it runs on your graphics card (with about how much of its memory it uses), on your processor or online. ([#539](https://github.com/throndir2/Martlet/pull/539))

### Changed
- Detect zones finds your character's touch zones much more accurately. It now shows your Thinking model the whole character at full size on a plain backdrop with a grid, then a close-up of the head, the body and the legs, and lets it check and correct its own numbered boxes until they're right. Martlet fits each box to the character, puts left and right back the right way round and points out boxes that look wrong. The picture always holds the whole character, even when you've zoomed in, and **Show the picture Thinking saw** and **Open the pictures** let you see exactly what was sent. Detection now takes several requests; **Stop** keeps what it found so far. ([#547](https://github.com/throndir2/Martlet/pull/547))
- Your character now shows its emotes more often and mixes them up: Martlet asks your Thinking model to use them freely and to try different ones. In Companion › Character › Emotes and motions, an empty *When to use* box now shows in grey the hint your Thinking model gets for that emote, and the card explains what the box is for. ([#540](https://github.com/throndir2/Martlet/pull/540))
- The Devices page now shows how much memory each job usually holds and the most it takes while it works hardest, such as *12-14 GB*: Thinking models hold the same amount all the time, while voices grow as they speak. A computer whose jobs usually fit but can run out when they're all busy now says it's *tight* instead of over-full, so you can tell what runs well from what probably won't. ([#538](https://github.com/throndir2/Martlet/pull/538))

### Fixed
- Martlet no longer says it can think about more things at once than your Thinking pool can run. One slot stays free for quick jobs such as screen summaries, so a pool with three slots thinks about two things at once and the rest wait in line. ([#551](https://github.com/throndir2/Martlet/pull/551))
- The blush and the symbols Martlet draws over your character's face now stay on the face while the head turns, tilts, sways, breathes or follows your mouse, and a turned head's far cheek gets a narrower blush that fades as it turns away. ([#543](https://github.com/throndir2/Martlet/pull/543))
- When you stop Martlet partway through a sentence, an emote it hadn't reached yet, such as a wink, no longer plays anyway. When Martlet pauses because you talk over it, the emote waits and plays at the same point in the speech once it goes on. ([#542](https://github.com/throndir2/Martlet/pull/542))
- Chatterbox Turbo is no longer listed with emotions it can't perform: of its tones, only whispering changes the voice. ([#539](https://github.com/throndir2/Martlet/pull/539))

## [0.53.0] - 2026-10-07

### Added
- Martlet knows what changed on your screen over the last moments: a new Screen summary over time setting (on by default) asks a Thinking model that sees, in the background, to sum it up for your next message, without making replies slower. ([#533](https://github.com/throndir2/Martlet/pull/533))
- With **Hear what this PC plays** on, Martlet now also notices the sound that isn't words: music and its mood, game and video sounds, laughter, applause and alarms. About every 10 seconds it describes what plays in one short line for its next reply. A Thinking pool model that can hear does it, or else a small sound tagger on your PC's processor. Turn it off with **Describe PC sounds** in Companion › Listening. The sound stays in memory only and is never saved. ([#532](https://github.com/throndir2/Martlet/pull/532))
- Martlet now hears when you've finished talking. A small model on your PC listens to how you end each sentence, so Martlet answers about half a second sooner when you're clearly done, and waits longer when you trail off mid-thought instead of cutting you off. It's on by default; turn off **Judge when I finish talking** in Companion › Listening to go back to the plain pause. ([#531](https://github.com/throndir2/Martlet/pull/531))
- Replies can now take in short notes from things happening around you, such as what your character shows, without ever waiting for them. ([#528](https://github.com/throndir2/Martlet/pull/528))
- Deep thinking is now the **Thinking pool**: one shared set of Thinking models for background work. Each of your computers joins only when you tick *Join the Thinking pool*, and each member has its own slot count. One slot always stays free for quick jobs. If the pool is empty, thinking longer and research use the conversation model. Your Deep thinking choices move over automatically. ([#529](https://github.com/throndir2/Martlet/pull/529))
- When you talk over Martlet, it now pauses at once and decides: words meant for it stop the reply and get an answer, while a quick "yeah", agreeing, laughing along, side talk or a TV lets the reply play on from exactly where it paused, with nothing lost. "Stop", "wait" or Martlet's name still stop it right away. Choose Pause and decide or Stop at once in Companion › Listening. ([#526](https://github.com/throndir2/Martlet/pull/526))
- With a Thinking pool set up, a pool model makes that call when you talk over Martlet, within 0.4 s; without one, or when it is busy, Martlet's own quick rules decide. ([#530](https://github.com/throndir2/Martlet/pull/530))
- With your desktop character's position locked, press and drag across it to stroke it: each part you cross reacts right away, the first one's emote stays on while you stroke, and on zones Martlet notices it hears how it went, such as *They slowly stroked your hair 4 times*. Martlet also hears when you move the character (even to another monitor), zoom in on it, pan, lock, hide or show it, with your next message. ([#527](https://github.com/throndir2/Martlet/pull/527))
- Martlet notices when you touch your desktop character: turn on *Martlet notices* for a zone in Companion › Character › Touch zones, and your pats, pokes and long presses add up into one line your Thinking model gets with what you say next, or, when you say nothing, in a short reaction of its own a moment after your last touch. The character still reacts right away, and talking or typing never waits for it. This replaces *Tell the character*; zones that had it on keep it. ([#525](https://github.com/throndir2/Martlet/pull/525))
- The personality now decides how the character reacts when you touch it: which emotes, gestures and face symbols play for each part of its body, from hating it to craving it, and what happens when you keep touching. Your Thinking model decides it in the background when you save a personality, and you can change it yourself in Companion › Character › Touch temperament. ([#523](https://github.com/throndir2/Martlet/pull/523))
- Adding the Thinking pool role to a computer whose only graphics card already runs a Thinking model now warns you first: both models share the card and each runs at about half speed. Martlet recommends one graphics card for each Thinking model. ([#521](https://github.com/throndir2/Martlet/pull/521))

### Changed
- Remembering what you said, naming your character's emotes, deciding its touch temperament and finding its touch zones now run on a free member of the Thinking pool, so they never slow down Martlet's replies. Without a pool member, the conversation model does them as before, but only after Martlet finishes speaking. ([#524](https://github.com/throndir2/Martlet/pull/524))
- Intimate touch zones now react by default. Turn off **Include intimate zones** in Companion › Character › Touch zones to leave them out. ([#522](https://github.com/throndir2/Martlet/pull/522))

### Fixed
- In Prepare this computer, the GPU power sliders fit the window again and show their handle, with each GPU's current, default and allowed limits on a line below. ([#536](https://github.com/throndir2/Martlet/pull/536))
- Adding a Linux computer as a host works again when its Docker has no buildx plugin, instead of stopping with "failed to parse platform". ([#534](https://github.com/throndir2/Martlet/pull/534))
- When adding a Linux computer stops, the message now says why, not only "check the output". ([#535](https://github.com/throndir2/Martlet/pull/535))
- Touch zone detection finds the groin zone when the vision model calls it the crotch, pelvis or between the legs. ([#519](https://github.com/throndir2/Martlet/pull/519))

## [0.52.0] - 2026-10-06

### Added
- Characters can show anime emote symbols drawn over their face: a sweat drop, an anger vein, floating hearts, sparkles, tears, gloom lines, a question or exclamation mark, a sleepy Zzz and music notes, on every Live2D model and VRM. Sweat, hearts, gloom and Zzz can stay on until they're turned off. ([#516](https://github.com/throndir2/Martlet/pull/516))
- Martlet reads the text on your screen while it watches: new text such as a score, "Victory" or a new message makes it more likely to look, and each look gets the words it read. Choose where it reads in the new Companion › Reading page: Windows' own text recognition on this PC (the default; fast and private), or Martlet's new Reading role (RapidOCR, often better with game fonts) on this PC or another of your computers, without a graphics card. Your own messages never wait for it. ([#515](https://github.com/throndir2/Martlet/pull/515))
- Every character can now blush: a model without a blush of its own gets a soft pink glow drawn on its cheeks, on Live2D and VRM alike. ([#511](https://github.com/throndir2/Martlet/pull/511))
- Companion › Character › Touch zones: your Thinking model (when it can see) marks where the character's head, cheeks, hands and other parts are in one picture, and each zone reacts its own way when you click it: a head pat, a blush, a flinch, optionally telling the character. Rename, move, resize or turn zones off; intimate zones stay off unless you turn them on. ([#514](https://github.com/throndir2/Martlet/pull/514))
- Emotes can now stay on, like a VTuber's toggle: glasses, a blush, an angry face, a pout or any look set to "Stays on" stays until Martlet writes {/tag} to turn it off, several at once, and Martlet knows what is showing so it can decide. Choose per emote in Companion › Character › Emotes and motions; Clear emotes on the character's right-click menu turns them all off. ([#513](https://github.com/throndir2/Martlet/pull/513))
- The character has new gestures for touches and moods: it can wink, pout, act shy, giggle, flinch, lean in for a head pat, look away, think, roll its eyes and get drowsy, on any Live2D or VRM model that supports them; pouting, shyness, looking away and drowsiness can also stay on until they're turned off. ([#512](https://github.com/throndir2/Martlet/pull/512))
- Click (without dragging) on your desktop character and it reacts: it plays the model's own tap motion for that part when it has one, or tilts its head, nods or looks surprised. ([#510](https://github.com/throndir2/Martlet/pull/510))
- The character now reacts to every sound and tone the voice makes: it laughs, sighs, gasps, coughs, hums, cries, glowers and more along with the voice, on any Live2D or VRM model, without lengthening replies. ([#507](https://github.com/throndir2/Martlet/pull/507))

### Fixed
- Drawn emotes such as the blush, gloom lines and hearts stay on a Live2D character's head when it tilts, instead of swinging off to the side. ([#517](https://github.com/throndir2/Martlet/pull/517))
- When you ask Martlet to whisper with the Chatterbox Turbo voice, it now really whispers. Chatterbox ignored its whisper tag, so Martlet now turns those sentences into a quiet, breathy whisper itself, and a whispered reply stays whispered from sentence to sentence. Update your host to get it. ([#509](https://github.com/throndir2/Martlet/pull/509))

## [0.51.0] - 2026-10-06

### Added
- A companion PC that also runs a host service now shows as "Companion PC + host" and checks that host service on Home the way a host PC does: a Host service tile, and when it isn't working (Docker Desktop stopped, not set up, stopped) one item that says which jobs stop and offers the step that fixes it. ([#504](https://github.com/throndir2/Martlet/pull/504))
- When a PC becomes a Martlet host (or Martlet starts on one), Martlet starts Docker Desktop by itself if this PC's host roles need it, starts every role and loads its model, so the first request from your other computers doesn't wait. It never installs anything by itself, and the host dashboard says what it did or why it couldn't. ([#502](https://github.com/throndir2/Martlet/pull/502))

### Changed
- More setup work runs at the same time: adding a role such as Ollama to this PC no longer waits while Martlet prepares its host service's update, Windows Firewall opens while Docker Desktop starts during host setup, and Update hosts updates all your hosts at once instead of one by one. ([#503](https://github.com/throndir2/Martlet/pull/503))

### Fixed
- Devices shows the graphics card, memory and processor of a host running in Docker Desktop again, after the host is updated. Since 0.18.0 such hosts reported no hardware. ([#505](https://github.com/throndir2/Martlet/pull/505))
- When a job runs on this PC's own host service, Home and Devices now call it "This PC's host service" instead of its host name as if it were another computer, and no longer offer to take lip-sync "back to this PC" when it's already there. ([#504](https://github.com/throndir2/Martlet/pull/504))

## [0.50.1] - 2026-10-06

### Fixed
- The Mac downloads are back in the release: the Mac app no longer fails to build because of a character folder. ([#500](https://github.com/throndir2/Martlet/pull/500))

## [0.50.0] - 2026-10-06

### Added
- ARM64 computers such as a Raspberry Pi 5, an NVIDIA DGX Spark or a Windows on Arm or Apple-silicon PC running Docker Desktop can now be Martlet hosts for thinking (Ollama) and listening (whisper or Parakeet). Voice cloning, singing, pictures and Audio2Face need an x86_64 PC with an NVIDIA GPU, so Martlet shows them as unavailable on these hosts and says why. ([#498](https://github.com/throndir2/Martlet/pull/498))
- Martlet installs and runs on Windows 11 on Arm PCs such as Snapdragon X laptops, through Windows' x64 emulation. Settings, Devices and Doctor show when Martlet is running emulated. Jobs that need an NVIDIA graphics card, which Windows on Arm can't use, are refused with that reason. The installer explains that Windows 10 on Arm isn't supported. Not yet tried on a real Arm PC. ([#493](https://github.com/throndir2/Martlet/pull/493))
- Martlet runs on a Linux desktop and on a Mac: talk by typing or by holding a key, with OpenAI or a model on the same computer (Ollama, LM Studio or Docker Model Runner), hear the replies, and see your VRM or Live2D character on your screen with its mouth moving as it speaks. It only offers what that computer can run, and settings brought from another computer that it can't run are refused with the reason. Not yet tried on a real Mac. ([#490](https://github.com/throndir2/Martlet/pull/490))
- Devices shows how much of each computer's graphics memory, memory, processor and disk every part of Martlet takes, what is left, and what else would fit there; a new card sums up what your computers cover and could still run, such as room for 2 more Deep thinking models. ([#487](https://github.com/throndir2/Martlet/pull/487))
- A new welcome wizard: start a new Martlet network or join yours (it finds your other computers), see what this PC has, choose whether free online services are OK, and get a suggested setup that shows how much of the graphics card, memory and processor each part uses. It walks you through a free NVIDIA key when Thinking goes online and sets lip-sync to follow the voice when the PC can't run Audio2Face. ([#489](https://github.com/throndir2/Martlet/pull/489))
- Martlet for Linux (coming with the Linux download) can show the character on top of your other windows with clicks passing through everywhere but the character, talk while you hold a push-to-talk key, keep your keys in the desktop's keyring (GNOME Keyring or KWallet), watch your screen after you start watching (on Wayland your desktop asks which screen to share each time), and start when you log in. It tells you plainly when your desktop limits one of these. Not yet tried on a real GNOME or KDE desktop. ([#488](https://github.com/throndir2/Martlet/pull/488))
- Google Gemini is a new Thinking provider, with the free Gemini 3.5 Flash-Lite filled in and steps for getting a free key. It can hear your voice once you allow it, and it makes a good "If Thinking fails" backup for NVIDIA Build. ([#486](https://github.com/throndir2/Martlet/pull/486))
- A new [Resource footprints](docs/RESOURCE_FOOTPRINTS.md) page lists how much graphics memory, memory, processor and disk each Thinking model, voice engine, listening model, lip-sync, singing and pictures option takes, and which numbers were measured. Setup recommendations use these numbers. ([#485](https://github.com/throndir2/Martlet/pull/485))
- Releases now include Linux (AppImage and .deb, x64 and arm64) and macOS (Apple silicon and Intel .dmg) downloads of the new desktop companion; the Mac app also carries the Mac host. ([#484](https://github.com/throndir2/Martlet/pull/484))
- A Mac can be a Martlet host: its `macos-setup` command runs Martlet's host in the background while you're logged in and lends your other computers the Mac's own Ollama and whisper.cpp, on its graphics chip on Apple silicon. Jobs that need an NVIDIA GPU are never offered there. Not yet tried on a real Mac. ([#482](https://github.com/throndir2/Martlet/pull/482))
- The Docker host image now also builds for Apple-silicon Macs and other ARM64 computers (CPU only). ([#482](https://github.com/throndir2/Martlet/pull/482))
- Martlet for Mac (coming with the Mac download) can float the character over full-screen games, talk while you hold a push-to-talk key in any app, keep your keys in the Mac's keychain, watch your screen after you allow it, start at login and show what it is doing in the menu bar. It also finds Ollama, LM Studio or Docker Model Runner running on the Mac. ([#480](https://github.com/throndir2/Martlet/pull/480))
- People keeps the last 5 clips of each voice you haven't named yet, so you can play them and hear who it is. They stay on this PC and are deleted once you name the voice. ([#477](https://github.com/throndir2/Martlet/pull/477))

### Changed
- When this PC joins your Martlet network, the welcome wizard's suggestions take into account what your other computers already run. ([#491](https://github.com/throndir2/Martlet/pull/491))
- People's voice cards are tidier: names are chips you can add, remove or pick as the one Martlet uses, and a voice can go by up to 40 names instead of 12. ([#477](https://github.com/throndir2/Martlet/pull/477))

### Fixed
- A host's Deep thinking role now recommends more thinks at once for Gemma 4 models, based on Martlet's measured model sizes: a 24 GB card beside Thinking's Gemma 4 E4B gets 4 Gemma 4 12B thinks instead of 1. ([#496](https://github.com/throndir2/Martlet/pull/496))
- The welcome wizard's suggestion now counts a Google Gemini key you already saved, including one on the If Thinking fails backup. ([#495](https://github.com/throndir2/Martlet/pull/495))
- Martlet knows NVIDIA Build's Nemotron 3 Nano Omni hears your voice and sees your screen, and says NVIDIA's retired Gemma 3n models need replacing. A new guide lists which cloud Thinking models hear, their free limits and how to get a key. ([#483](https://github.com/throndir2/Martlet/pull/483))
- A voice no longer learns "no name yet" or similar placeholders as its name, and ones learned by mistake are dropped. ([#477](https://github.com/throndir2/Martlet/pull/477))

## [0.49.0] - 2026-10-06

### Added
- **Set it all up for me** in the welcome tour and on Home sets up thinking, listening and a voice that fit your PC in one go: the smallest local model that hears you, your default microphone, and a voice on your graphics card when it has room (a Windows voice otherwise). ([#475](https://github.com/throndir2/Martlet/pull/475))

### Changed
- A brand-new README with screenshots, a feature tour and a Buy Me a Coffee link. ([#472](https://github.com/throndir2/Martlet/pull/472), [#473](https://github.com/throndir2/Martlet/pull/473))
- Every release now lists what changed in its notes. ([#474](https://github.com/throndir2/Martlet/pull/474))

## [0.48.0] - 2026-10-06

### Added
- Martlet can now answer WhatsApp through the Cloud API with guided setup. ([#463](https://github.com/throndir2/Martlet/pull/463))
- Discord camera view supports picture backgrounds, free character framing, and Martlet-changed backgrounds. ([#461](https://github.com/throndir2/Martlet/pull/461), [#466](https://github.com/throndir2/Martlet/pull/466), [#462](https://github.com/throndir2/Martlet/pull/462))

### Changed
- Shared work now respects busy computers, job order, exclusions, and dedicated hosts. ([#469](https://github.com/throndir2/Martlet/pull/469))
- Devices scales to more computers with a folding map and searchable list. ([#464](https://github.com/throndir2/Martlet/pull/464))
- Conversation history syncs edits and deletes across PC, Telegram, Discord, and WhatsApp. ([#467](https://github.com/throndir2/Martlet/pull/467))
- Outside access requires sign-in, pauses when sign-in disappears, and supports agent-managed hosts. ([#470](https://github.com/throndir2/Martlet/pull/470), [#465](https://github.com/throndir2/Martlet/pull/465))
- Sign-in cleans up signed-out computers while keeping outside addresses with pairings. ([#468](https://github.com/throndir2/Martlet/pull/468))
- Native Linux host setup supports sudo-rs and more systemd distributions and releases. ([#459](https://github.com/throndir2/Martlet/pull/459))

### Fixed
- Martlet starts more reliably when built on DrivePool volumes. ([#460](https://github.com/throndir2/Martlet/pull/460))

## [0.47.0] - 2026-10-06

### Fixed
- Host setup checks bare-metal prerequisites first and installs curl when needed. ([#457](https://github.com/throndir2/Martlet/pull/457), [#456](https://github.com/throndir2/Martlet/pull/456))

## [0.46.0] - 2026-10-06

### Added
- Martlet can talk with you from Telegram. ([#433](https://github.com/throndir2/Martlet/pull/433))
- Discord companion mode adds calls, voice, presence, friends, private calls, and camera presence. ([#447](https://github.com/throndir2/Martlet/pull/447), [#452](https://github.com/throndir2/Martlet/pull/452), [#450](https://github.com/throndir2/Martlet/pull/450))
- Discord setup now includes guided connection, invites, chat modes, owner controls, and a restored reply engine. ([#435](https://github.com/throndir2/Martlet/pull/435), [#444](https://github.com/throndir2/Martlet/pull/444), [#454](https://github.com/throndir2/Martlet/pull/454))
- Martlet can generate pictures through ComfyUI, a Pictures host role, OpenRouter, or NVIDIA Build. ([#445](https://github.com/throndir2/Martlet/pull/445))
- Web research can run as a background job and save report creations. ([#446](https://github.com/throndir2/Martlet/pull/446))
- Martlet can say scheduled reminders from the companion PC you used most recently. ([#428](https://github.com/throndir2/Martlet/pull/428))
- You can sign in from outside home and join by account, TOTP, invite, or attested join. ([#448](https://github.com/throndir2/Martlet/pull/448), [#430](https://github.com/throndir2/Martlet/pull/430))
- Martlet can sign in with Discord or Steam. ([#451](https://github.com/throndir2/Martlet/pull/451))

### Changed
- Deep thinking has no time or hourly limit and queues work to a free computer. ([#443](https://github.com/throndir2/Martlet/pull/443), [#453](https://github.com/throndir2/Martlet/pull/453))
- Background work can run in parallel across several computers. ([#440](https://github.com/throndir2/Martlet/pull/440))
- Every reply now gathers waiting PC audio, pictures, and finished work before answering. ([#442](https://github.com/throndir2/Martlet/pull/442))
- Conversation history keeps what Martlet sees. ([#439](https://github.com/throndir2/Martlet/pull/439))
- Outside host access shows reachable paths and security-audit information. ([#449](https://github.com/throndir2/Martlet/pull/449), [#438](https://github.com/throndir2/Martlet/pull/438))
- Character gestures are shared per rig, and voice-tag synonyms map to engine tags. ([#441](https://github.com/throndir2/Martlet/pull/441), [#437](https://github.com/throndir2/Martlet/pull/437))

## [0.45.0] - 2026-10-06

### Added
- Character profiles switch Martlet's look, voice, and personality together. ([#431](https://github.com/throndir2/Martlet/pull/431))
- Discord text chat supports channels, threads, DMs, /martlet, and /chatmode. ([#421](https://github.com/throndir2/Martlet/pull/421), [#426](https://github.com/throndir2/Martlet/pull/426))
- Outside host addresses are signed in the roster and used when home cannot answer. ([#429](https://github.com/throndir2/Martlet/pull/429))
- The talk window shows background tasks as a header chip with a task list. ([#427](https://github.com/throndir2/Martlet/pull/427))
- Any computer can make another companion PC a host PC. ([#419](https://github.com/throndir2/Martlet/pull/419))

### Changed
- Add a computer is streamlined into Connect and Roles steps. ([#420](https://github.com/throndir2/Martlet/pull/420))
- The network's job owner is honored on every computer, and host PCs stay awake while Martlet runs. ([#425](https://github.com/throndir2/Martlet/pull/425), [#418](https://github.com/throndir2/Martlet/pull/418))
- Several Deep thinking jobs can run at once on one host graphics card. ([#434](https://github.com/throndir2/Martlet/pull/434))
- Gateway protection adds lockouts, budgets, and a security audit. ([#422](https://github.com/throndir2/Martlet/pull/422))
- Behind-the-scenes improvements to releases, pinned packages, and research documentation. ([#436](https://github.com/throndir2/Martlet/pull/436), [#432](https://github.com/throndir2/Martlet/pull/432), [#424](https://github.com/throndir2/Martlet/pull/424))

## [0.44.0] - 2026-10-06

### Changed
- The Memory page is simpler, and Martlet can manage its memories. ([#415](https://github.com/throndir2/Martlet/pull/415))
- Each companion PC remembers the character's position and monitor. ([#414](https://github.com/throndir2/Martlet/pull/414))

### Fixed
- In-network Thinking uses local timing and explains time-limit failures plainly. ([#416](https://github.com/throndir2/Martlet/pull/416))

## [0.43.0] - 2026-10-05

### Changed
- Thinking can move to a host route that allows long thinks. ([#412](https://github.com/throndir2/Martlet/pull/412))
- The character's position can be unlocked from its right-click menu. ([#411](https://github.com/throndir2/Martlet/pull/411))

## [0.42.0] - 2026-10-05

### Added
- Parakeet can be offered for listening on another computer. ([#408](https://github.com/throndir2/Martlet/pull/408))
- Host roles can be configured from the companion PC, including the Deep thinking model. ([#407](https://github.com/throndir2/Martlet/pull/407))

### Changed
- Always listening stays on while Martlet speaks. ([#405](https://github.com/throndir2/Martlet/pull/405))
- The talk window is quieter: status details move to tooltips, and failed transcriptions stay hidden. ([#401](https://github.com/throndir2/Martlet/pull/401), [#403](https://github.com/throndir2/Martlet/pull/403))
- The speech bubble is drawn wholly in Martlet's palette. ([#402](https://github.com/throndir2/Martlet/pull/402))

### Fixed
- Martlet actually starts a song when it agrees to sing. ([#404](https://github.com/throndir2/Martlet/pull/404))
- Mis-bracketed emote and voice tags are accepted and noted under replies. ([#406](https://github.com/throndir2/Martlet/pull/406))
- Role downloads recover from container DNS outages, with cleaner setup output. ([#409](https://github.com/throndir2/Martlet/pull/409))

## [0.41.0] - 2026-10-05

### Added
- Martlet's speech and singing have a voice volume control. ([#395](https://github.com/throndir2/Martlet/pull/395))

### Changed
- A Windows host PC stays awake while its host service serves other computers. ([#397](https://github.com/throndir2/Martlet/pull/397))
- The Deep thinking host mirrors Thinking's suggested models. ([#399](https://github.com/throndir2/Martlet/pull/399))
- Vision remarks less about static screens and suggests Gemma 4 for Deep thinking hosts. ([#396](https://github.com/throndir2/Martlet/pull/396))

### Removed
- Removed Thinking-generated character palettes. ([#398](https://github.com/throndir2/Martlet/pull/398))

## [0.40.0] - 2026-10-05

### Added
- You can choose the graphics card for each host role on multi-GPU machines. ([#393](https://github.com/throndir2/Martlet/pull/393))
- Background tasks get their own page, and hidden run windows no longer cancel work. ([#391](https://github.com/throndir2/Martlet/pull/391))

### Changed
- Host setups can run side by side instead of one at a time. ([#392](https://github.com/throndir2/Martlet/pull/392))

## [0.39.0] - 2026-10-04

### Added
- A new PC can pair hosts and follow your Martlet network before Setup. ([#383](https://github.com/throndir2/Martlet/pull/383))

### Changed
- Vision is on by default and looks at the whole screen. ([#386](https://github.com/throndir2/Martlet/pull/386))
- Pairing codes no longer expire, and the pairing panel has Copy code. ([#385](https://github.com/throndir2/Martlet/pull/385))
- Setup steps can run side by side. ([#388](https://github.com/throndir2/Martlet/pull/388))
- This PC's host service updates in the background after app updates and restarts sooner. ([#387](https://github.com/throndir2/Martlet/pull/387))
- Add a computer controls now behave like the buttons they look like. ([#382](https://github.com/throndir2/Martlet/pull/382))
- Behind-the-scenes release maintenance. ([#390](https://github.com/throndir2/Martlet/pull/390), [#384](https://github.com/throndir2/Martlet/pull/384))

### Fixed
- Role image downloads resume after stalls or dropped connections. ([#389](https://github.com/throndir2/Martlet/pull/389))

## [0.38.1] - 2026-10-04

### Changed
- Chatterbox voice tags are grouped into non-word sounds and tones in the prompt. ([#379](https://github.com/throndir2/Martlet/pull/379))

### Fixed
- Docker Desktop setup can set up WSL when Docker says it is missing. ([#380](https://github.com/throndir2/Martlet/pull/380))

## [0.38.0] - 2026-10-04

### Changed
- Behind-the-scenes release maintenance. ([#378](https://github.com/throndir2/Martlet/pull/378))

### Fixed
- Chatterbox recovers from broken GPU contexts, keeps the model warm, and warns about shared GPUs. ([#377](https://github.com/throndir2/Martlet/pull/377))
- Docker Desktop setup recovers when virtualization is not detected on a fresh PC. ([#376](https://github.com/throndir2/Martlet/pull/376))

## [0.37.0] - 2026-10-04

### Added
- Singing installs and runs through the normal role flow, with SoulX by default and VevoSing optional. ([#374](https://github.com/throndir2/Martlet/pull/374))

### Changed
- Behind-the-scenes release maintenance. ([#375](https://github.com/throndir2/Martlet/pull/375))

## [0.36.0] - 2026-10-04

### Added
- The Deep thinking host role is available wherever roles are added. ([#372](https://github.com/throndir2/Martlet/pull/372))

### Changed
- Behind-the-scenes release maintenance. ([#373](https://github.com/throndir2/Martlet/pull/373))

## [0.35.0] - 2026-10-04

### Added
- Hosts without internet can receive native setup files from this PC over SSH. ([#369](https://github.com/throndir2/Martlet/pull/369))

### Changed
- Behind-the-scenes release maintenance. ([#371](https://github.com/throndir2/Martlet/pull/371))

## [0.34.0] - 2026-10-04

### Added
- Martlet can sing in conversation, play and stop songs, lip-sync them, and save creations. ([#358](https://github.com/throndir2/Martlet/pull/358), [#359](https://github.com/throndir2/Martlet/pull/359), [#360](https://github.com/throndir2/Martlet/pull/360), [#364](https://github.com/throndir2/Martlet/pull/364), [#355](https://github.com/throndir2/Martlet/pull/355))
- Martlet can mute or unmute from the character menu and tune chattiness about vision and PC audio. ([#350](https://github.com/throndir2/Martlet/pull/350), [#354](https://github.com/throndir2/Martlet/pull/354))
- Martlet can decide where the character looks. ([#349](https://github.com/throndir2/Martlet/pull/349))
- Every computer can share logs, with Save logs to share. ([#366](https://github.com/throndir2/Martlet/pull/366))

### Changed
- Martlet hears your voice by default while keeping it on this PC and can send it straight to hearing models. ([#368](https://github.com/throndir2/Martlet/pull/368), [#361](https://github.com/throndir2/Martlet/pull/361))
- Speech pauses only at sentence ends, not commas. ([#348](https://github.com/throndir2/Martlet/pull/348))
- Installed roles are reused and jobs keep answering while switching. ([#347](https://github.com/throndir2/Martlet/pull/347))
- Watching controls are separate from listening controls. ([#356](https://github.com/throndir2/Martlet/pull/356))
- Local listening offers three Parakeet models, with the fastest English model as default. ([#353](https://github.com/throndir2/Martlet/pull/353))
- Model suggestions favor qwen3.5:4b and show which local models can hear. ([#367](https://github.com/throndir2/Martlet/pull/367))
- Voice defaults now favor lower latency, with clearer Dia, F5, and Chatterbox comparisons. ([#363](https://github.com/throndir2/Martlet/pull/363), [#365](https://github.com/throndir2/Martlet/pull/365))
- Deep thinking is optional and always parallel. ([#352](https://github.com/throndir2/Martlet/pull/352))
- Behind-the-scenes improvements to validation and release maintenance. ([#362](https://github.com/throndir2/Martlet/pull/362), [#370](https://github.com/throndir2/Martlet/pull/370))

### Fixed
- Providers can be switched without removing set-aside keys first. ([#351](https://github.com/throndir2/Martlet/pull/351))

## [0.33.0] - 2026-10-04

### Added
- Martlet records conversations and can bring them back when mentioned. ([#346](https://github.com/throndir2/Martlet/pull/346))
- Memories belong to the recognized speaker, and voices can merge or rename from conversation. ([#344](https://github.com/throndir2/Martlet/pull/344), [#345](https://github.com/throndir2/Martlet/pull/345))

### Changed
- Slow remote voices keep talking instead of cutting replies short. ([#342](https://github.com/throndir2/Martlet/pull/342))
- Voice timing accounts for how long speech actually went on. ([#341](https://github.com/throndir2/Martlet/pull/341))
- Behind-the-scenes improvements to voice latency measurement and release maintenance. ([#340](https://github.com/throndir2/Martlet/pull/340), [#343](https://github.com/throndir2/Martlet/pull/343))

## [0.32.0] - 2026-10-03

### Added
- Deep thinking has its own parallel place, and Martlet can think longer in the background. ([#338](https://github.com/throndir2/Martlet/pull/338), [#335](https://github.com/throndir2/Martlet/pull/335))
- Martlet detects Thinking models that can hear or see and shares what it finds. ([#337](https://github.com/throndir2/Martlet/pull/337))
- Martlet can use an opt-in terminal while you talk. ([#329](https://github.com/throndir2/Martlet/pull/329))

### Changed
- App-wide settings and memories are shared across computers. ([#330](https://github.com/throndir2/Martlet/pull/330))
- Martlet colors itself after the character. ([#334](https://github.com/throndir2/Martlet/pull/334))
- Calmer barge-in listens for words, not just sounds. ([#333](https://github.com/throndir2/Martlet/pull/333))
- Behind-the-scenes improvements to validation, benchmarks, and release maintenance. ([#336](https://github.com/throndir2/Martlet/pull/336), [#331](https://github.com/throndir2/Martlet/pull/331), [#339](https://github.com/throndir2/Martlet/pull/339))

### Fixed
- The tray menu opens at the click point when Martlet's DPI differs from the monitor. ([#328](https://github.com/throndir2/Martlet/pull/328))

## [0.31.0] - 2026-10-03

### Changed
- Automatic updates install as soon as they are downloaded. ([#326](https://github.com/throndir2/Martlet/pull/326))
- Behind-the-scenes release maintenance. ([#327](https://github.com/throndir2/Martlet/pull/327))

## [0.30.0] - 2026-10-03

### Changed
- Thinking steps are off by default. ([#323](https://github.com/throndir2/Martlet/pull/323))
- Behind-the-scenes release maintenance. ([#325](https://github.com/throndir2/Martlet/pull/325))

### Fixed
- Menus use Martlet's palette without a white strip in Rose dark. ([#324](https://github.com/throndir2/Martlet/pull/324))

## [0.29.0] - 2026-10-03

### Added
- Character emotes and motions can be discovered, named, and triggered from replies or voice cues. ([#312](https://github.com/throndir2/Martlet/pull/312))
- Each persona can choose where its voice pauses between spoken pieces. ([#318](https://github.com/throndir2/Martlet/pull/318))
- Thinking steps can turn a reasoning model's hidden thinking off or on. ([#319](https://github.com/throndir2/Martlet/pull/319))

### Changed
- Chatterbox Turbo streams speech as it is made and has better latency logging. ([#321](https://github.com/throndir2/Martlet/pull/321), [#314](https://github.com/throndir2/Martlet/pull/314))
- Barge-in is opt-in and off by default. ([#313](https://github.com/throndir2/Martlet/pull/313))
- Thinking requests reuse caches and stop resending unchanged context. ([#315](https://github.com/throndir2/Martlet/pull/315))
- Behind-the-scenes release maintenance. ([#322](https://github.com/throndir2/Martlet/pull/322))

### Fixed
- The character's position can be locked, then unlocked only in Martlet's window. ([#316](https://github.com/throndir2/Martlet/pull/316))
- Host voice keeps working through routine clock steps. ([#317](https://github.com/throndir2/Martlet/pull/317))
- If a model refuses Thinking steps, Martlet asks again with the model's default. ([#320](https://github.com/throndir2/Martlet/pull/320))

## [0.28.0] - 2026-10-03

### Added
- Voice recognition is built into Martlet and on by default. ([#307](https://github.com/throndir2/Martlet/pull/307))
- Companion > Prompts shows estimated tokens. ([#304](https://github.com/throndir2/Martlet/pull/304))

### Changed
- Automatic and remotely requested updates install silently. ([#310](https://github.com/throndir2/Martlet/pull/310))
- Speech bubbles size to their whole text, and the character overlay has more side room. ([#305](https://github.com/throndir2/Martlet/pull/305), [#303](https://github.com/throndir2/Martlet/pull/303))
- Martlet hears only the output you hear through a virtual cable and leaves out speaker echo in the mic. ([#308](https://github.com/throndir2/Martlet/pull/308), [#300](https://github.com/throndir2/Martlet/pull/300))
- PC audio and self-voice playback no longer cause repeated replies or interruptions. ([#302](https://github.com/throndir2/Martlet/pull/302), [#306](https://github.com/throndir2/Martlet/pull/306))
- Behind-the-scenes release maintenance. ([#311](https://github.com/throndir2/Martlet/pull/311))

### Fixed
- Host voice audio stays within stream limits. ([#301](https://github.com/throndir2/Martlet/pull/301))
- If Martlet cannot close, it explains what is happening and offers to exit anyway. ([#309](https://github.com/throndir2/Martlet/pull/309))

## [0.27.0] - 2026-10-03

### Changed
- The Devices map names each computer by what it is, showing both device and host service. ([#298](https://github.com/throndir2/Martlet/pull/298))
- Behind-the-scenes release maintenance. ([#299](https://github.com/throndir2/Martlet/pull/299))

## [0.26.0] - 2026-10-03

### Added
- Martlet can hear what this PC plays. ([#294](https://github.com/throndir2/Martlet/pull/294))

### Changed
- Chatterbox Turbo supports GeForce RTX 50 series GPUs. ([#296](https://github.com/throndir2/Martlet/pull/296))
- Windows stay on screen, and popups are resizable. ([#295](https://github.com/throndir2/Martlet/pull/295))
- Copy buttons appear above read-only text boxes. ([#291](https://github.com/throndir2/Martlet/pull/291))
- Behind-the-scenes release maintenance. ([#297](https://github.com/throndir2/Martlet/pull/297))

### Fixed
- Speech bubbles show unsaid words, and host voice failures are logged. ([#293](https://github.com/throndir2/Martlet/pull/293))
- Voice failures no longer cut replies short. ([#292](https://github.com/throndir2/Martlet/pull/292))

## [0.25.0] - 2026-10-02

### Changed
- The Devices map shows every Martlet computer and can auto-allow paired PCs. ([#289](https://github.com/throndir2/Martlet/pull/289))
- Behind-the-scenes release maintenance. ([#290](https://github.com/throndir2/Martlet/pull/290))

## [0.24.0] - 2026-10-02

### Added
- Vision sees your whole screen, notices notifications, and can accompany messages. ([#285](https://github.com/throndir2/Martlet/pull/285))

### Changed
- Conversation context size is configurable, and Martlet detects model limits. ([#286](https://github.com/throndir2/Martlet/pull/286))
- Add a voice accepts Ogg Vorbis and Opus recordings. ([#287](https://github.com/throndir2/Martlet/pull/287))
- Behind-the-scenes release maintenance. ([#288](https://github.com/throndir2/Martlet/pull/288))

## [0.23.0] - 2026-10-02

### Added
- Downloaded Live2D models are supported, including VTube Studio folders, Unicode names, and 8K textures. ([#279](https://github.com/throndir2/Martlet/pull/279))
- Add a voice can use several recordings, accept most audio/video files, and fill transcripts with speech-to-text. ([#276](https://github.com/throndir2/Martlet/pull/276), [#281](https://github.com/throndir2/Martlet/pull/281), [#282](https://github.com/throndir2/Martlet/pull/282))
- Character models and settings can sync across Martlet computers. ([#272](https://github.com/throndir2/Martlet/pull/272), [#278](https://github.com/throndir2/Martlet/pull/278))
- Diagnostics shows This PC's host service logs, and copy buttons appear in outputs, dialogs, and errors. ([#268](https://github.com/throndir2/Martlet/pull/268), [#269](https://github.com/throndir2/Martlet/pull/269))

### Changed
- Companion > Voice is one voice engine list, and settings save automatically. ([#275](https://github.com/throndir2/Martlet/pull/275), [#274](https://github.com/throndir2/Martlet/pull/274))
- Hosts announce their Martlet release, follow updates, and expose Update available as a button. ([#271](https://github.com/throndir2/Martlet/pull/271), [#270](https://github.com/throndir2/Martlet/pull/270))
- The notification-area right-click menu stays open. ([#280](https://github.com/throndir2/Martlet/pull/280))
- Behind-the-scenes release maintenance. ([#283](https://github.com/throndir2/Martlet/pull/283))

### Fixed
- Setup and host updates avoid stranded runs and collisions. ([#267](https://github.com/throndir2/Martlet/pull/267), [#273](https://github.com/throndir2/Martlet/pull/273), [#277](https://github.com/throndir2/Martlet/pull/277))
- The character renderer works in release builds. ([#284](https://github.com/throndir2/Martlet/pull/284))

## [0.22.0] - 2026-10-02

### Added
- Voices are unified and shared with every Martlet node. ([#266](https://github.com/throndir2/Martlet/pull/266))
- The character right-click menu adds Hide, Talk, Open, Settings, and Keep on top. ([#259](https://github.com/throndir2/Martlet/pull/259))

### Changed
- Speaker echo reduction in the microphone is on by default. ([#265](https://github.com/throndir2/Martlet/pull/265))
- Host dashboard steps tick automatically when already working and show which computers use each host. ([#261](https://github.com/throndir2/Martlet/pull/261), [#262](https://github.com/throndir2/Martlet/pull/262))
- A Martlet host can avoid talking, listening, or showing the character. ([#258](https://github.com/throndir2/Martlet/pull/258))

### Fixed
- Host changes and updates wait or retry instead of colliding. ([#264](https://github.com/throndir2/Martlet/pull/264))
- Paired-host Ollama gets a context it accepts and room for hidden thinking. ([#257](https://github.com/throndir2/Martlet/pull/257))

## [0.21.0] - 2026-10-02

### Added
- Dia, Chatterbox Turbo, GPT-SoVITS, and XTTS-v2 are available as self-hosted voice engines. ([#255](https://github.com/throndir2/Martlet/pull/255), [#254](https://github.com/throndir2/Martlet/pull/254), [#253](https://github.com/throndir2/Martlet/pull/253), [#251](https://github.com/throndir2/Martlet/pull/251))
- Thinking models that hear can receive the user's recording. ([#249](https://github.com/throndir2/Martlet/pull/249))

### Changed
- Voice latency improves with overlapped synthesis, eager first clauses, and barge-in on by default. ([#250](https://github.com/throndir2/Martlet/pull/250))
- App update settings show the current version. ([#252](https://github.com/throndir2/Martlet/pull/252))

### Fixed
- F5 host avoids failed replies while the worker is busy or stopped. ([#248](https://github.com/throndir2/Martlet/pull/248))
- OpenRouter replies are no longer cut off by hidden reasoning or strict streams. ([#247](https://github.com/throndir2/Martlet/pull/247))

## [0.20.0] - 2026-10-02

### Added
- The talk window has a Refresh context button. ([#245](https://github.com/throndir2/Martlet/pull/245))
- Every internal LLM prompt is editable in Companion > Prompts. ([#244](https://github.com/throndir2/Martlet/pull/244))
- A prettier speech bubble follows the character and has position settings. ([#238](https://github.com/throndir2/Martlet/pull/238))

### Changed
- Conversation context is kept when settings change. ([#242](https://github.com/throndir2/Martlet/pull/242))
- Local model suggestions leave graphics memory for games and explain Ollama overfill. ([#243](https://github.com/throndir2/Martlet/pull/243))
- Vision backs off on rate limits, and Thinking can use a fallback provider. ([#239](https://github.com/throndir2/Martlet/pull/239))

### Fixed
- Ollama models recover when a Gemma 4 draft model fails to load. ([#241](https://github.com/throndir2/Martlet/pull/241))
- Memory update notes are fewer and clearer. ([#240](https://github.com/throndir2/Martlet/pull/240))

## [0.19.0] - 2026-10-02

### Added
- Martlet can listen from Home without the talk window, with an indicator and start-with-listening option. ([#236](https://github.com/throndir2/Martlet/pull/236))

### Changed
- Annie, a cute anime girl voice, is now the default F5 voice. ([#235](https://github.com/throndir2/Martlet/pull/235))

## [0.18.1] - 2026-10-02

### Fixed
- Host dashboard step buttons wrap under their detail. ([#233](https://github.com/throndir2/Martlet/pull/233))

## [0.18.0] - 2026-10-02

### Added
- Audio2Face can run as a local open-source GPU engine without an NGC key. ([#231](https://github.com/throndir2/Martlet/pull/231))
- Home Assistant can be installed, set up, shared, and managed from Martlet. ([#230](https://github.com/throndir2/Martlet/pull/230))
- Cute, high-pitched anime-style F5 voices are available by default. ([#229](https://github.com/throndir2/Martlet/pull/229))
- Other apps and scripts can call your hosts with API keys. ([#228](https://github.com/throndir2/Martlet/pull/228))
- Martlet can close to the notification area, show a tray menu, and start with Windows. ([#227](https://github.com/throndir2/Martlet/pull/227))
- The Martlet network can pair a host once for all computers and discover nearby computers without codes. ([#223](https://github.com/throndir2/Martlet/pull/223), [#221](https://github.com/throndir2/Martlet/pull/221))

### Changed
- The talk window has a Start listening button and no longer blocks Martlet. ([#225](https://github.com/throndir2/Martlet/pull/225))
- Lip-sync loudness is handled as a This PC method. ([#226](https://github.com/throndir2/Martlet/pull/226))

### Fixed
- Stale audio-device callbacks no longer crash Martlet, and native crashes are captured. ([#224](https://github.com/throndir2/Martlet/pull/224))
- Docker Desktop setup continues after installing WSL. ([#222](https://github.com/throndir2/Martlet/pull/222))

## [0.17.0] - 2026-10-01

### Added
- The MCP directory lets you browse, search, and install MCP servers. ([#218](https://github.com/throndir2/Martlet/pull/218), [#219](https://github.com/throndir2/Martlet/pull/219))
- Secure commands let Martlet computers update and manage hosts without SSH. ([#220](https://github.com/throndir2/Martlet/pull/220))

### Changed
- UI text is cleaner across the app. ([#217](https://github.com/throndir2/Martlet/pull/217))

## [0.16.2] - 2026-10-01

### Added
- A new computer can be paired with a short typed code. ([#216](https://github.com/throndir2/Martlet/pull/216))
- Diagnostics now has its own page and an optional log host. ([#213](https://github.com/throndir2/Martlet/pull/213))

### Changed
- Behind-the-scenes research for Home Assistant installation and management. ([#212](https://github.com/throndir2/Martlet/pull/212))

### Fixed
- A stuck character no longer blocks hiding, updating, or exiting. ([#214](https://github.com/throndir2/Martlet/pull/214))

## [0.16.1] - 2026-10-01

### Changed
- The default F5 voice is feminine, moving off the retired male sample. ([#211](https://github.com/throndir2/Martlet/pull/211))
- Speech bubbles are on by default, with settings on Companion > Character. ([#209](https://github.com/throndir2/Martlet/pull/209))
- Replies default to one or two sentences. ([#205](https://github.com/throndir2/Martlet/pull/205))
- Listening pauses only from its own button. ([#207](https://github.com/throndir2/Martlet/pull/207))

### Fixed
- Voice and host failures say which job failed. ([#210](https://github.com/throndir2/Martlet/pull/210))
- The talk box hint lines up with where typing starts. ([#208](https://github.com/throndir2/Martlet/pull/208))
- Host roles reconnect after their network holder is replaced. ([#206](https://github.com/throndir2/Martlet/pull/206))

## [0.16.0] - 2026-10-01

### Added
- Home health tiles show what needs attention. ([#203](https://github.com/throndir2/Martlet/pull/203))
- Vision shows when it is watching and keeps watching when Martlet's window is in front. ([#201](https://github.com/throndir2/Martlet/pull/201))
- Paired computers are listed when setting up another of your computers. ([#197](https://github.com/throndir2/Martlet/pull/197))

### Changed
- Always listening stays active and lets the model decide when to speak. ([#202](https://github.com/throndir2/Martlet/pull/202))
- Ollama on this PC can load its model, answer without a reply budget, and be tested during setup. ([#200](https://github.com/throndir2/Martlet/pull/200), [#198](https://github.com/throndir2/Martlet/pull/198))
- Host job assignments sync on all computers by default. ([#193](https://github.com/throndir2/Martlet/pull/193))
- Martlet keeps replies short by asking instead of cutting them off. ([#199](https://github.com/throndir2/Martlet/pull/199))

### Fixed
- Docker and F5 setup show progress and continue after virtualization restarts. ([#195](https://github.com/throndir2/Martlet/pull/195), [#194](https://github.com/throndir2/Martlet/pull/194))

## [0.15.0] - 2026-10-01

### Added
- Ten redistributable F5 voices are bundled, replacing the retired sample voice. ([#190](https://github.com/throndir2/Martlet/pull/190))

### Changed
- F5 setup drops a redundant confirmation, and the welcome tour no longer asks to get this PC ready. ([#189](https://github.com/throndir2/Martlet/pull/189), [#188](https://github.com/throndir2/Martlet/pull/188))

### Fixed
- Lip-sync no longer marks the default Audio2Face service in use when nothing answers. ([#191](https://github.com/throndir2/Martlet/pull/191))

## [0.14.1] - 2026-10-01

### Changed
- Martlet can install on Windows 10 2004+ and current Windows 11. ([#187](https://github.com/throndir2/Martlet/pull/187))

## [0.14.0] - 2026-10-01

### Changed
- Vision-capable Thinking models are preferred by default. ([#185](https://github.com/throndir2/Martlet/pull/185))
- The Devices page puts the map on top and lets jobs be configured per device. ([#184](https://github.com/throndir2/Martlet/pull/184))

## [0.13.1] - 2026-10-01

### Fixed
- Provider failures explain retired models and include local diagnostics and log tails. ([#182](https://github.com/throndir2/Martlet/pull/182))

## [0.13.0] - 2026-10-01

### Added
- F5 voices can be added and switched freely. ([#177](https://github.com/throndir2/Martlet/pull/177))
- Martlet can recognize people by voice, share the voice list, and use Parakeet listening. ([#178](https://github.com/throndir2/Martlet/pull/178))

### Changed
- Listening uses the default mic without a test and says when Audio2Face is not running. ([#181](https://github.com/throndir2/Martlet/pull/181))
- Behind-the-scenes validation policy updates. ([#176](https://github.com/throndir2/Martlet/pull/176))

### Fixed
- The character's head stays in view when zooming. ([#180](https://github.com/throndir2/Martlet/pull/180))

## [0.12.0] - 2026-10-01

### Added
- Martlet can call tools from MCP servers on this PC, with per-call approval. ([#170](https://github.com/throndir2/Martlet/pull/170), [#172](https://github.com/throndir2/Martlet/pull/172))
- Smart home requests can use Home Assistant MCP tools with confirmations for sensitive actions. ([#174](https://github.com/throndir2/Martlet/pull/174), [#165](https://github.com/throndir2/Martlet/pull/165), [#167](https://github.com/throndir2/Martlet/pull/167))
- SillyTavern-style lorebooks and SillyTavern/Chub character-card imports are supported. ([#169](https://github.com/throndir2/Martlet/pull/169), [#163](https://github.com/throndir2/Martlet/pull/163))
- Reply generation settings are configurable. ([#166](https://github.com/throndir2/Martlet/pull/166))

### Changed
- The talk window is just the conversation; settings moved to grouped Companion pages. ([#168](https://github.com/throndir2/Martlet/pull/168), [#164](https://github.com/throndir2/Martlet/pull/164))
- Audio devices are listed automatically, and default devices are assumed to work. ([#162](https://github.com/throndir2/Martlet/pull/162))
- The Martlet mascot replaces the in-app hearts. ([#161](https://github.com/throndir2/Martlet/pull/161))
- Behind-the-scenes improvements to desktop publishing. ([#175](https://github.com/throndir2/Martlet/pull/175), [#173](https://github.com/throndir2/Martlet/pull/173))

### Fixed
- Pairing no longer grabs lip-sync, and Martlet shows when Audio2Face is not installed. ([#160](https://github.com/throndir2/Martlet/pull/160))

## [0.11.0] - 2026-10-01

### Added
- Memory is on by default and remembers what is talked about. ([#158](https://github.com/throndir2/Martlet/pull/158))

### Changed
- Microphone and speaker setup is simpler, with clearer mic setup status. ([#157](https://github.com/throndir2/Martlet/pull/157))
- Whisper can use the GPU or CPU, and setup avoids console windows. ([#156](https://github.com/throndir2/Martlet/pull/156))
- Lip-sync setup now works like voice setup. ([#155](https://github.com/throndir2/Martlet/pull/155))

## [0.10.2] - 2026-10-01

### Changed
- The Voice tab puts Now first, with one-click F5 Docker or Windows voice and contextual cards. ([#154](https://github.com/throndir2/Martlet/pull/154))
- Home shows status, and Companion is the one place to change things. ([#152](https://github.com/throndir2/Martlet/pull/152))
- Behind-the-scenes policy updates. ([#151](https://github.com/throndir2/Martlet/pull/151))

### Fixed
- Abandoned host sessions no longer hold gateway state, and host run logs are available. ([#153](https://github.com/throndir2/Martlet/pull/153))

## [0.10.1] - 2026-10-01

### Fixed
- The gateway stays stopped while setup renews its approval. ([#149](https://github.com/throndir2/Martlet/pull/149))

## [0.10.0] - 2026-10-01

### Added
- F5 starts with a bundled sample voice and voice playback. ([#145](https://github.com/throndir2/Martlet/pull/145))
- This PC's host can be set up in one click without typed confirmations. ([#141](https://github.com/throndir2/Martlet/pull/141), [#142](https://github.com/throndir2/Martlet/pull/142))

### Changed
- Cloud provider setup shows saved API key state. ([#140](https://github.com/throndir2/Martlet/pull/140))
- Setup and user flows are simpler, with less required input and more automatic setup. ([#146](https://github.com/throndir2/Martlet/pull/146), [#144](https://github.com/throndir2/Martlet/pull/144))

### Fixed
- Hiding the avatar no longer crashes Martlet. ([#143](https://github.com/throndir2/Martlet/pull/143))
- Buttons no longer shift the layout when hovered. ([#139](https://github.com/throndir2/Martlet/pull/139))

### Removed
- The offline fixture demo was removed from the app. ([#147](https://github.com/throndir2/Martlet/pull/147))

## [0.9.0] - 2026-10-01

### Added
- Martlet keeps a local crash/error log and handles global exceptions. ([#137](https://github.com/throndir2/Martlet/pull/137))

### Changed
- Setup now has dedicated pages for thinking, voice, listening, and character. ([#138](https://github.com/throndir2/Martlet/pull/138))

## [0.8.2] - 2026-10-01

### Changed
- Automatic update checks remain selected by default. ([#136](https://github.com/throndir2/Martlet/pull/136))

## [0.8.1] - 2026-10-01

### Changed
- Updates check by default and prompt you to update now. ([#135](https://github.com/throndir2/Martlet/pull/135))
- The character overlay can zoom deeper and reset. ([#133](https://github.com/throndir2/Martlet/pull/133))
- Subtitles state full-screen game support like the character overlay. ([#132](https://github.com/throndir2/Martlet/pull/132))

## [0.7.0] - 2026-09-30

### Added
- Martlet can watch cameras, phones, and other video sources. ([#128](https://github.com/throndir2/Martlet/pull/128), [#129](https://github.com/throndir2/Martlet/pull/129))
- Optional speech bubbles and active-screen subtitles are available. ([#123](https://github.com/throndir2/Martlet/pull/123))
- Per-job Setup now includes prefilled provider defaults. ([#124](https://github.com/throndir2/Martlet/pull/124))

### Changed
- Character overlay controls moved into the main window, with reset position and anti-aliased rendering. ([#122](https://github.com/throndir2/Martlet/pull/122), [#120](https://github.com/throndir2/Martlet/pull/120))
- The Home character step uses more generic wording. ([#121](https://github.com/throndir2/Martlet/pull/121))
- Platform support now shows when a job stops working. ([#126](https://github.com/throndir2/Martlet/pull/126))
- Behind-the-scenes planning for Android, macOS, smart home, and free platform choices. ([#125](https://github.com/throndir2/Martlet/pull/125), [#127](https://github.com/throndir2/Martlet/pull/127), [#119](https://github.com/throndir2/Martlet/pull/119), [#130](https://github.com/throndir2/Martlet/pull/130))

## [0.6.0] - 2026-09-30

### Added
- Vision-aware commentary can watch your screen and know when to stay quiet. ([#114](https://github.com/throndir2/Martlet/pull/114))
- Martlet can see full-screen games through DXGI Desktop Duplication. ([#116](https://github.com/throndir2/Martlet/pull/116))
- Host work is shared across computers and desktops, with per-job failover. ([#115](https://github.com/throndir2/Martlet/pull/115))

### Changed
- Behind-the-scenes planning for iOS and iPadOS support. ([#117](https://github.com/throndir2/Martlet/pull/117))

## [0.5.0] - 2026-09-29

### Changed
- The installer is quicker, with setup questions moved to the first-run wizard. ([#112](https://github.com/throndir2/Martlet/pull/112))

## [0.4.0] - 2026-09-29

### Added
- Linux nodes can run Thinking with Ollama and share generic host roles. ([#106](https://github.com/throndir2/Martlet/pull/106))
- Linux hosts can listen with Whisper and speak with F5 voice. ([#109](https://github.com/throndir2/Martlet/pull/109), [#110](https://github.com/throndir2/Martlet/pull/110))
- Windows can drive Linux Martlet hosts over the in-app SSH runner. ([#107](https://github.com/throndir2/Martlet/pull/107))

### Changed
- Linux GPU computers can be prepared from the Devices map. ([#105](https://github.com/throndir2/Martlet/pull/105))

### Fixed
- SSH restarts and wakes wait on the target's own port. ([#108](https://github.com/throndir2/Martlet/pull/108))

## [0.3.0] - 2026-09-29

### Added
- Martlet can update itself from GitHub Releases and update hosts from the main PC. ([#103](https://github.com/throndir2/Martlet/pull/103))
- Hands-free voice activity and local Voice ID are available. ([#102](https://github.com/throndir2/Martlet/pull/102))
- The Devices map can hand node roles between paired hosts. ([#101](https://github.com/throndir2/Martlet/pull/101))

### Changed
- The installer includes recommended setup, and the setup planner detects GPU and host hardware. ([#99](https://github.com/throndir2/Martlet/pull/99), [#100](https://github.com/throndir2/Martlet/pull/100))

## [0.2.0] - 2026-09-29

### Added
- The installer and prerequisites tool can install required components. ([#96](https://github.com/throndir2/Martlet/pull/96))
- A setup advisor wizard gives goal-based recommendations. ([#95](https://github.com/throndir2/Martlet/pull/95))
- Hosts can be set up from the desktop by Docker, SSH, or native install. ([#89](https://github.com/throndir2/Martlet/pull/89), [#88](https://github.com/throndir2/Martlet/pull/88))
- Audio2Face can run on a separate host or be auto-detected locally with loudness fallback. ([#87](https://github.com/throndir2/Martlet/pull/87), [#86](https://github.com/throndir2/Martlet/pull/86))
- Hiyori ships as the default animated Live2D character. ([#85](https://github.com/throndir2/Martlet/pull/85))
- OpenRouter, NVIDIA Build, and OpenAI-compatible LLM endpoints are supported. ([#94](https://github.com/throndir2/Martlet/pull/94))

### Changed
- The desktop UI has a staged design with a Devices map and host dashboard. ([#97](https://github.com/throndir2/Martlet/pull/97))
- Setup layouts recommend fastest-response and unlimited-budget choices, including cloud LLM offload. ([#93](https://github.com/throndir2/Martlet/pull/93), [#92](https://github.com/throndir2/Martlet/pull/92))
- Behind-the-scenes documentation for releases and recommended setups. ([#84](https://github.com/throndir2/Martlet/pull/84), [#90](https://github.com/throndir2/Martlet/pull/90))

### Fixed
- Martlet can open the host port in Windows Firewall when this PC becomes a host. ([#91](https://github.com/throndir2/Martlet/pull/91))

## [0.1.0] - 2026-09-26

### Added
- First public release of Martlet.
- Voice conversation includes microphone capture, transcription, streaming replies, playback, voice activity, and Windows offline speech. ([#11](https://github.com/throndir2/Martlet/pull/11), [#7](https://github.com/throndir2/Martlet/pull/7), [#10](https://github.com/throndir2/Martlet/pull/10), [#9](https://github.com/throndir2/Martlet/pull/9), [#6](https://github.com/throndir2/Martlet/pull/6), [#14](https://github.com/throndir2/Martlet/pull/14), [#15](https://github.com/throndir2/Martlet/pull/15), [#18](https://github.com/throndir2/Martlet/pull/18), [#30](https://github.com/throndir2/Martlet/pull/30), [#73](https://github.com/throndir2/Martlet/pull/73), [#74](https://github.com/throndir2/Martlet/pull/74), [#75](https://github.com/throndir2/Martlet/pull/75), [#77](https://github.com/throndir2/Martlet/pull/77), [#8](https://github.com/throndir2/Martlet/pull/8), [#62](https://github.com/throndir2/Martlet/pull/62), [#52](https://github.com/throndir2/Martlet/pull/52), [#42](https://github.com/throndir2/Martlet/pull/42), [#38](https://github.com/throndir2/Martlet/pull/38))
- Animated Live2D/VRM overlays, themes, a bird icon, personas, runtime styles, Stop, and Escape-to-discard are included. ([#71](https://github.com/throndir2/Martlet/pull/71), [#63](https://github.com/throndir2/Martlet/pull/63), [#60](https://github.com/throndir2/Martlet/pull/60), [#61](https://github.com/throndir2/Martlet/pull/61), [#78](https://github.com/throndir2/Martlet/pull/78), [#59](https://github.com/throndir2/Martlet/pull/59))
- Local and cloud model foundations include Ollama, OpenRouter, NVIDIA Build, and configurable inference routes. ([#76](https://github.com/throndir2/Martlet/pull/76), [#29](https://github.com/throndir2/Martlet/pull/29), [#55](https://github.com/throndir2/Martlet/pull/55))
- Multi-computer host foundations include Linux gateways, durable pairings, host roles, local control, and artifact acquisition. ([#70](https://github.com/throndir2/Martlet/pull/70), [#69](https://github.com/throndir2/Martlet/pull/69), [#58](https://github.com/throndir2/Martlet/pull/58), [#68](https://github.com/throndir2/Martlet/pull/68), [#57](https://github.com/throndir2/Martlet/pull/57), [#56](https://github.com/throndir2/Martlet/pull/56), [#54](https://github.com/throndir2/Martlet/pull/54), [#53](https://github.com/throndir2/Martlet/pull/53), [#51](https://github.com/throndir2/Martlet/pull/51), [#50](https://github.com/throndir2/Martlet/pull/50), [#49](https://github.com/throndir2/Martlet/pull/49), [#47](https://github.com/throndir2/Martlet/pull/47), [#46](https://github.com/throndir2/Martlet/pull/46), [#45](https://github.com/throndir2/Martlet/pull/45), [#41](https://github.com/throndir2/Martlet/pull/41), [#36](https://github.com/throndir2/Martlet/pull/36), [#35](https://github.com/throndir2/Martlet/pull/35), [#34](https://github.com/throndir2/Martlet/pull/34), [#33](https://github.com/throndir2/Martlet/pull/33), [#28](https://github.com/throndir2/Martlet/pull/28), [#40](https://github.com/throndir2/Martlet/pull/40), [#43](https://github.com/throndir2/Martlet/pull/43), [#44](https://github.com/throndir2/Martlet/pull/44))
- Memory, diagnostics, support export, Doctor status, and local MCP desktop automation are available. ([#48](https://github.com/throndir2/Martlet/pull/48), [#39](https://github.com/throndir2/Martlet/pull/39), [#19](https://github.com/throndir2/Martlet/pull/19), [#17](https://github.com/throndir2/Martlet/pull/17), [#4](https://github.com/throndir2/Martlet/pull/4), [#65](https://github.com/throndir2/Martlet/pull/65))
- Setup, rollback, package verification, provenance, SBOM, and configuration restore foundations are included. ([#12](https://github.com/throndir2/Martlet/pull/12), [#20](https://github.com/throndir2/Martlet/pull/20), [#26](https://github.com/throndir2/Martlet/pull/26), [#27](https://github.com/throndir2/Martlet/pull/27), [#23](https://github.com/throndir2/Martlet/pull/23), [#24](https://github.com/throndir2/Martlet/pull/24), [#25](https://github.com/throndir2/Martlet/pull/25), [#31](https://github.com/throndir2/Martlet/pull/31))
- Martlet includes a participation policy for deciding when to speak. ([#16](https://github.com/throndir2/Martlet/pull/16))
- Release delivery includes opt-in update checks and normal unsigned versioned releases. ([#79](https://github.com/throndir2/Martlet/pull/79), [#82](https://github.com/throndir2/Martlet/pull/82), [#83](https://github.com/throndir2/Martlet/pull/83), [#81](https://github.com/throndir2/Martlet/pull/81))

### Changed
- Behind-the-scenes improvements to tests, packaging, validation policy, planning, docs, and repository foundations. ([#80](https://github.com/throndir2/Martlet/pull/80), [#72](https://github.com/throndir2/Martlet/pull/72), [#67](https://github.com/throndir2/Martlet/pull/67), [#64](https://github.com/throndir2/Martlet/pull/64), [#22](https://github.com/throndir2/Martlet/pull/22), [#13](https://github.com/throndir2/Martlet/pull/13), [#5](https://github.com/throndir2/Martlet/pull/5), [#3](https://github.com/throndir2/Martlet/pull/3), [#2](https://github.com/throndir2/Martlet/pull/2), [#1](https://github.com/throndir2/Martlet/pull/1), [#32](https://github.com/throndir2/Martlet/pull/32))

### Fixed
- MCP desktop automation stays attached during unrelated window churn. ([#66](https://github.com/throndir2/Martlet/pull/66))
