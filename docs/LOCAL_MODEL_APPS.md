# Local model apps

Martlet can think with a model that runs on this PC's own graphics card. This
page tells you which apps Martlet works with, how it finds them and what each
one needs. The user stories are C0, C2, C2a and C2b in
[User stories](USER_STORIES.md#c-thinking).

## Short answers

- **Is Ollama the only local option?** No. Ollama is the one Martlet installs
  and manages for you. Any app that serves an OpenAI-compatible Chat
  Completions API on this PC also works.
- **Do the other apps need their own code in Martlet?** No. Each one boils down
  to an address on this PC, such as `http://127.0.0.1:1234/v1`. Martlet sends
  the same requests to all of them. It only needs to find the app, list its
  models and test one.
- **Which computer runs the model?** The PC where Martlet runs, on its own
  graphics card. For a model on another of your computers, set that computer
  up as a Martlet host (Companion › Thinking › *Another of your computers*).

## Where to choose

1. Open **Companion › Thinking**.
2. Under *Where it runs*, choose **This PC (recommended)**.
3. Under *Model app*, choose one:
   - **Ollama (recommended)**: Martlet installs Ollama, downloads a model that
     fits this PC and keeps it ready. Any Ollama model works: a library name
     (`qwen3:14b`), a Hugging Face GGUF (`hf.co/<user>/<repo>:Q4_K_M`) or a
     model you made with `ollama create`.
   - **A model app you already use**: Martlet lists the apps it finds on this
     PC and their models. Choose the app and the model, then **Test model** and
     **Use**. For an app on another port, choose *Another address on this PC*,
     type the address the app shows and choose **Find models**.

The page looks for apps when it opens. **Look again** looks again. Looking
asks only this PC's loopback address (127.0.0.1), never the network, and it
starts or installs nothing.

## Apps Martlet finds by itself

Martlet asks each default address for the app's model list
(`GET <address>/models`; Ollama: `/api/tags`). An app on another port works
through *Another address on this PC*.

| App | Default address | How to start its server |
| --- | --- | --- |
| Ollama | `http://127.0.0.1:11434/v1` | Martlet starts it. Choose *Ollama* for the managed flow. |
| LM Studio | `http://127.0.0.1:1234/v1` | Developer (or Local Server) › *Start server*. Load a model, or turn on loading models on demand. |
| llama.cpp server (`llama-server`) | `http://127.0.0.1:8080/v1` | `llama-server -m model.gguf --jinja`. `--jinja` lets the model use tools. |
| KoboldCpp | `http://127.0.0.1:5001/v1` | Start it with a model. Its OpenAI-compatible API is on by default. |
| Jan | `http://127.0.0.1:1337/v1` | Settings › *Local API Server* › start. |
| vLLM | `http://127.0.0.1:8000/v1` | `vllm serve <model>` in WSL or Linux on this PC. |
| Lemonade Server | `http://127.0.0.1:13305/api/v1` (older: port 8000) | Start Lemonade Server and load a model. |
| SGLang | `http://127.0.0.1:30000/v1` | `python -m sglang.launch_server --model-path <model>`. |
| text-generation-webui or TabbyAPI | `http://127.0.0.1:5000/v1` | Start text-generation-webui with `--api`, or start TabbyAPI. |
| GPT4All | `http://127.0.0.1:4891/v1` | Settings › Application › *Enable Local API Server*. |
| Docker Model Runner | `http://127.0.0.1:12434/engines/v1` | Docker Desktop › Settings › AI › host-side TCP support. |
| LiteLLM proxy | `http://127.0.0.1:4000/v1` | Start the proxy. It may send requests on to cloud providers. |

Some apps share a port. llama.cpp, LocalAI and llamafile all use 8080, and
vLLM and other servers use 8000. Martlet names the app from the `owned_by`
field of its model list (`llamacpp`, `vllm`). When the list doesn't say,
Martlet calls it *Model app on port 8080*.

Apps that pick a port when they start, such as Microsoft Foundry Local, work
through *Another address on this PC*.

## What Martlet does with the address you type

Martlet turns what you type into the base URL that Thinking uses:

| You type | Martlet uses |
| --- | --- |
| `1234` | `http://127.0.0.1:1234/v1` |
| `localhost:1234` | `http://127.0.0.1:1234/v1` |
| `http://localhost:8080/v1/chat/completions` | `http://127.0.0.1:8080/v1` |
| `localhost:12434` | `http://127.0.0.1:12434/engines/v1` (the path of the app Martlet knows on that port) |
| `192.168.1.20:1234` | Refused: that address isn't this PC |

Martlet refuses an address on another computer. Plain HTTP on the network
would send your conversation and any key without encryption. Use a Martlet
host for another computer, or enter its HTTPS address under *A cloud
provider › Custom OpenAI-compatible server*.

## Test model

**Test model** sends the model one short request, the way a reply does:

- streamed, with the reply length and the *Thinking steps* choice from
  Companion › Replies;
- with one harmless tool offered, because replies offer tools.

The result says how soon the first words came and how long the reply took.
It also says how much context the model takes, when the app says so (LM
Studio, llama.cpp, vLLM and Ollama do). If the app refuses the tool, Martlet
asks again without it, as replies do. The test then passes with a warning:
replies have no tools (memory search, reminders and other tools). For
llama.cpp, start `llama-server` with `--jinja` to fix this.

A test fails, and says why, when:

- nothing answers at the address;
- the app asks for an API key and none is given, or it refuses the key;
- the model answers with nothing, or uses its whole reply length on hidden
  thinking;
- the model takes longer than two minutes.

## Keys

Most apps on this PC need no key. When an app asks for one (vLLM started with
`--api-key`, LM Studio with authentication on), enter the key you set in the
app. Martlet keeps it in Windows Credential Manager, like any other key, and
sends it only to that address.

## Privacy

Your messages go to the app on this PC. The app decides what it keeps, and
whether anything leaves this PC. Some apps, such as a LiteLLM proxy, can pass
requests on to cloud providers. For this reason, *Let Thinking hear my voice*
stays off until you tick it for every app except Martlet's own Ollama.

## When the app stops

Home checks the app that Thinking uses, on this PC's loopback address only.
When it doesn't answer, Home shows *LM Studio isn't ready on this PC*, with
**Check again** and **Change thinking**. It also says so when the app asks for
a key that isn't saved, or no longer lists the model.

## How the parts fit together

| Part | Where |
| --- | --- |
| The app list, address rules, looking, model lists and the test | `src/Martlet.Providers/LocalModelServers.cs` |
| Companion › Thinking › This PC on Windows | `src/Martlet.Desktop/MainWindow.LocalServers.cs` |
| Home's check | `MainWindow.Health.cs` (`LocalServerHealth`) |
| *Find model apps* on macOS and Linux | `src/Martlet.Companion/LocalModelChoice.cs` |
| MCP | `local_model_servers` and the `LocalServer*` automation IDs ([MCP](MCP.md)) |

Replies to a model app on this PC use the same Chat Completions route as any
endpoint. They get the local timing (up to two minutes for the first words,
while the app loads the model), the stable prompt start that keeps the app's
prompt cache, and the chat template switch for *Thinking steps*.
