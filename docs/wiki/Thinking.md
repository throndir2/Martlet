# Thinking

Thinking is the model that reads your message and writes Martlet's reply. Run it free and private on your own PC, on another of your computers, or with a cloud AI you like.

![Companion Thinking](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion.png)

## Where it runs

Open **Companion › Thinking** and choose under **Where it runs**:

- **This PC (recommended)**: private, free and with no account. Under **Model app**, choose **Ollama**: Martlet installs it and downloads a model that fits your graphics card. Any Ollama model works, also a Hugging Face GGUF or a model you made. Or choose a model app you already run: Martlet finds **LM Studio**, **llama.cpp**, **KoboldCpp**, **Jan**, **vLLM**, **Lemonade**, **GPT4All** and **Docker Model Runner**.
- **Another of your computers**: a paired computer with a stronger graphics card thinks for you. See [Multiple Computers](Multiple-Computers).
- **A cloud provider**: **OpenAI**, **Google Gemini**, **OpenRouter**, **NVIDIA Build** or any OpenAI-compatible service. Your messages leave this PC, and some providers charge. Martlet walks you through a free key for NVIDIA Build or Google Gemini.

**Recommended setup** on Home picks a model that fits all your computers and tells you when a better one is available.

### Ollama on this PC

Ollama runs the model on your own PC. Your messages stay on this PC, you need no API key, and there is no cost for each request.

1. Open **Companion › Thinking › This PC**. The **Ollama on this PC** card shows your graphics card and the recommended model: the fastest one that fits. It also names the largest model that fits with room for a game and Martlet's character. Bigger models are smarter but slower.
2. Choose a model. To type any Ollama name, choose **Another Ollama model**.
3. If Ollama isn't installed, choose **Install Ollama and use it**. Martlet installs Ollama, downloads the model, switches Thinking to it and tests it.
4. If Ollama is installed, choose **Check Ollama** to see which models are downloaded, then **Download model**.
5. Choose **Test model** (see [Testing a model](#testing-a-model)), then **Use Ollama on this PC**.

When you choose **Use Ollama on this PC** for a model that isn't downloaded yet, Martlet asks first. Then it downloads and loads the model in a window that shows each step. Thinking keeps using the model it has until the new one is ready, so a model that won't download or load leaves Thinking as it was. If you choose another model meanwhile, Thinking switches to the one you chose last. The model's own license applies.

**Use a model file you downloaded...** adds a GGUF file (from Hugging Face, LM Studio's folder or anywhere else) to Ollama and switches Thinking to it. An mmproj file in the same folder comes too, so the model can see. Ollama keeps its own copy, which takes more disk space, so you can delete your file afterwards.

If Ollama doesn't start or keeps stopping, see [Ollama on this PC doesn't answer](Troubleshooting#ollama-on-this-pc-doesnt-answer).

### Free cloud keys

When no cloud provider has a saved key, **Companion › Thinking**, Home and **Recommended setup** show **Add a free API key**. The key from NVIDIA Build costs nothing, and it lets Martlet reply when your computers are offline or have no room for thinking.

1. Choose **Get a free key**. NVIDIA Build's key page opens in your browser. If the browser doesn't open, go to https://build.nvidia.com/settings/api-keys yourself.
2. Sign in, create a key and copy it.
3. Choose **Add your key** and paste the key.

Where one of your computers can already think, the key goes to **If Thinking fails**, because a local model starts to answer sooner. Where no computer has room for Thinking, the key goes to Thinking itself, and Martlet thinks online. When Martlet uses the key, NVIDIA gets and logs what you say. Martlet keeps every key in Windows Credential Manager, never in its settings files.

## Use a model app you already run

1. Start the app's local server. In LM Studio, use **Developer › Start server**.
2. Open **Companion › Thinking** and choose **This PC (recommended)**.
3. Under **Model app**, choose your app. For an app on another port, choose **Another app, by its address** and type what the app shows, such as `localhost:5001`.
4. Choose the model, press **Test model**, then **Use**.

**Look again** looks for apps again. Martlet looks only on this PC and starts or installs nothing.

## Choosing and checking a model

- Martlet's own model list updates every day, so new models show up without a Martlet update. Each model says what it takes in (text, pictures, sound and video), how smart it is and what it needs.
- **Companion › Thinking** says whether your model hears, sees, takes video and calls tools.
- **Test model**, **Test tools**, **Test vision** and **Test hearing** check a model before you rely on it.
- When a service retires your model, Home offers a replacement with a **Use** button.
- A cloud model hears your voice only when you tick **Let Thinking hear my voice** on **Companion › Listening**.

### Model facts

Each model in a list shows facts. Point at a fact to see what it means.

- **Smartness**: how smart the model is among the models that Martlet's model catalog scores. Martlet shows words, or LMArena's rating with its credit.
- **Takes**: what the model takes in (text, pictures, sound and video), as Martlet's model catalog says. When the catalog doesn't know, Martlet counts it as no.
- **Writes**: about how many words a second it writes. Martlet estimates this from the bytes the model reads for each word and the memory speed of the graphics card. Real speed can be lower.
- **First sentence**: about how long until the model writes its first sentence. For an image, audio or reading model, the speed says how long one description or read takes; a reply never waits for it.
- **Graphics memory** and **Fits this PC**: how much graphics memory the model holds, and whether your card holds it with room for a game and Martlet's character. Without a graphics card, a model runs on the processor, much slower.
- **Context**: how much of the conversation the model reads at once. The graphics memory figure is for 8,192 tokens; a longer context takes more. **Test model** shows how much context Ollama gives the model. To give it more, raise Ollama's context length setting.
- **On a host**: Martlet's host roles install only the models that they list. A newer Martlet on the host can offer more.
- **API key**: what you need before a cloud model works. Martlet keeps a key in Windows Credential Manager.

### Testing a model

**Test model** on **Companion › Thinking › This PC** starts Ollama when it isn't running, checks that the model is downloaded, loads it and asks for a short reply. It shows each step in a window, with Ollama's own error text. Nothing leaves this PC. The result says one of these:

- *works on this PC*: how long it took to load and to answer, and what else it supports (vision, tools).
- *works once loaded, but loading took ...*: the model is fine, but the first reply after a load can time out. Try again after the model finishes loading.
- *works, but starts too slowly for Martlet*: Martlet waits a limited time for the first words. Choose a smaller model.

**Test tools**, **Test vision** and **Test hearing** each send the model one small request: a made-up tool, a picture of one word drawn on this PC, or a recording of one word made by Windows speech. They never send what you said, typed or showed. For a cloud model, the request uses your API key and may cost a little, so Martlet asks first. Choosing a model already asks its server what the model takes, when the server says.

**Companion › Thinking** shows what Martlet knows about the Thinking model: whether it hears recordings, sees pictures, takes video and calls tools, and where each answer came from. *By its name* means Martlet guesses from the model's name; a test makes sure. Martlet also remembers when a model refuses a recording or a picture.

## When Thinking fails or is slow

- **If Thinking fails** on **Companion › Thinking** is an optional backup model. It answers once when the main model fails before any words arrive: an error, a rate limit or no answer in 15 seconds. A good pair is NVIDIA Build first and Google Gemini as the backup.
  - Screen and camera looks use the backup too, so choose a model that can see images.
  - When the backup answers, your messages, the recent conversation and any screen or camera picture of that reply go to it. Its requests can cost money. You tick a box to agree before you save it.
  - A backup that is the same as Thinking adds nothing, so Martlet asks you to choose another model or provider.
  - Paste the backup's API key in its key box. Martlet keeps it in Windows Credential Manager. Leave the box empty to keep a saved key, or to use Thinking's key for the same provider.
  - With no backup, Martlet says that Thinking failed, and you try again.
- **Backup Thinking** asks a second model when the first one is slow to start. It is off by default. On **Companion › Thinking pool › Backup for slow replies**, tick **Ask a machine in the pool too when a reply is slow to start**. Then tick **Backup for slow replies** on the machines it may use, ideally with the same model. The first one to start gives the reply.
- Do you play games on a PC? Then while a game runs there, Martlet moves her thinking off that PC's graphics card to another computer. The model stays loaded while the game leaves room. When the game ends, she moves it back. When no other place can do live Thinking, it stays on that PC, and Martlet unloads it only when the game needs the graphics card's memory. Ollama then runs it on the processor.
- Ollama on this PC doesn't answer or is slow to load? See [Ollama on this PC doesn't answer](Troubleshooting#ollama-on-this-pc-doesnt-answer).

## Reply settings

**Companion › Replies** changes how the Thinking model writes replies. Leave a box empty to use the model's default. Changes save as you type. An open conversation uses them after you reload it. Each setting has a **?** that says what it does.

| Setting | What it does |
| --- | --- |
| **Thinking steps** | Off (the default) lets replies start sooner. On lets a reasoning model think step by step first. That can help with hard questions, but the first word comes seconds later. A model that always thinks refuses Off, and Martlet then uses the model's own default. |
| **Short first sentence** | On (the default): each spoken reply starts with a few quick words, such as "Oh, nice one!". Her voice says the first sentence as soon as it is written, so a short one starts her talking sooner. The **Short first sentence** prompt on **Prompts** asks for it. |
| **Adult content** | Off (the default). On lets Martlet flirt, be sexual and react explicitly, true to her personality, when her character is an adult. She never does with a character under 18, and never in a Discord call, where others can hear. Touches on intimate parts then count as sexual, and when you keep touching one, she takes it as deliberate and reacts more and more. Many cloud models refuse such content; models you run yourself usually don't. The words she is given are the **Adult content** prompt on **Prompts**. Choose On only if you are 18 or older. |
| **Personal content** | What she may do with personal things she sees on your screen or camera. **Keep private** (the default): she never reads out messages, chats, emails or numbers, and says only who a message is from. **Read my messages**: she may read, quote and talk about your messages, chats, chat history, emails and notifications. **Everything**: she also sees password managers, private browsing, authenticator and banking windows, which she otherwise greys out. What she sees goes to your Thinking or image model. The words she is given are the **Personal content** prompts on **Prompts**. |
| **Max reply length** | Stops unusually long replies, up to 32,768 tokens. A reply that reaches it can end early. |
| **Temperature** | 0-2. Higher is more varied, lower is more predictable. |
| **Top P**, **Top K**, **Min P** | How many likely words the model considers. A Top P of 1 turns it off. |
| **Repeat**, **frequency** and **presence penalty** | Discourage repeated words and push replies toward new topics. |
| **Context size** | How much the model keeps in mind: the persona, lore, memory, the conversation so far and the reply. Older exchanges are left out when a conversation outgrows it. A larger size sends more with each reply, which costs more on paid providers. See [Context](Talking-to-Martlet#context). |

Under each setting, a line says whether your Thinking model uses it:

- **Used by ...**: the model uses it. With a Martlet host, update the host to your Martlet version if replies fail after a change.
- **May not work with ...**: it depends on the server. Empty the box if replies fail.
- **Not used** or **Not supported**: the model ignores it.

**Thinking longer** on the same page opens the **Thinking pool**. There, Martlet can think a hard task through in the background while you keep talking.

To see everything Martlet sent for a reply, click **What was sent** under that reply in the talk window.

## Thinking pool

**Companion › Thinking pool** is a list of machines that do Martlet's background work: thinking longer, web research, screen and sound summaries, remembering, check-ins and other helpers. Your conversation keeps its own Thinking model at full speed, and it always comes first: background work waits while you talk.

- **Machines**: each graphics card, model in Ollama on this PC or cloud provider is a row with an **On** box and its model. **Add a machine** adds one. Your paired computers with a Thinking model join by themselves. Untick **On** to keep one out.
- **Several graphics cards**: a computer with two or more NVIDIA graphics cards can run a pool model on each card. Press **Add a model** on the row of the free card.
- **Smarts**: each machine is **Fast**, **Standard** or **Smart**. Martlet guesses it from the model name, and you can change it.
- **Quick jobs** and **Long jobs**: keep a machine for quick checks while you talk, or for long thinking and research.
- **Runs on**: choose where each kind of job runs: any machine, **Prefer smart**, **Smart only** or the machines you pick.
- **May receive pictures and recordings**: a cloud machine gets screenshots and recordings only when you tick this.
- **Busy pool**: choose whether more important jobs may stop less important ones, and how often a failed job tries again.

Ask Martlet to take her time and she thinks it over in the background while you talk, then brings it up when it's ready. The **Thinking requests** page in the side menu shows every job. Select one to see what it was for, where it ran, how long it took, and what it sent and answered.

### Machines in the pool

Each row in **Machines** is one machine: one graphics card on one of your computers, a model in Ollama on this PC, or a cloud provider or server.

- **On**: untick it to stop using the machine. The machine keeps its settings and takes no jobs until you tick **On** again. With no machine on, the pool is off.
- **Quick jobs** are the judges (are you done talking, did you interrupt) and the screen and sound summaries. They are short and need a fast answer.
- **Long jobs** are thinking longer, research, a song's lyrics, touch zones, remembering, naming and check-ins. They can run for minutes.
- **Smarts**: Martlet guesses it from the model name. Small models (about 4B or less) are **Fast**. Large models (about 26B or more, and big cloud models) are **Smart**. **Runs on** uses it.
- **Slots**: how many jobs the machine runs at once. Each slot after the first needs room on the graphics card for the context of one more job.
- **Model**: on a paired computer, a new model or slot count is set up there, and a run window shows the download. The old model keeps working until the new one is ready. For a cloud provider or server, type the model ID and press Enter (or leave the box) to save it. A model in Ollama on this PC can't be Thinking's own model, because that model can't think something over while it answers you.
- The badges under a machine: **Waits while you talk** means that it shares the conversation's computer, so it starts no new pool work while you talk. **Costs money** is a paid cloud provider. **Offline** means that its computer doesn't answer now. **Limiting requests** means that its provider asked Martlet to slow down, so it gets fewer jobs for a while and its jobs wait instead of failing.

**The conversation** is the first row. It is your own Thinking model and it is never in the pool, so no pool job waits in front of a reply and its model stays ready for you. Tick **Do thinking longer and research here when no machine in the pool takes long jobs** to use it when its provider answers several requests at once. Quick jobs then use their own simple rules.

Martlet sets the order of the jobs: quick jobs go first. When the pool has two or more slots, long jobs leave one slot that takes quick jobs free. While you talk, the machines that share the conversation's computer start no new pool work. Summaries, remembering and thinking longer there stop and go on later. The judges still run there, and the other machines never wait.

Cloud providers and servers in the pool, with their API keys, are the same on all your computers. So are **Backup for slow replies** and **Busy pool**. Paired computers join the pool of each PC by themselves. Models in Ollama on this PC stay with this PC.

### Add a machine

**Add a machine** is under the list. It has three forms:

- **Another of your computers**: your paired computers with a Thinking model join the list by themselves. A computer joins with its Thinking pool role (one machine for each graphics card with a Thinking pool model), or with its Ollama when that Ollama doesn't do this PC's Thinking. To add a new computer, pair it on **Devices**, then add the **Thinking pool role** there.
- **A model in Ollama on this PC**: a second model thinks here while Thinking's model answers you, such as a larger model beside a small, fast one. Ollama runs each model in its own process, so both answer at the same time, but only while both fit on the graphics card. Martlet checks before each think, and doesn't think it over when they don't fit. Replies may start a little later while it thinks.
- **A cloud provider or server**: a strong reasoning model (for example on OpenRouter) thinks while your Thinking model keeps talking, even one on this PC. Paste its API key: Martlet saves it in Windows Credential Manager. A second model at the same provider uses the saved key again, so it needs no key. Tick the box to agree to what goes there (see [What leaves this PC](#what-leaves-this-pc)).

The Thinking pool role is an Ollama of its own. So it works also on the computer that does Thinking for your conversation, but they share its graphics card and replies may start later. Without the role, a computer whose Ollama does Thinking for the conversation can't join: that Ollama answers one request at a time, and a pool job there would make your replies wait. Adding the role asks which model it runs and how many slots it has. **Change model** switches them later. A computer's model can only work as long as its Martlet allows, so update it to the same version for jobs over a minute.

When you untick **On** or press **Remove** for a paired computer, it stays out of the pool. Tick **In the pool** on its row to add it again.

We recommend one graphics card for each Thinking model. A Thinking pool model on the same card as a Thinking model shares it: while both run, each runs at about half speed, so replies can start later. Add another computer with its own graphics card, or a cloud provider, instead. On a computer with several graphics cards, press **Add a model** on the row of a free card: that card joins as a machine of its own, at full speed beside the other cards.

### Runs on

**Runs on** chooses which machines do each kind of job:

- **Any member**: the free machine that shares least with the conversation.
- **Prefer smart**: the smartest free machine. When none of the smartest machines comes free within a short wait (30 seconds at most), a less smart one takes the job. Thinking longer, research and lyrics take the smartest free machine at once.
- **Smart only**: only a **Smart** machine. Without one, the job uses its simple fallback.
- **These members**: only the machines you tick.

Your conversation's own replies never change. Each check-in can choose its own **Runs on** on its card in **Companion › Check-ins**.

### Backup for slow replies

**Backup for slow replies** is off by default. When it is on and the conversation's Thinking model has no words after a short wait, the same request also goes to a machine that has **Backup for slow replies** ticked in its row. The machine that starts first gives the reply, and the other one stops at once. So a slow or busy model doesn't keep you waiting.

- Choose machines with the same model as the conversation, or a similar one.
- **Wait for first words** sets the wait. **Automatic** uses how fast recent replies began.
- A paid cloud provider is asked only when you tick it, and only for a reply already taken. Each request there may cost money.

### Busy pool

When every slot is busy, a more important request can stop a less important one to go first. Tick **Higher priority requests may stop lower ones** to allow this.

- The stopped request keeps its place at the front of its line and starts again when there is room.
- If it keeps getting stopped, it becomes more important, so it finishes in the end. **More important after** sets after how many stops.
- **Try a failed request again** sets how many times a failed request is tried again, at the importance it had.

### Thinking longer

Replies answer right away (Thinking steps are off by default). When a task really needs thought, such as song lyrics, a story, a plan, or tricky math or code, Martlet can say it'll think it over. It works on it in the background while you keep talking, then brings it up when it's done.

- **Let Martlet think things over in the background** turns it on or off. When it is off, Martlet answers everything right away and never thinks in the background or researches. Your computers share this choice. Reload an open conversation after you change it.
- A think uses Thinking steps, whatever replies use. One think runs on each free slot of a machine that takes long jobs, with no time limit and no limit on how many. When the pool has two or more slots, one slot that takes quick jobs stays free.
- **Stop** (Esc) doesn't end a think. Its **Cancel** in the talk window or closing the conversation does.
- A paid provider may charge for its thinking.
- Your Thinking model hands the task off with a tool, so it must use tools: OpenAI or a Chat Completions endpoint, such as Ollama on this PC. A model on a Martlet host can't hand a task off.
- When the pool can't think, add a machine or tick **Long jobs** on one.

### What leaves this PC

- A job's text goes to the machine that runs it. For a think, that is the conversation so far (as much as fits in 16 KiB) and the task. A paired computer gets it through its paired, pinned connection. While it works there, Thinking, the voice and listening keep working here at full speed.
- A cloud provider gets the text of each job it takes. Its requests (with their hidden thinking) may cost money.
- Check-ins, screen and sound summaries and other jobs can include a screenshot or a recording. A machine outside this PC and your paired computers gets them only when you tick **May receive pictures and recordings** in its row. Text-only jobs go to it either way.
- **Web research**: your search words go to DuckDuckGo, and each page Martlet reads sees this PC's internet address. What the pages say goes to the pool machine that works on it, with the recent conversation. Martlet only connects to public websites, never to your own network. A quick look-up reads a few pages. Research reads more, one thing at a time and with no time limit, and an in-depth look reads the most. **Cancel** in the talk window stops it. The report and its sources are kept in [Creations](Pictures-and-Creations), and Martlet offers to show it in your browser.

### When the Thinking model is busy

**Companion › Thinking** has the list **When the Thinking model is busy**. A reply always tries the conversation's own model first, so nothing changes while it is free. Add computers that run the same model, and they take a reply when your model is busy. The list is empty by default: a reply then waits for its own model. Another computer starts without the conversation in its prompt cache, so its first word comes later.

The other jobs (voice, listening and more) have a list of machines too. See [Each job has a list of machines](Multiple-Computers#each-job-has-a-list-of-machines).

### Image and audio models

The **Image models** list on **Companion › Vision** and the **Audio models** list on **Companion › Hearing** hold the models that describe your pictures or recordings in words for Thinking. See also [Image models](Vision#image-models) and [Audio model](Listening-and-Voice-ID#audio-model).

- The lists are in order. The first model that sees (or hears) takes the pictures (or recordings). The next ones take one when it is busy or can't.
- With an empty list, the line under it says which model takes each picture or recording instead. A reply never waits for an image or audio model.
- To add a cloud provider or server, press **A cloud provider or server**. Choose a model that sees pictures (or hears recordings). It describes them in words for Thinking and never answers you itself. It goes at the end of the list: move it up with **Up**.
- An API key that you paste is saved in Windows Credential Manager on this PC. To replace a saved key, paste a new one and press **Save**.

The models in the Thinking pool that see or hear get your pictures or recordings too, for background work: screen summaries and PC sound descriptions as quick jobs, and touch zones, the character's eyes and check-ins as long jobs. Both pages list these models under **Also gets your pictures** (or recordings). You set them on **Companion › Thinking pool**.

### Thinking requests

The **Thinking requests** page in the side menu shows every request for the Thinking pool since Martlet started. The waiting and running requests come first, then the newest ones that ended.

- Select a request to see its type, task, machine, tries and how long it waited and ran.
- **Input** shows what it sent its model, after it is sent. **Output** shows what the model answered, after the request ends. Both stay on this PC: they are never in a log, a status file or MCP.
- When no machine is in the pool, requests use their own simple fallbacks. Add machines on **Companion › Thinking pool**.
- **Copy shown** copies the shown requests with their timings. When another app is using the clipboard, try again. **Clear finished** only forgets the requests that ended.

## Looking things up

**Web research** on **Companion › Thinking pool** is on by default. Martlet answers what she knows at once. Is she unsure of one fact, like a price, a date or how to get something in a game? Then she does a quick look-up and tells you in a minute or two. For bigger questions, she researches and writes a report. Ask for in-depth research to get a longer one. She tells you as soon as she finds your answer, and she checks the report's sources against their pages. The search words go to DuckDuckGo, and each page she reads sees this PC's internet address. Reports wait in [Creations](Pictures-and-Creations).

## Check-ins

Small models forget what they left on and what they promised. **Companion › Check-ins** has the Thinking pool check on Martlet every few minutes or when something happens. Check-ins run only on pool machines, never on your conversation model, so replies never wait for them. Each check-in gets only the facts it needs, such as the end of the conversation, the emotes that stay on and the reminders Martlet started. Check-ins wait while you talk and while nobody uses the PC, and they stay on that PC. A check-in's card shows its last answer and each tool it called.

Built-in check-ins turn off emotes that no longer fit and take the eyes back to their usual gaze. They remind Martlet of her promises and her character, and stop her from repeating herself. Others keep her quiet while you talk to someone else, describe your touches and think ahead about what you'll say next. **How I react** changes how she takes your touches when her mood changes. **Welcome back**, **Unanswered question** and **Someone else is here** are off until you turn them on.

**Act on what was said** runs right after each reply. The pool does what you asked or Martlet promised, like calling a Discord friend, setting a reminder or changing a memory. So the reply only says she'll do it, and it starts sooner. If something doesn't work, the talk window says so. Check-ins with tools work in several steps until they are done: say "that's for Minecraft, not Terraria!" and it finds every memory it touches and fixes each one.

**Memory care** looks after what Martlet remembers every hour. It adds which game or app a memory is about when the memory doesn't say, gives memories to the right person, merges duplicates and fixes memories your conversation showed are wrong. Every fix keeps the earlier version.

### Your own check-ins

To write your own check-in:

1. Press **Add a check-in**. A new check-in starts off.
2. Write its prompt, such as "If it's late at night, remind Martlet to talk more softly". Choose what it gets to know (it always gets the day and time): your screen, your microphone, what the PC plays, your touches, what was said or who is here.
3. Choose when it runs: **Every** few minutes, or under **It starts when**, such as when a reply ends or your touches end. It can also wait until you come back, a call starts or ends, a notification shows, a song ends or a script's output changes.
4. Choose what **Its answer** does: remind Martlet in her next reply, have her bring it up, add to what she knows, or let **Its tools act**.
5. Under **It may use these tools**, tick tool sets such as reminders, memory, Discord calls, songs and pictures. Tools need a pool model that calls tools.

**Copy as your own** turns a built-in check-in into one you can change freely. **Use built-in settings** puts Martlet's own back.

More detail: [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Local model apps](https://github.com/throndir2/Martlet/blob/main/docs/LOCAL_MODEL_APPS.md), [Hosted Thinking](https://github.com/throndir2/Martlet/blob/main/docs/HOSTED_THINKING.md), [Model catalog](https://github.com/throndir2/Martlet/blob/main/docs/MODEL_CATALOG.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md).
