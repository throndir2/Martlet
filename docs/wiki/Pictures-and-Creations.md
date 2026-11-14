# Pictures and Creations

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Users
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Desktop/Pages/Pictures`, `src/Martlet.Conversation/Pictures`, `src/Martlet.Conversation/Research`, `src/Martlet.Core/Creations`

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
picture waits in the shortest line. The list and its choices are shared between your computers. Each computer skips
places it cannot use. Cloud keys travel as secrets, not as plain setting values.
The pictures are shared too.

When you add a place, it goes to the end of the list. Press **Up** to draw
there sooner. With nothing on, pictures are off.

### Set up the Picture making role

Add **This PC** or one of your computers, open its **Settings** and press
**Set up**. Martlet sets the role up in Docker, as its other roles, and shows
the progress. If this PC has no host service yet, Martlet sets it up first (and
installs Docker Desktop when it is missing).

- It needs an NVIDIA graphics card with 8 GB or more (12 GB is faster). On a PC
  without one, use another computer, your own ComfyUI or a cloud provider.
- It shares the graphics card with the voice and listening. It uses the card
  only while it draws, and frees it a few minutes after the last picture.
- It builds a container with ComfyUI (GPL-3.0) and PyTorch. Then it downloads
  about 20 GB of pinned Z-Image Turbo files from
  `huggingface.co/Comfy-Org/z_image_turbo` (Apache-2.0): the diffusion model,
  its Qwen3 4B text encoder and the VAE.
- The descriptions that Martlet writes go to that computer.

If setup stops, its run window has the details. If setup finishes but Martlet
can't see Picture making yet, wait a minute and press **Check**.

### Your own ComfyUI

Choose **Add address** and type the address of a ComfyUI that you run. Martlet
sends ComfyUI the workflow and the description through ComfyUI's own API, and
fetches the picture.

1. Make your ComfyUI address reachable from the PC that runs Martlet.
   Use only a network you trust. Do not expose an unprotected ComfyUI service to the internet.
2. Open the place's **Settings** and press **Connect**. Martlet shows the
   ComfyUI version, its checkpoints and whether Z-Image Turbo is there.
3. Choose the **Workflow** and press **Save**:
   - **Z-Image Turbo**: its three files must be in ComfyUI's models folders.
   - **A checkpoint**: a Stable Diffusion 1.5 or XL checkpoint that **Connect**
     listed.
   - **My own workflow**: in ComfyUI, open your workflow and use
     **Workflow › Export (API)**. Then press **Load workflow file** and choose
     that file (256 KB at most). Put `{{prompt}}` where the description goes.
     You can also use `{{negative}}`, `{{seed}}`, `{{width}}` and
     `{{height}}`. Without `{{prompt}}`, Martlet fills in the sampler's prompt
     itself.

If **Connect** fails, make sure that ComfyUI runs, that you started it with
`--listen` and that the address is right.

### Cloud providers

Under **A cloud provider**, choose **OpenRouter** or **NVIDIA Build**:

- **OpenRouter**: any OpenRouter model that draws (on openrouter.ai/models,
  choose output **image**). Martlet asks for one picture of about 1K, in the
  shape that it chose.
- **NVIDIA Build**: NVIDIA's FLUX models (build.nvidia.com), with the same
  `nvapi-` key as NVIDIA Build's chat models.

Paste your API key. Martlet saves it in Windows Credential Manager. When
Thinking already uses the same provider, you can leave the key empty to use
Thinking's key. Tick the box to agree: the description of each picture drawn
there goes to the provider, and each picture may cost money. Then press
**Add**.

The model is part of the place. To draw with another model of the same
provider, add the provider again with that model. A cloud place's **Settings**
can save a key of its own. When you remove the place, its own key goes too.

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

Open **Companion › Background thinking** and use **Look things up on the web**.
It is on by default. It also needs **Think longer about hard questions** on,
a Thinking model that can call tools and an available background-thinking place.

How much she looks up depends on the question:

1. She answers what she knows at once. Most questions need no search.
2. She checks her [app guides](App-Guides) and her earlier reports.
3. For one fact she isn't sure of, such as a price, a date or how to get
   something in a game, she does a **quick look-up**. It has a budget of 3 searches, 9 pages and 4 model steps, with a 10-minute time limit.
   It can finish sooner when a page answers the question.
4. When you ask her to look into something, she does **research**: up to 30
   searches and 60 pages, with notes as she reads. Ask for an in-depth look,
   and she reads about twice as much.

While she researches:

- She tells you as soon as she finds what you wanted, then goes on for the full
  report.
- The report has numbered sources. She keeps it in Creations and offers to show
  it in your browser.
- After the report is ready, a background job can check supported cited claims against their pages.
  It adds a **Source check** when it completes. This is a model check, not proof that every claim is correct.
- Later, ask about it again. She finds the part of the report that answers,
  with its pages.
- One full or deep research job runs at a time, with no time limit.
  Up to two quick look-ups can run beside it. **Cancel** in the talk
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

A creation that a newer Martlet made can be of a kind that this Martlet doesn't
know yet. Update Martlet to use it on this PC. Until then, Martlet keeps it for
your other computers.

The Creations page has no Play button. Ask Martlet instead: "Sing the rain song again",
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
