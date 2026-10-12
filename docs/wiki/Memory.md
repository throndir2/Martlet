# Memory

Martlet remembers lasting facts and keeps a searchable conversation history, the same on all your computers.

## Defaults

Memory is **ON by default** for new profiles and most migrated profiles. A current explicit OFF stays off. Configuration restore forces memory off for review.

Open **Companion › Memory**.

## Facts

The Memory window lets you turn memory on/off, choose the store location, list facts, add facts, choose retention, inspect provenance, edit/delete facts, purge expired facts and export after preview. **Show replaced facts** also lists facts that stopped being true: when a fact is corrected, Martlet keeps the old version as history with the date it was replaced, and never recalls it. Deleting a fact deletes its old versions too.

## Recall and remembering

For accepted typed, push-to-talk or hands-free turns, Martlet queries a local lexical index and sends matching facts in notes. After a completed reply, the same Thinking model can make one extra text-only request to extract lasting facts. Paid providers may charge for that extra request.

Each fact can have hidden search words (for "The user is vegetarian": diet, food, meat, cooking), so a question in other words still finds it. They are never sent as the fact's text. When you have a Thinking pool, a free pool member also tidies memory in the background: it adds missing search words, merges facts that say the same thing and keeps a fact a newer one made outdated as history. It never uses the model you talk to.

Screen/camera glances and PC-audio notes are not remembered as facts.

## Conversation history

The **Conversations** page in the side menu lets you browse, edit or delete recorded exchanges; **Companion › Memory › Conversation history** turns the record on or off. Optional `search_conversations` lets Martlet search it during replies.

Your record is the same on every computer where you are signed in. Martlet recalls what you said on any of them, and an edit or a deletion on one computer reaches the others. A deleted message never comes back.

## Sync

With **Keep Martlet the same on all my computers** on, memories and your record of conversations sync through paired hosts. Each person's record goes only to the computers where that person is signed in. There is no cloud memory service by default.

## Sharing memories with your household

When several people use Martlet, each person has their own memories, and the household has shared memories that every character knows.

- In the Memory window, select facts and use **Share selected with**. Choose **The household's memories**, another person, or a character you share together, then choose **Copy** or **Move**. Facts for another person go through your paired hosts, which never show you that person's memories.
- Tick **Share new memories about me with the household** to let every character in the household remember new things Martlet learns about you.
- A character shared together remembers everyone it talks to in its own memories. Your other characters don't see them.

More detail: [Memory](https://github.com/throndir2/Martlet/blob/main/docs/MEMORY.md), [Conversation](https://github.com/throndir2/Martlet/blob/main/docs/CONVERSATION.md), [Cluster](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md).
