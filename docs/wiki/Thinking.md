# Thinking

Thinking is the model that reads your message and writes Martlet's reply.

![Companion Thinking](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/companion.png)

## Where it runs

Open **Companion › Thinking**.

| Place | Notes |
| --- | --- |
| **This PC** | Usually Ollama or another local OpenAI-compatible loopback server. Private/no per-request cost, but needs local compute. |
| **Another computer** | Use a paired host with a stronger GPU or loaded model. |
| **Cloud provider** | OpenAI, OpenRouter, NVIDIA Build or custom OpenAI-compatible endpoint. May cost money and sends text/images to that provider. |

## Local Ollama

**Test model** starts Ollama if installed, checks the model is downloaded, loads it and asks a short streamed reply. If a model loads slowly, the talk window shows that status.

## Fallback

**Companion › Thinking › If Thinking fails** saves an optional second destination. It is used once when the main Thinking route fails before text arrives. It receives the same kind of conversation/image data when used; a reply already started is not restarted elsewhere.

## Replies settings

**Companion › Replies › Thinking steps** is Off by default. **Context size** bounds persona, lore, memory, recent conversation and the reply. Larger context can cost more on paid routes.

## Thinking pool

**Companion › Thinking pool** is a shared set of Thinking models for background work: thinking longer, research, screen and sound summaries and other helpers. Each job goes to a free member, so the conversation keeps its own Thinking model at full speed.

Your paired computers with a Thinking model join the pool by themselves when Martlet checks them: a computer's **Thinking pool role** joins with its slots, and a computer's Ollama joins when it doesn't already do this PC's Thinking. You don't need to tick anything when a computer comes online or gets a Thinking model. To keep a computer out, untick **In the Thinking pool** on it; tick it again to add it back. A member that goes offline stays in the pool, and its slots come back when it answers again. Ollama on this PC and cloud providers join only when you add them.

**Thinking longer** lets Martlet start background reasoning on a pool member and bring the result back later.

## Tools and vision

Use a model that supports function calling for tools and image input for Vision. If a model rejects tools, Martlet retries without them and stops offering tools to that model for a while.

More detail: [Setup](https://github.com/throndir2/Martlet/blob/main/docs/SETUP.md), [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md), [Voice latency](https://github.com/throndir2/Martlet/blob/main/docs/VOICE_LATENCY.md).
