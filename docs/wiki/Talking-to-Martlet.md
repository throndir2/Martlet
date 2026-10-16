# Talking to Martlet

Talk hands-free, hold a key or type. Martlet answers out loud within moments,
waits until you have finished and lets you talk over her.

![Talk window](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/talk.png)

## Ways to talk

| Way | How |
| --- | --- |
| **Always listening** (the default) | Press **Start listening** on Home or in the talk window. Martlet answers when you pause and listens again after each reply, until you press **Stop listening**. |
| **Push-to-talk** | Choose it in **Companion › Listening › How you talk**. Hold **Hold to talk** (or Space) in the talk window, speak, then release to send. |
| Type | Press **Start talking** on Home. Type in the talk window and press Enter. Shift+Enter starts a new line. |
| Discord | Set up the bot in **Companion › Discord**. See [Discord](Discord). |
| Telegram and WhatsApp | Pair a chat in **Companion › Messaging**. See [Telegram and WhatsApp](Telegram). |

Nothing listens until you press a button. Starting Martlet does not open the
microphone, and no letter key starts listening or watching.

One turn of talking can last up to 10 minutes. Martlet hears long speech in
pieces cut at your pauses, so nothing you say is lost.

## The talk window

- **Start talking** on Home opens the talk window. While it is open, the button
  says **Show conversation**. The window has its own taskbar button, so it can
  stay open beside your work.
- It calls your character by its personality's name, and labels each message
  with the name of the person who spoke.
- The small icons at the top start and stop listening and watching. Hover over
  an icon to see what it does.
- **Stop** (or Esc) stops the reply, drops what waits to be answered and stops
  watching. Listening stays on.
- **What was sent** under each of Martlet's messages shows everything that
  went to the Thinking model for that reply.
- **Refresh context** makes the next reply start fresh. The messages stay on
  screen.
- **Warming up** shows while Martlet loads its model. You can talk or type at
  once. The first reply starts when the model is ready.
- The **Conversations** page keeps every conversation: the talk window,
  Discord, Telegram and WhatsApp.

## How Martlet takes turns

These settings are in **Companion › Listening › How you talk**.

- **She hears when you have finished.** With **Judge when I finish talking**
  on (the default), a small model on this PC listens to how you end each
  sentence. Martlet answers sooner when you are clearly done. She waits longer
  when you trail off.
- **She starts in the pause.** With **Start replies early** on (the default),
  Martlet starts on her reply in the short pause after you speak. She shows and
  says nothing until your turn ends. If you keep talking, she drops that start.
  This needs Parakeet on this PC for listening and a Thinking model on your
  own computers. **Also for cloud models** allows it with a cloud model, which
  can add a small cost.
- **Reply after** sets how long a pause ends your turn. **Sensitivity** sets
  how readily Martlet hears quiet speech.
- **Word check** (Relaxed, Normal or Sensitive) sets how readily a sound
  counts as words. A cough or a "Mm" does not become a turn. The talk window
  shows a faded *Ignored* note for it.

## How Martlet answers

- She speaks each reply sentence by sentence while it is written, so her voice
  starts before the whole reply is ready.
- Each spoken reply starts with a few quick words, such as "Hmm, good
  question." Turn this off with **Companion › Replies › Short first
  sentence**.
- When a reply is slow to start, she can say a quick "Mm," or "Hmm..." in her
  own voice. Turn on **Companion › Voice › Quick sounds while Martlet thinks**
  (off by default).
- Long replies are said to the end.
- **Speak Martlet's replies aloud** in **Companion › Voice** turns her voice
  on or off. **Mute voice** on the character's right-click menu does the same.
  When the voice is off or fails, the text still shows in the talk window and
  the speech bubble.
- **Companion › Replies › Thinking steps** is Off by default, so replies start
  sooner. A model that always thinks first uses its own default.

## Talking over Martlet

Martlet keeps listening while she speaks, when **Reduce echo from my
speakers** is on (the default). By default, what you say then is answered
after her reply. **Stop** or Esc always stops her.

To stop her by talking, turn on **Let me interrupt Martlet by talking** in
**Companion › Listening**. It is off by default. Then choose **When you talk
over Martlet**:

- **Pause and decide** (recommended): she pauses at once. Words meant for her
  stop the reply and get an answer. A "yeah", agreeing, a laugh, talk to
  someone else or the TV let her carry on from where she paused, with nothing
  lost. "Stop", "wait" or her name stop her at once.
- **Stop at once**: real words stop the reply right away.

A hum, a cough, laughter and the sound this PC plays never stop her. When you
cut her off, she remembers only what she said aloud. The next reply knows that
you did not hear the rest, and it can pick it up if it still fits.

Through speakers, this needs echo reduction. If Martlet stops herself, use
headphones or turn interrupting off.

## People around you

- Martlet treats voices and sounds around you as context. She answers someone
  in your call or room who clearly talks to her.
- When words were not for her, she stays quiet, and the talk window notes
  *Martlet stayed quiet*.
- The **Talking to someone else** check-in (on by default) notices when you
  talk to someone in the room, in a call or on the phone. Martlet then holds
  back until you talk to her again. See [Check-ins](Thinking#check-ins).
- **Voice ID** makes Martlet answer only your voice. **People** learns who is
  talking. See [Listening and Voice ID](Listening-and-Voice-ID).

## Context

Each reply gets the recent conversation that fits **Companion › Replies ›
Context size**. Memory, the lorebook and other notes can go with it. Older
exchanges are left out when a conversation grows too long for that size.

## Privacy

- The microphone, the camera and the screen stay off until you press a button.
- Your words go only to the Listening and Thinking you chose. A cloud service
  gets them only when you choose that service.
- Martlet asks before it uses a tool or does a risky smart-home action.

More detail: [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Voice latency](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_LATENCY.md), [Messaging](https://github.com/throndir2/Martlet/blob/main/docs/MESSAGING.md).
