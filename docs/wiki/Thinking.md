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

**Copy as your own** turns a built-in check-in into one you can change freely. **Use built-in settings** puts Martlet's own back.

More detail: [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Local model apps](https://github.com/throndir2/Martlet/blob/main/docs/LOCAL_MODEL_APPS.md), [Hosted Thinking](https://github.com/throndir2/Martlet/blob/main/docs/HOSTED_THINKING.md), [Model catalog](https://github.com/throndir2/Martlet/blob/main/docs/MODEL_CATALOG.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md).
