# Thinking

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Users
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Desktop/Pages/Thinking/ThinkingCards.cs`, `src/Martlet.Desktop/Pages/Thinking/ThinkingCards.LocalThinking.cs`, `src/Martlet.Desktop/Pages/Thinking/ThinkingCards.LocalServer.cs`, `src/Martlet.Desktop/Pages/ThinkingPool/ThinkingPoolPage.cs`, `src/Martlet.Desktop/Pages/Replies/RepliesPage.cs`, `src/Martlet.Desktop/Pages/CheckIns/CheckInsPage.cs`, `src/Martlet.Conversation/ThinkingPool.cs`

Thinking is the model that reads your message and writes Martlet's reply. Set it up on **Companion › Thinking**.

## Choose where it runs

Martlet can use a model on this PC or a configured online service. Your available choices depend on your setup. A model app can also decide whether it sends requests outside this PC.

## Ollama on this PC

Ollama runs a model on this PC. Martlet shows model suggestions and models that Ollama has downloaded.

1. Open **Companion › Thinking** and find the **Ollama on this PC** card.
2. Select a model, or select **Another Ollama model** and enter its name.
3. Select **Install Ollama and use it** if Ollama is not installed. Otherwise, select **Download model** when needed.
4. Select **Test model** to check it. Select **Use Ollama on this PC** to use it for Thinking.

You can also select **Use a model file you downloaded...** to add a GGUF file to Ollama. Ollama keeps a copy of that file.

## Use a model app you already run

Select a model app under **Model app**. Martlet can find supported apps on this PC. You can also enter an app's local address. Start the app's local server first.

Select **Find models**, choose a model, and select **Test model**. Select **Use this app** to save the route. Enter an API key only when the app asks for one. The app controls how it handles your requests.

## Reply settings

**Companion › Replies** controls how the Thinking model answers. It includes Thinking steps, Short first sentence, and other reply settings. Some settings may not apply to every model.

## Thinking pool

**Companion › Background thinking** sets up the Thinking pool. The pool runs background jobs on selected machines. It is separate from the model that answers your live conversation. **Thinking requests** shows the pool's current and recent jobs.

Some jobs need a model that can see pictures, hear recordings, or use tools. Martlet uses a pool member that supports the job. See [Multiple Computers](Multiple-Computers) and [Listening and Voice ID](Listening-and-Voice-ID).

## Check-ins

**Companion › Check-ins** lets you configure built-in and custom check-ins. Check-ins use the Thinking pool. See [Multiple Computers](Multiple-Computers).

More detail: [Local model apps](https://github.com/throndir2/Martlet/blob/main/docs/LOCAL_MODEL_APPS.md), [Hosted Thinking](https://github.com/throndir2/Martlet/blob/main/docs/HOSTED_THINKING.md), [Model catalog](https://github.com/throndir2/Martlet/blob/main/docs/MODEL_CATALOG.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md).
