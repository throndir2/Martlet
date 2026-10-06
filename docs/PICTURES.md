# Pictures

Martlet draws pictures when you ask ("draw me a fox curled up in the snow", "paint our cat as a knight"). It draws in the
background while the conversation carries on, shows the picture in the talk window when it's ready (click it to open it
full size), keeps it in **Creations** on all your Martlet computers, and shows it again later when you ask
(`perform_creation`). Nothing is drawn until you choose where in **Companion › Pictures**.

## Where it draws

Companion › Pictures › *Where it draws* is this PC's own choice (`pictures.json` in the data folder; never shared, since
which machine is free to draw depends on the computer you talk to). The pictures themselves are shared.

| Place | What it is | Cost and privacy |
| --- | --- | --- |
| **Martlet's Pictures role** (recommended) | ComfyUI with [Z-Image Turbo](https://huggingface.co/Tongyi-MAI/Z-Image-Turbo) on this PC or another of your computers with an NVIDIA graphics card (8 GB+, 12 GB+ is faster). **Set up** installs it with the same `martlet-host add` flow as every role and Martlet reaches it through that computer's gateway ([host role details](PICTURES_HOST.md)). | Free and private. About 20 GB of downloads. |
| **My own ComfyUI** | A [ComfyUI](https://github.com/comfyanonymous/ComfyUI) you already run, on this PC or another machine, at its address (for example `http://192.168.1.20:8188`). Start it with `--listen` so other computers can reach it; ComfyUI has no password, so only on your own network. | Free and private. |
| **OpenRouter** | Any model on [OpenRouter](https://openrouter.ai/models?output_modalities=image) that draws, through its image API (`POST /api/v1/images`, one picture at the 1K tier in the chosen aspect ratio). Default `google/gemini-3.1-flash-image`. | Each picture costs money. The description goes to OpenRouter and the model's provider. |
| **NVIDIA Build** | NVIDIA's FLUX models (`POST https://ai.api.nvidia.com/v1/genai/<model>`). Default `black-forest-labs/flux.1-schnell`. | Uses your `nvapi-` key; the description goes to NVIDIA. |

A cloud provider uses Pictures' own key (saved in Windows Credential Manager, bound to that provider) or, without one,
Thinking's key for the same provider (NVIDIA Build's chat and image models share one key). The key is read for each
picture and sent only to that provider.

### ComfyUI workflows

With your own ComfyUI, choose what it runs:

- **Z-Image Turbo**: ComfyUI's own Z-Image Turbo example (9 steps, cfg 1), when ComfyUI has its three files
  (`diffusion_models/z_image_turbo_bf16.safetensors`, `text_encoders/qwen_3_4b.safetensors`, `vae/ae.safetensors` from
  [Comfy-Org/z_image_turbo](https://huggingface.co/Comfy-Org/z_image_turbo)).
- **A checkpoint** from its `checkpoints` folder (Stable Diffusion 1.5 or XL and their fine-tunes; **Connect** lists them).
  An XL-looking name (`xl`, `pony`, `illustrious`...) draws at about 1024 pixels, others at 512-768; turbo/lightning
  checkpoints use 8 steps.
- **My own workflow**: in ComfyUI use *Workflow › Export (API)* and load that file. Put `{{prompt}}` where the description
  goes (and optionally `{{negative}}`; an input that is exactly `{{seed}}`, `{{width}}` or `{{height}}` becomes that
  number). Without `{{prompt}}`, Martlet puts the description into the text node feeding each sampler's positive input,
  sets the samplers' seeds and the empty latent's size. The workflow needs a *Save Image* node.

Martlet checks the workflow's model files are there before it queues, follows the prompt through ComfyUI's queue, and
fetches the saved picture. Cancelling the job (the talk window's Cancel) takes it off ComfyUI's queue or interrupts it.

## In conversation

While pictures are set up and Thinking can call tools, every reply offers one more tool, `draw_picture` (always the same
definition, after the song tools, so prompt caches stay warm):

```json
{"description": "A vivid English description of the whole image", "title": "A short title",
 "shape": "square|landscape|portrait|wide|tall", "avoid": "things to leave out"}
```

It starts a background job (`picture-1`, `picture-2`...; two at a time, 20 an hour, 10 minutes each) and returns at once,
so the reply never waits. Shapes are 1024x1024, 1216x832, 832x1216, 1344x768 and 768x1344. When the picture is ready it
is kept as a `picture` creation, shown in the talk window, and a note tells Martlet so it can say something about it.
`perform_creation` with a picture's id shows it again.

## Check and test

Companion › Pictures › **Check** asks the saved place whether it can draw now (for a cloud provider, only whether a key is
there). **Draw a test picture** draws one picture there and shows it on the page (a cloud provider asks first, since it
costs money). After a picture on Martlet's Pictures role, the desktop frees that computer's graphics card three minutes
after the last picture, so the voice, listening and a local Thinking model get it back.

`MARTLET_PICTURES_FIXTURE=1` before Martlet starts makes it draw with the FIXTURE - NOT AI gradient maker (automated
checks). MCP: `pictures_status` and `pictures_check` ([MCP](MCP.md#pictures)).

## Use

Don't use pictures for anything illegal or harmful or to depict real people without their consent, and say they are
AI-generated when you share them. Z-Image Turbo is Apache-2.0; ComfyUI is GPL-3.0; cloud models have their providers' terms.

## Qualification

Verified on 2026-10-06 on Windows with no usable NVIDIA GPU in that session:

- The targeted unit and relay tests passed.
- `pictures_check` with the fixture maker kept and read back a picture creation.
- Against a real ComfyUI v0.39.0 on the CPU with a Stable Diffusion 1.5 checkpoint:
  - `pictures_check` reported the missing Z-Image Turbo files and the missing checkpoint clearly.
  - It drew a real 1024x1024 picture with a custom Export (API) workflow, kept it and read it back.
  - A run that hit the 10-minute limit was taken off ComfyUI (*Processing interrupted*).
- The desktop's Companion › Pictures:
  - Connect found the version and checkpoint, and it saved the ComfyUI choice.
  - Check said ready, and Draw a test picture started.
- A live conversation with a loopback FIXTURE Thinking server called `draw_picture` and showed the FIXTURE picture in the
  talk window.

**NOT RUN:**

- The Pictures role's Docker image build and Z-Image Turbo on a GPU, because Docker wasn't running.
- OpenRouter and NVIDIA Build with real keys, because each picture costs money.
- A real Thinking model choosing to draw.
