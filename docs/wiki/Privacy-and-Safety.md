# Privacy and Safety

Martlet runs on your own computers. It asks before it captures, sends, spends or changes things.

## When Martlet starts

Starting Martlet doesn't listen, watch or send anything you say. The microphone, camera and screen stay off until you press **Start listening**, **Start watching** or your push-to-talk key.

A few things happen without a click. None of them sends what you say:

- **Update checks** (on by default): Martlet asks GitHub for new releases. Turn them off in **Settings › App updates**.
- **The model list**: once a day, Martlet reads public model lists, with no key, to keep its list of AI models current.
- **Your own computers** sync settings, memories and logs with each other, only over their own paired connections.
- **App guides** (on by default): when you start a game that Martlet has no guide for, it sends the game's name to DuckDuckGo and reads the game's wiki. Those sites see the requests and your internet address. Martlet sees only the program's name, never window titles or the screen. To stop it, say no, press **Stop reading**, or turn off **Read up when I start a game or app** in **Companion › App guides**. See [App Guides](App-Guides).

Two startup choices in **Settings › Startup and closing** are worth knowing:

- **When Martlet starts, show the character and start listening** is off by default.
- **Greet me once Martlet has warmed up after it starts** is on by default. When you start talking or listening, Martlet says hello. The greeting's request to your Thinking model has the time of day, your name, the end of your last conversation, reminders coming up and the names of the apps you have open.

## Where your data goes

- **Microphone audio** goes to the Listening engine you chose. Voice ID runs on your own computers.
- **Your words** go to your Thinking model, and to the backup in **If Thinking fails** when it steps in.
- **Background work** (thinking longer, research, screen and sound summaries, remembering) goes to the models in your Thinking pool. A cloud model there gets that work.
- **Reply text** goes to the voice engine you chose.
- **Screenshots and camera pictures** go to your Thinking model or image model, only after **Start watching** or when you add a picture to a message. They are never saved.
- **Sound your PC plays** goes to your audio model, only with **Hear what this PC plays** on.
- **Web research** sends search words to a search service and reads web pages.
- **Tool calls** go to the tool you allowed. **Smart-home commands** go to your Home Assistant.
- **Discord, Telegram and WhatsApp** messages go through those services.

A cloud service gets your words only when you choose it. It gets pictures and recordings only when you allow it. Work never moves to the cloud silently. Cloud providers can charge for use and keep data by their own terms.

To see exactly what Martlet sent for one reply, open **What was sent** under that reply in the talk window.

## What stays on your computers

- Memories, conversations, voices, characters and creations live on your own computers. They sync only between them. There is no cloud memory service.
- API keys stay in Windows Credential Manager, never in settings files or logs. They travel to your other computers over your own paired connections, so a new PC gets the same setup.
- Logs hold activity, errors and status, never keys or what you said. They can include local paths, computer names and provider error text. Review them before you share them.

## Personal things on your screen

Martlet never looks at its own windows. By default, it also never looks at password managers or private browser windows. **Companion › Replies › Personal content** decides what Martlet may do with personal things it sees:

- **Keep private** (the default): it never reads out messages, chats, emails or numbers. It says only who a message is from.
- **Read my messages**: it may read and talk about your messages, chats, emails and notifications.
- **Everything**: it may also see password managers and private browser windows.

## Accounts in your household

Each person has their own memories, characters, settings and creations. On the **Account** page you can:

- add a Martlet password, with an optional authenticator app,
- lock your account on this PC with a PIN or Windows Hello,
- turn on **Encrypt my files on this PC while locked** (off by default; it needs a PIN).

Windows Hello can't tell apart people who share one Windows login. Encryption keeps people on one Windows login apart, but not from an administrator or from someone who guesses a short PIN. See [Accounts and Household](Accounts-and-Household).

## Sharing with friends

- A friend uses only the hosts you share with them, and only their engines: thinking, listening, speaking, lip-sync and reading. They never see your settings, memories, voices, logs, API keys or other computers.
- When you share your language models with a key, people get those models only. Your cloud models and API keys are never shared.
- Your own work always comes first, and you can stop sharing at any time on the **Share** page.

See [Sharing with Friends](Sharing-with-Friends).

## You stay in charge

- Before a tool runs, Martlet shows what it wants to do: **Allow once**, **Always allow** or **Deny**.
- The **Terminal** is off until you turn it on. It asks before every command and never runs as administrator.
- Locks, doors, garage doors, gates, valves and alarms are blocked, or Martlet asks every time: **Send request** or **Don't send**.

## Voices, songs and pictures of people

Voice ID is not a password: Martlet uses it to know who talks, not to prove who you are. Use only voices, characters and pictures that you have the rights to use. Don't clone a voice or make songs or pictures of a real person without their permission. Say that AI-made pictures and songs are AI-made when you share them.

More detail: [README privacy](https://github.com/throndir2/Martlet/blob/main/README.md#-private-by-design), [App guides](https://github.com/throndir2/Martlet/blob/main/docs/APP_GUIDES.md#privacy), [Accounts](https://github.com/throndir2/Martlet/blob/main/docs/ACCOUNTS.md#account-security), [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md#sharing-a-host-with-friends), [Smart home](https://github.com/throndir2/Martlet/blob/main/docs/SMART_HOME.md), [Diagnostics](https://github.com/throndir2/Martlet/blob/main/docs/DIAGNOSTICS.md).
