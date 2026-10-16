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

- **If Thinking fails** on **Companion › Thinking** is an optional backup model. It answers once when the main model fails before any words arrive. A good pair is NVIDIA Build first and Google Gemini as the backup.
- **Backup Thinking** asks a second model when the first one is slow to start. It is off by default. On **Companion › Thinking pool › Backup for slow replies**, tick **Ask a machine in the pool too when a reply is slow to start**. Then tick **Backup for slow replies** on the machines it may use, ideally with the same model. The first one to start gives the reply.
- Do you play games on a PC? Then while a game runs there, Martlet moves her thinking off that PC's graphics card to another computer. When the game ends, she moves it back.

## Reply settings

**Companion › Replies** sets **Max reply length** (up to 32,768 tokens), **Context size**, **Thinking steps** (off by default, so replies start sooner) and **Short first sentence**. A larger context can cost more on paid providers.

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

Small models forget what they left on and what they promised. **Companion › Check-ins** has the Thinking pool check on Martlet every few minutes or when something happens. Check-ins run only on pool machines, never on your conversation model, so replies never wait for them.

Built-in check-ins turn off emotes that no longer fit and take the eyes back to their usual gaze. They remind Martlet of her promises and her character, and stop her from repeating herself. Others keep her quiet while you talk to someone else, describe your touches and think ahead about what you'll say next. **How I react** changes how she takes your touches when her mood changes. **Welcome back**, **Unanswered question** and **Someone else is here** are off until you turn them on.

**Act on what was said** runs right after each reply. The pool does what you asked or Martlet promised, like calling a Discord friend, setting a reminder or changing a memory. So the reply only says she'll do it, and it starts sooner. If something doesn't work, the talk window says so.

To write your own check-in:

1. Press **Add a check-in**.
2. Write its prompt, and choose what it gets to know: your screen, your microphone, what the PC plays, your touches, what was said or who is here.
3. Choose when it runs: **Every** few minutes, or under **It starts when**, such as when a reply ends or your touches end. It can also wait until you come back, a call starts or ends, a notification shows, a song ends or a script's output changes.
4. Choose what **Its answer** does: remind Martlet in her next reply, have her bring it up, add to what she knows, or let **Its tools act**.
5. Under **It may use these tools**, tick tool sets such as reminders, memory, Discord calls, songs and pictures. Tools need a pool model that calls tools.

**Copy as your own** turns a built-in check-in into one you can change freely. **Use built-in settings** puts Martlet's own back.

More detail: [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Local model apps](https://github.com/throndir2/Martlet/blob/main/docs/LOCAL_MODEL_APPS.md), [Hosted Thinking](https://github.com/throndir2/Martlet/blob/main/docs/HOSTED_THINKING.md), [Model catalog](https://github.com/throndir2/Martlet/blob/main/docs/MODEL_CATALOG.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md).
