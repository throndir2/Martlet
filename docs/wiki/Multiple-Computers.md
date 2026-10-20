# Multiple Computers

Martlet is one app on all your computers. Talk to her on one PC, and let your strongest computers do the heavy work.

![Devices map](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/devices.png)

## Companion PCs and hosts

- A **companion PC** is where you talk to Martlet. It has the microphone, the speakers, the screen and the character.
- A **host** lends its graphics card and processor. It does jobs such as thinking, listening or speaking for your companion PCs.
- One PC can be both: a companion PC can also run its own host service.

To change what a PC is for, open **Settings › What this PC is for** and choose **Use as my companion PC** or **Use as a Martlet host**. The **Your other computers** list on the same card switches your other computers too. On the Devices map, select another computer and choose **Make it a host PC** or **Make it a companion PC**.

### Switch another computer from here

The switch travels with the shared settings, so **Keep Martlet the same on all my computers** must be on (**Devices › Settings for all devices**).

1. Open **Settings › What this PC is for › Your other computers**, or select the computer on the Devices map.
2. Choose **Make it a host PC** or **Make it a companion PC**, then confirm.
3. Wait. The other computer switches within a minute while Martlet runs there, or the next time Martlet starts there. If Martlet is replying there, it switches right after that reply.
4. This PC says on its status line when the other computer has switched.

Until it switches, the button reads **Keep it a companion PC**: choose it to take back your ask. A later choice made on that computer itself replaces your ask, and this PC tells you.

When a computer becomes a host PC, Martlet there stops talking and listening, shows its host dashboard and starts its host service's jobs by itself. It keeps its companion choices, so you can make it a companion PC again from here or at that computer. A computer that has no host service yet does no work for your other computers until someone at it chooses **Set up host service** on its Home (once; Windows may ask to allow it).

When a computer becomes a companion PC again, Martlet there shows the character and listens again as before it became a host, or as its startup choices say. A host service that it runs keeps serving your other computers.

If another computer asks this PC to switch from a newer Martlet, update Martlet on this PC so that it can follow the ask.

To add a host, see [Setting Up a Host](Setting-Up-a-Host).

## Join your Martlet network

Your computers with Martlet form your **Martlet network**. Pair a host once, and every computer in the network can use it.

1. Install Martlet on the new PC.
2. In the welcome tour, choose **Yes, join my Martlet network**. Later, use **Connect to your other computers** on Home, or **Devices › Add a computer › Martlet on your network**.
3. Pick one of your other computers in the list.
4. Make sure that both screens show the same check number.
5. Choose **Allow** on the other computer. **Turn down** refuses the request.

When you choose **Allow**, the other computer gets a one-use code for each host listed. It pairs like a computer that you added yourself, and then it can use those hosts' jobs. Choose **Deny** if you didn't just ask from that computer, or if the check numbers are different.

The new PC then pairs with every host by itself, also with hosts that you add later. It follows your shared settings, so its jobs go to your hosts.

If a computer is not in the list, guest Wi-Fi, a VPN or a firewall can hide it. Use **Enter a pairing code** instead. **Share › Your Martlet network** lists your computers and the computers that ask to join. Away from home, use **Join with an invite** ([Use your hosts from outside your home](Sharing-with-Friends#use-your-hosts-from-outside-your-home)).

## Recommended setup

On **Home**, **Recommended setup** plans the best use of all your computers at once.

- It keeps your gaming PC's graphics card free for your games. Tell it which PCs you play games on.
- It uses the models you already have: models in your model apps (such as Ollama or LM Studio) and models your hosts keep.
- A review shows what changes on each computer and why. **Reconfigure** does all the changes. Each computer shows **Configuring** while it changes.
- Thinking, Listening and Lip-sync start **Locked**: Martlet keeps your choice and only suggests a better one. Clear the box to let Martlet choose.
- When Martlet finds a better setup, the button says **A better setup is available**. Martlet never changes anything by itself.

Change the plan's preferences in the review or in **Settings › Recommended setup preferences**. On a PC by itself, the button runs **Set it all up for me**.

### The preferences

A change to a preference plans again at once. The preferences are the same on all your computers, but each companion PC keeps its own answer about games.

- **Quality**: Martlet picks the smartest Thinking model whose first word comes within the time you choose. **Quick replies** prefers the smaller of two models that are almost the same.
- **Prefer models that hear you**: a Thinking model that hears your voice wins over a smarter one when it is no more than one step behind. When this is off, Thinking gets only the words you said.
- **Host graphics card share**: how much of each host's graphics card Martlet may plan with. Choose less for a host that also does other work.
- **Prefer models your hosts already have**: Thinking uses the best model that your hosts already run or keep, so nothing downloads. Its first word can come later than with a smaller model.

### The review

- Martlet sets up the parts at the top of the list first. The needed parts make a conversation work. An optional part can be off: tick **Off** to plan without it. Vision, Reading, Hearing, People and Smart home turn off on their own Companion pages.
- Companion PCs stay light, so that your games keep their graphics card. Each graphics card runs at most one language model.
- A locked job keeps its model. Martlet only suggests a clearly better one: smarter at the same speed, or as smart with less memory. **Use this suggestion** plans that suggestion once, and the job stays locked. A job that you change by hand locks again.
- When a change needs a key, type it in the review. Martlet skips a change whose key you leave empty, and that computer shows that it needs attention.
- When Ollama must download a model, the model's own license applies. Ollama must be installed on the computer first.
- Your other companion PCs follow this PC's Thinking and Listening through the shared settings once they can.
- Online services get the data that the review lists for each part, and their requests can cost money.

### While you play or when a host is away

The review shows three plans: **Normal**, **While gaming** and **Host away**. Martlet changes between them by itself: while a game runs on a companion PC that you use for games, and while a host doesn't answer. Each job comes back when the game ends or the host answers again.

### When Martlet can't make a change

- **Reconfigure** shows its progress in **Background tasks**. Check the recommended setup again when it is done. If Martlet closed before a change was made, check again too.
- Martlet makes changes on other computers only through their host service. Make the change at that computer, or connect it on the **Devices** map and set how Martlet reaches it.
- A computer that isn't paired with this PC pairs by itself once it is in your Martlet network. Check again then.
- When **Keep Martlet the same on all my computers** is off on this PC, who does what can't change on your other computers. Turn it on in **Devices**, then choose **Reconfigure** again.
- When a host's role doesn't offer a model yet, update Martlet on that host, then check again.

## The Devices map

Open **Devices** to see all your computers: this PC in the middle, your other computers, your hosts and the cloud services your jobs use. Choose **Map** or **List**. The list shows by itself when the map is too full, and it has a **Find** box.

Select a computer to see what it does, whether it answers and its Martlet version. Its actions let you give it a job, install, change or remove a role, update it and show its status.

- **What your computers can run** shows how much graphics memory, memory, processor and disk each part uses, from Martlet's estimates.
- **Cloud providers and servers** lists every online service and server that Martlet uses, and what it uses each one for: Thinking, the Thinking pool, Picture making, Singing, Vision, tools and more.
- **Sharing work** sums up which machines do each job.

## Each job has a list of machines

Each job (voice, listening, lip-sync, reading, seeing, hearing, pictures and singing) has a list of machines, in order, on its Companion page. Add this PC, one of your computers, a graphics card or a cloud provider, and turn each one on or off.

- The first machine does the job. When it is busy or offline, the next one takes over. The lists are the same on all your computers.
- **Companion › Thinking** lists the computers that may answer when your Thinking model is busy. The list is empty by default. Another computer's model has no prompt cache for your conversation, so it answers later and pushes its own conversation out of its cache. While the list is empty, a reply waits for its own computer.
- In the Thinking pool, untick a computer to keep every companion PC's pool jobs off it. A computer that you keep for one companion PC takes no Thinking pool work from the others. In the other lists, each machine has its own **Every companion PC** or **Only** choice.
- A job that a host does can have **Fail over automatically**. When the host stops answering for about 30 seconds, the job moves to another of your hosts with the same engine.
- Work goes only to machines that you put on a list or chose as a backup. Martlet never moves your work to a cloud service by itself.

When a computer stops answering for 30 seconds, Home says **Working with less** and tells you what moved. Home also tells you when the computer is back.

## Keep Martlet the same on all my computers

**Devices › Settings for all devices › Keep Martlet the same on all my computers** is on by default. It brings these to every computer:

- Who does each job, and how Martlet thinks, listens and speaks, with your API keys.
- Your personality, replies, prompts, lorebooks, character, theme and the other settings.
- Memories, your conversation history, speaking voices, characters, creations and Home Assistant.

Everything travels only through your paired hosts. Nothing goes to a cloud service.

Some things are always shared, also when the switch is off: the people Martlet recognizes by voice (**Companion › People**), your household's accounts, the computers in your network and the logs. Each computer keeps a week of every computer's logs.

Some things stay on each computer, because they describe that computer: its microphone, speakers, cameras and screens, where the character sits, whether it is a companion PC or a host, how Martlet starts, the models and tools installed there, the terminal, and the Vision, Hearing, Reading and Picture making lists.

Each person's own settings go only to the computers where that person is signed in. See [Accounts and Household](Accounts-and-Household).

If your computers disagree, choose **Use this PC's settings on all my computers**. A computer that cannot use a shared choice yet (for example, a model is not installed there) keeps its own choice and says why.

### Use this PC's settings on all my computers

This button copies this PC's settings to every paired computer and replaces what they have. It copies how Martlet thinks, listens and speaks (with this PC's API keys), the character with its emotes and motions, the personality, replies, prompts, lorebooks, how you talk, the theme, Voice ID, recognizing voices, what Martlet may do with Home Assistant, app updates and what Thinking models hear and see.

With accounts, your own settings (personality, replies, prompts, lorebooks, the character, how you talk, the theme and Voice ID) go only to the computers where you are signed in. Other people's settings stay as they are.

### The sync status lines

Under the switch, Devices shows how sync goes:

- **Sync is on** tells how many of your hosts have the same choices of who does what, and when Martlet last checked.
- **Settings** tells how many settings are shared, on how many hosts they are the same, and what this PC last took from another computer.
- A host that is not responding is counted. Update a host that runs an older Martlet, so that it can share settings and each person's settings.
- With accounts, your own settings start to sync when this PC finishes switching to you.

### Not followed here yet

This line names each shared setting that this PC cannot use yet, and why. This PC keeps its own choice and tries again. To fix the usual causes:

- **Chosen, made or saved on a newer Martlet**: update Martlet on this PC.
- **A character from VRoid Hub**: link VRoid Hub on this PC in **Account › Linked services**.
- **A character's model file isn't on this PC**: add the character in **Companion › Character › Your characters** on the computer that has the file. Martlet then copies it to your other computers.
- **Ollama isn't installed, or doesn't have the model**: set up Ollama or download the model in **Companion › Thinking**.
- **Windows voices were removed**: choose another voice for this PC in **Companion › Voice**.
- **A job runs on a host now**: this PC switches to the shared choice when the job comes back to this PC.
- **The Thinking pool file can't be read or saved**: open **Companion › Thinking pool** to set it up again, and check that Martlet can write to its data folder.

### When settings can't be read or saved

- **Saved by a newer Martlet**, or **cannot be saved by this older Martlet**: update Martlet, or restore a compatible backup.
- **Damaged or too large**: restore a compatible backup, or fix the settings file.
- **Changed since you opened them**: another window or computer saved first. Open the page again, check your changes and save again.
- **Cannot be accessed**: check the data folder's permissions and free space, and close other Martlet windows that use the same data folder.
- **The key was not saved** or **could not save the cleanup**: open Setup again and finish or remove the waiting key first.

## Update every host from your companion PC

- The Devices map shows **Update available** on a host with an older Martlet. Select the host and choose **Update host**.
- **Settings › App updates › Update hosts** updates all your hosts at once.
- **Update paired hosts automatically**, on the same card, keeps your hosts on this PC's version.

A PC that runs Martlet as a host updates its own host service after Martlet updates itself.

## Share with friends

The **Share** page shares your hosts with friends, and your language models with other people's apps. See [Sharing with Friends](Sharing-with-Friends).

More detail: [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md), [One Martlet on all your computers](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md), [Resource footprints](https://github.com/throndir2/Martlet/blob/main/docs/RESOURCE_FOOTPRINTS.md), [Diagnostics](https://github.com/throndir2/Martlet/blob/main/docs/DIAGNOSTICS.md), [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md).
