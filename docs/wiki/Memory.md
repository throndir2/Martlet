# Memory

Martlet remembers what matters to you and picks up earlier conversations. Everything stays on your own computers, and it is the same on all of them.

## Turn memory on or off

Memory is on from the start. Open **Companion › Memory** and press **Manage memory**. In the Memory window, **Remember facts from conversations** turns memory on or off.

## How she remembers

After each reply, Martlet keeps the lasting facts from what was said, and she knows whose each fact is. When you talk to her, she recalls the facts that match what you say. She finds them also when you use another form of a word ("cats" finds your cat), and in Spanish, French, German, Portuguese, Italian, Chinese, Japanese and Korean too.

- **Hidden search words**: each fact gets search words (for "The user is vegetarian": diet, food, meat, cooking), so a question in other words still finds it. They are never sent as the fact's text.
- **Finding by meaning**: a small embedding model also finds facts that say the same thing in other words. It runs in Ollama on this PC's processor. Choose it on **Companion › Memory › Finding by meaning**. It is on by default, and Martlet asks before it downloads the model. Without the model, she finds by words only.
- **Memory brief**: Martlet writes a short profile of you and the people you talk about, and sends it with the first message of each conversation. See it, change it with **Save my changes**, or press **Write it again** on **Companion › Memory › Memory brief**. Untick **Send a short profile of me with the first message of each conversation** to turn it off.
- **Tidying**: when you have a [Thinking pool](Thinking#thinking-pool), a free pool member tidies memory in the background. It adds missing search words, merges facts that say the same thing and keeps an outdated fact as history. It never uses the model you talk to.

Remembering runs in the background, so replies never wait for it. When it uses a paid provider, the extra request can cost money. Screen and camera glances and what your PC plays are never remembered.

## The Memory window

In the Memory window you can:

- **Search** your facts and see where each one came from.
- **Add fact**: type the **Fact**, choose whom it **Belongs to**, and choose how long to **Keep it**.
- Change a fact and **Save changes**, or use **Delete selected**, **Delete all shown** or **Delete everything**.
- **Show replaced facts**: when a fact is corrected, Martlet keeps the old version as history with the date it stopped being true, and never recalls it as current. Deleting a fact deletes its old versions too.
- **Where memory is stored**: use Martlet's folder or a folder you choose.
- **Export to a file**: preview the export, then create it on this PC. Martlet never uploads it.

You can also ask Martlet herself what she remembers, or ask her to remember, correct or forget something. Martlet keeps up to 100,000 facts. When memory is full, the talk window says so, so you can choose what to delete.

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
