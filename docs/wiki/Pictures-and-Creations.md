# Pictures and Creations

Martlet paints pictures, writes and sings songs, and researches the web, all in
the background while you keep talking. Everything she makes is a **creation**.
It waits for you on the **Creations** page.

## Picture making

Open **Companion › Picture making**. Martlet draws nothing until you add a place
to **Where it draws**. Under **Add to the list**, add **This PC**, one of your
computers, an address (**Add address**) or **A cloud provider**.

| Place | What it is | Cost and privacy |
| --- | --- | --- |
| **Martlet's Picture making role** (recommended) | ComfyUI with Z-Image Turbo, on this PC or on one of your computers with an NVIDIA graphics card (8 GB or more; 12 GB is faster). **Set up** installs it. | Free and private. About 20 GB of downloads. |
| **Your own ComfyUI** | A ComfyUI you already run, at its address on your home network. | Free and private. |
| **OpenRouter** | A cloud model that draws. | Each picture costs money. Your description goes to OpenRouter. |
| **NVIDIA Build** | NVIDIA's FLUX models, with your `nvapi-` key. | Your description goes to NVIDIA. |

Each place has **On**, **Up**, **Down**, **Remove** and **Settings**. A place's
**Settings** set up the role or choose the ComfyUI workflow. **Check** asks each
place whether it can draw now. **Draw a test picture** draws one picture; when
a cloud provider is on, it asks first, because a picture there costs money.

Each picture goes to the first place in the list that is free. When a place is
busy, offline or missing a file, the next place draws. When all are busy, the
picture waits in the shortest line. Each computer keeps its own list, because
which place is free depends on where you talk. The pictures are shared.

## Asking for a picture

Say "Draw me a fox curled up in the snow." Your Thinking model must be able to
call tools.

- She starts the picture in the background and keeps talking.
- When it's ready, she shows it in the talk window. Click it to see it full size.
- She keeps it in Creations. Later, say "Show me the fox picture again."
- She draws up to two pictures at a time and 20 an hour.

## Web research

Martlet looks things up on the web in the background and tells you what she
found.

Turn it on or off on **Companion › Thinking pool › Web research**, with **Let
Martlet search the web and read pages to look things up**. It is on by default.
It needs **Thinking longer** on and a Thinking model that can call tools.

How much she looks up depends on the question:

1. She answers what she knows at once. Most questions need no search.
2. She checks her [app guides](App-Guides) and her earlier reports.
3. For one fact she isn't sure of, such as a price, a date or how to get
   something in a game, she does a **quick look-up**. She tells you in a minute
   or two.
4. When you ask her to look into something, she does **research**: up to 30
   searches and 60 pages, with notes as she reads. Ask for an in-depth look,
   and she reads about twice as much.

While she researches:

- She tells you as soon as she finds what you wanted, then goes on for the full
  report.
- The report has numbered sources. She keeps it in Creations and offers to show
  it in your browser.
- When the report is ready, she checks each claim against the page it cites and
  adds a **Source check** to the report. You don't wait for it.
- Later, ask about it again. She finds the part of the report that answers,
  with its pages.
- One research job runs at a time, with no time limit. **Cancel** in the talk
  window's **Background tasks** list stops it.

The work runs on a member of your [Thinking pool](Thinking#thinking-pool) that
does web research, so her replies stay fast.

**Privacy:** your search words go to DuckDuckGo, and each page she reads sees
your PC's internet address. What the pages say goes to the Thinking pool member
that does the work, with the recent conversation. She reads only public
websites, never your home network. A paid cloud provider may charge for its
thinking.

## Songs

She writes the lyrics and the music, and sings in a voice you chose. See
[Singing](Singing).

## Each job goes to a free computer

Each kind of work has its own list of computers, in order:

- Pictures: **Companion › Picture making**.
- Songs: the **Music** and **Voice match** lists on **Companion › Singing**.
- Research and look-ups: your [Thinking pool](Thinking#thinking-pool).
- Body motions for a VRM character: **Companion › Motion**.

When a computer is busy or offline, the next one takes the job. See
[Multiple Computers](Multiple-Computers).

## The Creations page

Open **Creations** in the main window. It lists everything Martlet made:
pictures, songs, research reports and body motions for VRM characters.

Choose a creation to see its title, kind and date, the voice and personality
that made it, and its details, such as a song's lyrics or a report's text.
**Rename** gives it a new title. **Delete** removes it. Both apply on all your
computers, and a deleted creation can't be brought back.

There is no Play button. Ask Martlet instead: "Sing the rain song again",
"Start from the chorus" or "Show me the fox picture."

## On all your computers

With **Keep Martlet the same on all my computers** on, your creations go to
every computer where you are signed in. A creation that is still on its way says
*Still copying to this PC.* Each person in your household has their own
creations. See [Accounts and household](Accounts-and-Household).

## Use pictures responsibly

Don't make illegal or harmful pictures. Don't show real people without their
consent. Say that a picture is AI-generated when you share it. Z-Image Turbo is
Apache-2.0, ComfyUI is GPL-3.0, and cloud models have their providers' terms.

More detail: [Picture making](https://github.com/throndir2/Martlet/blob/main/docs/PICTURES.md), [Creations](https://github.com/throndir2/Martlet/blob/main/docs/CREATIONS.md), [Web research](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md#web-research), [Deep research design](https://github.com/throndir2/Martlet/blob/main/docs/DEEP_RESEARCH.md), [Picture making role](https://github.com/throndir2/Martlet/blob/main/docs/PICTURES_HOST.md).
