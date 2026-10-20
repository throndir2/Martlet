# Thinking

Thinking is the model that reads your message and writes Martlet's reply. Run it free and private on your own PC, on another of your computers, or with a cloud AI you like.

![Companion Thinking](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion.png)

## Where it runs

Open **Companion › Thinking** and choose under **Where it runs**:

- **This PC (recommended)**: private, free and with no account. Under **Model app**, choose **Ollama**: Martlet installs it and downloads a model that fits your graphics card. Any Ollama model works, also a Hugging Face GGUF or a model you made. Or choose a model app you already run: Martlet finds **LM Studio**, **llama.cpp**, **KoboldCpp**, **Jan**, **vLLM**, **Lemonade**, **GPT4All** and **Docker Model Runner**.
- **Another of your computers**: a paired computer with a stronger graphics card thinks for you. See [Multiple Computers](Multiple-Computers).
- **A cloud provider**: **OpenAI**, **Google Gemini**, **OpenRouter**, **NVIDIA Build** or any OpenAI-compatible service. Your messages leave this PC, and some providers charge. Martlet walks you through a free key for NVIDIA Build or Google Gemini.

**Recommended setup** on Home picks a model that fits all your computers and tells you when a better one is available.

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

## When Thinking fails or is slow

- **If Thinking fails** on **Companion › Thinking** is an optional backup model. It answers once when the main model fails before any words arrive: an error, a rate limit or no answer in 15 seconds. A good pair is NVIDIA Build first and Google Gemini as the backup.
  - Screen and camera looks use the backup too, so choose a model that can see images.
  - When the backup answers, your messages, the recent conversation and any screen or camera picture of that reply go to it. Its requests can cost money. You tick a box to agree before you save it.
  - A backup that is the same as Thinking adds nothing, so Martlet asks you to choose another model or provider.
  - Paste the backup's API key in its key box. Martlet keeps it in Windows Credential Manager. Leave the box empty to keep a saved key, or to use Thinking's key for the same provider.
  - With no backup, Martlet says that Thinking failed, and you try again.
- **Backup Thinking** asks a second model when the first one is slow to start. It is off by default. On **Companion › Thinking pool › Backup for slow replies**, tick **Ask a machine in the pool too when a reply is slow to start**. Then tick **Backup for slow replies** on the machines it may use, ideally with the same model. The first one to start gives the reply.
- Do you play games on a PC? Then while a game runs there, Martlet moves her thinking off that PC's graphics card to another computer. The model stays loaded while the game leaves room. When the game ends, she moves it back. When no other place can do live Thinking, it stays on that PC, and Martlet unloads it only when the game needs the graphics card's memory. Ollama then runs it on the processor.

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

**Copy as your own** turns a built-in check-in into one you can change freely. The copy starts off. **Use built-in settings** puts Martlet's own prompt, facts, conditions, answer, inputs and tools back. **On** and **Every** stay as they are.

You can have at most 8 check-ins of your own. To add or copy one more, remove one first. When you edit a built-in check-in's prompt, the check-in uses the new prompt from its next run. If you empty the prompt, the check-in asks nothing.

**Check now** runs one check-in at once. Only one check-in runs at a time. If another check-in runs, try again in a moment.

The **Now** line tells how many check-ins are on and which Thinking pool member takes them first. If the pool has no member that can take check-ins, they wait. Add a member on **Companion › Thinking pool**.

### What a check-in knows

A check-in always gets the day and time. Under **It gets to know**, tick the facts it also gets. Each fact adds a few lines of text to the check-in's question. If Martlet doesn't have a fact now (for example the screen, while Martlet doesn't watch it), the line says so.

A placeholder puts a fact where you want it in the prompt. The facts that the prompt doesn't name go after the prompt, then the day and time and the answer format. Other placeholders are `{name}`, `{time}` and `{adult}` (whether Adult content is on, from **Companion › Prompts**).

| Fact | What the check-in gets | Placeholder |
|---|---|---|
| The conversation | The last 6 exchanges (you and Martlet, oldest first) and how long it has been quiet since. | `{conversation}` |
| Its personality | The active personality: the character's name and the text you wrote for it. | `{persona}` |
| Martlet's last replies | Martlet's last 6 replies, numbered, oldest first, without what you said. | `{replies}` |
| What Martlet said in the last hour | Martlet's replies, remarks and reactions of the last hour (the newest 10). Each has the time, such as "10:05 PM (12 min ago)". | `{said}` |
| Emotes and gaze | The emotes a reply turned on that still show, with their hints and how long each has shown. Where the eyes look now and where they usually look. | `{emotes}`, `{example}` (the first emote's tag), `{looking}`, `{usual}`, `{since}` |
| Reminders and background work | The reminders that are set and the background work Martlet started or finished in this conversation. | `{work}` |
| What changed on screen | Martlet's newest words about what changed on your screen, while it watches the screen. This is not a screenshot. For a screenshot, tick **A screenshot of the screen in front**. | `{screen}` |
| What the PC plays | Martlet's newest words about what this PC plays (music, videos, games), while it hears it. This is not a recording. For a recording, choose one under **With each run it takes**. | `{sound}` |
| Whether you're at the PC | Whether someone uses this PC now, or how long since someone last used the keyboard or mouse. | `{presence}` |
| How you touched the character | What you did to the character in the last 10 minutes, oldest first, each with the time: pokes, pats, holds, strokes (with their path and direction) and moves. It tells which touches were intimate, how the personality feels about them and the places you keep coming back to. Martlet only reads it: its next reply still gets your touches. | `{touches}` |
| What you are doing | What you seem to do on this PC (a game, a call or voice chat, a video, music, full screen), what it was before and when it changed. Martlet knows it only while it hears what this PC plays. | `{activity}` |
| Who is here | The voices Martlet heard in the last 10 minutes (Voice ID), each with the time: the names of the voices it knows, a voice it doesn't know and which voice is yours. Never what they said. | `{people}` |
| What happened while you were away | After you come back from 10 minutes or more away: how long you were away, what Martlet said meanwhile, the background work that finished and the reminders that came due. | `{away}` |
| The latest exchange | What you said last and Martlet's reply, in full. If the check-in didn't run after each exchange, it gets every exchange since it last ran (at most 6), so it misses nothing. | `{exchange}` |
| What Martlet heard you say | What always listening heard you say in the last 10 minutes, oldest first. Each line has the time, who spoke (Voice ID), other voices, people who talk over each other, lines that this PC played at the same time, and whether Martlet answered or stayed quiet. Then whether Martlet holds back now while you talk to someone else. | `{heard}` |
| The callback list | The few things from this conversation that are worth bringing back later (at most 8): what you're in the middle of, running jokes, things you said in your own words and facts that fit a tease. Each has when it came up. The **Callback list** check-in writes the list after each reply. | `{callbacks}` |

Under **With each run it takes**, a check-in can also take:

- **A screenshot of the screen in front**: a screenshot of the screen you work on. It doesn't run while a private window is in front.
- **A recording**: the last seconds of the microphone or of what this PC plays. Choose the length under **Length**.

**A script it runs first** is optional. Windows PowerShell runs the script hidden in your home folder, as you, before each run. The script stops after 20 seconds. The check-in gets what the script prints. For example, this script gives the busiest programs: `Get-Process | Sort-Object CPU -Descending | Select-Object -First 10 Name, CPU`.

### When a check-in runs

Every check-in waits:

- for its time (**Every**),
- while you talk,
- while nobody uses this PC,
- for its conditions under **It waits until**,
- for a Thinking pool member that can take it.

**Check now** skips the waits for the time and for something new, but not the other waits. Point at a condition to see when it waits. Some conditions need more detail:

- **What you do changed**: it waits until a game, a call or a full-screen app started or ended since it last ran. Martlet can tell this only while it hears what this PC plays.
- **Martlet asked something you didn't answer**: it waits until Martlet's last remark asked you something, you're at the PC and you didn't answer for 2 minutes. It runs once for each question.
- **A taskbar button flashed or a notification showed**: it waits until a taskbar button flashed or a pop-up notification showed since it last ran. Martlet notices them only while a check-in that is on waits for them. Martlet never reads what they say.
- **The conversation went quiet**: it waits until nothing was said for 3 minutes (but not more than 30), you're at the PC, the callback list has something and Martlet's last remark didn't ask you something. It runs once in each quiet spell.
- **Only between these hours**: it runs from the **From** hour up to the **Until** hour. When the **Until** hour comes first, it runs past midnight. The two hours must be different.
- **At most**: a limit. However often its conditions are met, it runs at most this many times in an hour.

Under **It starts when**, you can tick events that start the check-in instead of a time:

1. With one event ticked, the check-in runs only when that event happens: a moment after your touches stop, right after a reply, or right after you say something.
2. It runs at most once per **Every**. **Every** is then the time it waits before it can run again.
3. It runs on the Thinking pool, so a reply never waits for it.
4. It still waits while it's off, while nobody uses this PC, for its conditions and for a pool member.
5. With no event ticked, it runs every few minutes.

**A reply ends** runs right after each of Martlet's replies (after Martlet said it), with what you said and the reply. Exchanges that end while it waits for its **Every** are kept, and it reads them all when it runs. **You say something** runs right after always listening hears you say something, whether Martlet answered or stayed quiet. Only what the microphone hears counts, not what you type.

### What its answer does

Martlet adds the answer format to the check-in's prompt. **Its answer** sets what Martlet does with the answer:

- **Reminds Martlet in its next reply**: a `REMIND:` line goes in the notes of your next message, so Martlet's next reply follows it.
- **Martlet brings it up**: Martlet says a `SAY:` line on its own as soon as it's free.
- **Adds to what Martlet knows**: a `KNOW:` line, a short description of what happens, goes in the notes of your next message (within a few minutes). Martlet may use it as background. It is not a reminder to follow.
- **Thinks ahead for Martlet's next reply**: a `PLAN:` line goes in the notes of your next message, once. It has the character's own thoughts about what you'll likely say next and what it means to say then. If another reply ends first, Martlet drops it.
- **Martlet holds back while you talk to someone else**: an `ASIDE:` line makes Martlet stay quiet while that lasts, unless you talk to it. `BACK` ends it, and so do a few quiet minutes.
- **Keeps Martlet's callback list**: `CALLBACK:` lines are the whole new list of things worth bringing back later. **Think ahead** and **Quiet moments** read the list. `KEEP` leaves the list as it is and `NONE` empties it.
- **Turns off the emotes it names**: `OFF` lines with the tags of lingering emotes turn them off. Tick **Emotes and gaze** for this.
- **Takes the eyes back to their usual**: `USUAL` ends the gaze that a reply chose.
- **Its tools act**: the tools the check-in calls are what it does, and its answer only says what it did. Tick at least one tool set under **It may use these tools**.

For every answer, `OK` or `KEEP` changes nothing.

### Check-in tools and models

Under **It may use these tools**, tick the tool sets the check-in may call. The model can call these tools while it answers, at most 64 calls in 16 rounds. Only Thinking pool members on an endpoint whose model calls tools take a check-in with tools. Point at a tool set to see what it does and its tools.

Under **The model must handle**, choose what the check-in's model must do: read text (always), see pictures or hear recordings. Only Thinking pool members that can do all of these take the check-in.

- A screenshot needs a model that sees pictures. A recording needs a model that hears recordings.
- A screenshot or a recording goes only to that member. The member can be a cloud service.
- Martlet keeps the microphone and what this PC plays in memory only while a check-in that is on asks for them and Martlet hears them.
- Gemma 4 and Gemma 3n hear only the first 30 seconds of a recording. Gemini, OpenAI's audio models and Voxtral hear a whole minute.

More detail: [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Local model apps](https://github.com/throndir2/Martlet/blob/main/docs/LOCAL_MODEL_APPS.md), [Hosted Thinking](https://github.com/throndir2/Martlet/blob/main/docs/HOSTED_THINKING.md), [Model catalog](https://github.com/throndir2/Martlet/blob/main/docs/MODEL_CATALOG.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md).
