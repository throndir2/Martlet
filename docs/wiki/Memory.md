# Memory

Martlet remembers what matters to you and picks up earlier conversations. Everything stays on your own computers, and it is the same on all of them.

## Turn memory on or off

Memory is on from the start. Open **Companion › Memory** and press **Manage memory**. In the Memory window, **Remember facts from conversations** turns memory on or off.

## How she remembers

After each reply, Martlet keeps the lasting facts from what was said, and she knows whose each fact is. When you talk to her, she recalls the facts that match what you say. She finds them also when you use another form of a word ("cats" finds your cat), and in Spanish, French, German, Portuguese, Italian, Chinese, Japanese and Korean too.

- **Hidden search words**: each fact gets search words (for "The user is vegetarian": diet, food, meat, cooking), so a question in other words still finds it. They are never sent as the fact's text.
- **Finding by meaning**: a small embedding model also finds facts that say the same thing in other words. See [Finding by meaning](#finding-by-meaning).
- **Memory brief**: a short profile of you goes with the first message of each conversation. See [Memory brief](#memory-brief).
- **Tidying**: when you have a [Thinking pool](Thinking#thinking-pool), a free pool member tidies memory in the background. It adds missing search words, merges facts that say the same thing and keeps an outdated fact as history. It never uses the model you talk to.

Remembering runs in the background, so replies never wait for it. When it uses a paid provider, the extra request can cost money. Screen and camera glances and what your PC plays are never remembered.

**Companion › Memory** shows if memory is on. When it is off, Martlet doesn't remember or recall anything between conversations. Press **Manage memory** to turn it on.

### Finding by meaning

An embedding model turns each memory, guide section and report section into numbers that show what it means. Then Martlet also finds the ones that say the same thing in other words: "what should I cook?" finds "You are vegetarian".

- It runs in Ollama on this PC's processor. It never takes the graphics card from the conversation's model, and nothing leaves this PC.
- Martlet uses it only away from the start of its answers: when it remembers after a reply, when it tidies your memories, and when it looks something up with its memory and guide search tools. So it never makes a reply start later.
- Choose it on **Companion › Memory › Finding by meaning**. It is on by default. The choice is the same on all your computers; each computer makes its own search data.
- The model box names the embedding model in Ollama. **nomic-embed-text** (the default) is small and quick on a processor. **embeddinggemma** also reads other languages well.

The status line tells you what to do:

- **Not ready: ... isn't downloaded**: press **Download**. Martlet asks first, then downloads the model with Ollama on this PC. The model's own license applies. If Ollama isn't installed, install it on **Companion › Thinking** first.
- **Waiting** or **Not ready** with another reason: Martlet finds by words only until the model can load. It checks again in a few minutes, or press **Check again**.
- **It was chosen on a newer Martlet**: another computer has a newer Martlet. Update Martlet on this PC to use the same choice.

Without the model, Martlet finds by words only.

### Memory brief

Martlet writes a short profile of you and the people you talk about, from what it remembers. It sends the profile with the first message of each conversation, so it knows the important things about you even when nothing you said matches them.

- Martlet writes the brief once it remembers at least 3 things. It writes it again when your memories change, on a Thinking pool member (or the Thinking model in the cloud, once you aren't talking), never while it answers you.
- When a memory that the brief came from changes or goes away, Martlet doesn't send the brief until it writes it again.
- The brief is the same on all your computers. If another computer has a newer Martlet, update Martlet on this PC to use its brief.
- Change how it is written in **Companion › Prompts › Writing the memory brief**. When that prompt is empty, Martlet writes no brief.

On **Companion › Memory › Memory brief**:

1. Read the brief in the box.
2. To change it, edit the text and press **Save my changes**. Martlet keeps your own text as you wrote it and doesn't change it on its own.
3. To let Martlet write it from your memories again, press **Write it again**, or clear the box and press **Save my changes**.
4. To stop sending it, untick **Send a short profile of me with the first message of each conversation**.

## The Memory window

In the Memory window you can:

- **Search** your facts and see where each one came from.
- **Add fact**: type the **Fact**, choose whom it **Belongs to**, and choose how long to **Keep it**.
- Change a fact and **Save changes**, or use **Delete selected**, **Delete all shown** or **Delete everything**.
- **Show replaced facts**: when a fact is corrected, Martlet keeps the old version as history with the date it stopped being true, and never recalls it as current. Deleting a fact deletes its old versions too.
- **Where memory is stored**: use Martlet's folder or a folder you choose.
- **Export to a file**: preview the export, then create it on this PC. Martlet never uploads it.

You can also ask Martlet herself what she remembers, or ask her to remember, correct or forget something. Martlet keeps up to 100,000 facts. When memory is full, the talk window says so, so you can choose what to delete.

The Memory window has no Save button. Your choices save as soon as they change. A typed folder saves when you press Enter or leave the box. Deleting facts can't be undone. **Delete everything** deletes your own memories on all your computers; the household's memories and memories shared with you stay.

### Export to a file

1. Open **Export to a file** and press **Preview export**. Martlet shows the JSON that it will write. Nothing is exported yet.
2. In **Save export as**, type a new file name or press **Browse**.
3. Tick **Create this local export** and press **Create export**.

Martlet writes the file on this PC and never uploads it. It never overwrites a file: if the file already exists, choose a new name. If memory changes after the preview, preview the export again. Keep the file private, because it has everything Martlet remembers.

### When the Memory window shows a problem

| Message | What to do |
| --- | --- |
| Your memory settings aren't saved yet | Finish the folder (press Enter) or wait a moment, then try again. |
| Memory is off | Tick **Remember facts from conversations**. While memory is off, your saved facts stay on this PC, but you can't see or change them. |
| Memory changed, or Couldn't edit/delete/share the facts | Press **Refresh** and try again. Another computer or a conversation changed memory meanwhile. |
| Memory is in use, or Another Martlet action is still finishing | Wait a moment and try again. |
| Memory cleanup is pending | Check that Martlet can write to the memory folder, then press **Retry cleanup**. |
| Memory storage access was denied, or Memory storage is unavailable | Choose a folder you can write to, and check the free space on its drive. |
| The memory store is damaged | Keep the damaged folder (it can still hold your facts), and choose another folder in **Where memory is stored** if you need memory now. |
| Memory action canceled | Press **Refresh** if the window looks out of date. |

## Conversation history

The **Conversations** page in the side menu lets you read, search, edit and delete every conversation. **Companion › Memory › Conversation history** has two choices:

- **Keep a record of my conversations** (on by default).
- **Let Martlet search the record on its own** (off by default). It needs a Thinking model that uses tools.

Mention an earlier talk ("remember when...", "what did we talk about yesterday?") and she finds what was said, by your words, the time you name and who said it. Your record is the same on every computer where you are signed in. An edit or a deletion on one computer reaches the others, and a deleted message never comes back. Screen glances and what your PC plays are never recorded.

## On all your computers

While **Keep Martlet the same on all my computers** is on, memories and your record of conversations sync through your paired hosts. Each person's memories and record go only to the computers where that person is signed in. There is no cloud memory service. See [Multiple Computers](Multiple-Computers).

## Sharing memories with your household

When several people use Martlet, each person has their own memories, and the household has shared memories that every character knows.

- In the Memory window, select facts and use **Share selected with**. Choose **The household's memories**, another person, or a character you share together, then choose **Copy** or **Move**. Facts for another person go through your paired hosts, which never show you that person's memories.
- Tick **Share new memories about me with the household** to let every character in the household remember new things Martlet learns about you.
- A character shared together remembers everyone it talks to in its own memories. Your other characters don't see them.

See [Accounts and Household](Accounts-and-Household).

More detail: [Memory](https://github.com/throndir2/Martlet/blob/main/docs/MEMORY.md), [Search engine](https://github.com/throndir2/Martlet/blob/main/docs/SEARCH.md), [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Cluster](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md).
