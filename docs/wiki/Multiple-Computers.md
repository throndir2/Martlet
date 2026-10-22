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

### When your other computers can't find this PC

- Both computers must be on your home network. A PC without a private network address (10.x, 172.16-31.x or 192.168.x) can't be found.
- When Windows treats the network as **Public**, other computers can't find this PC. Choose **Let my other computers reach this PC**. Martlet marks the network as **Private** and opens port 9444 to your local network. Windows asks for administrator approval once.
- A PC answers one computer at a time. If it is already answering another computer, try again in a minute.
- Both computers must run the same Martlet version. Update both, then try again.
- After you choose **Allow**, a run window sends one-use pairing codes for your hosts. If sharing stops, the run window shows why.

### Allow or remove a computer

- **Allow** a computer only when it shows the same check number as the request. It can then use every host in your network, and it pairs with them by itself. You can remove it later.
- A computer that pairs with this PC's own host service, or signs in with a sign-in that you allowed, joins by itself. It needs no second **Allow** and no check number.
- **Remove from network** on a computer: every host revokes it, and it forgets your hosts. To come back, it must ask again and be allowed.
- **Remove from network** on a host: it stops trusting your computers, and they forget it. Pair it again with **Add a computer** to bring it back.
- A PC that waits to join shows a check number. Open **Share** on another of your computers and allow it there.
- A PC that was removed can pair a host of that network again to ask to join, or pair a new host to start a new network.
- A host PC has no network of its own. It joins the network of the main PC that pairs with its host service. Its list shows the computers that use its hosts.
- Hosts that run an older Martlet can't share the network. Update them with **Update host**.

### Outside addresses

**Outside addresses** tell your computers how to reach a host when they are not at home.

1. In **Share › Your Martlet network**, choose **Outside addresses** on a host.
2. Type up to four addresses, separated by commas. Use an overlay network address (Tailscale, ZeroTier or WireGuard; recommended), or your router's public name with a forwarded port.
3. Leave the box empty to remove them.

Martlet tries the home address first and the outside addresses only when home doesn't answer. It always checks the same host key, so a proxy that ends TLS (an HTTP tunnel) won't work. With outside addresses set, the host limits failed attempts and pairing codes from every address.

### Let your other computers manage this PC

**Settings › Your other computers › Let my other paired computers update Martlet here and manage this PC's host service** lets your other computers update Martlet on this PC and manage its host service (roles and status). It is on by default.

- When it is off, or when this PC's host service isn't running (for example, Docker Desktop isn't started), commands from your other computers wait.
- A PC without a host service only updates itself, as set in **Settings › App updates**.
- Several commands can run side by side. A change waits while the host does another change; send it again when that finishes.
- An update downloads Martlet from its GitHub Release and checks it against GitHub's SHA-256 digest. The installer isn't code-signed, so the digest finds a damaged download, not who published it.
- Martlet installs the update beside the running one, restarts in a few seconds and then updates its host service. The pairings and roles stay. The host service stops answering for a moment while it restarts.
- If Martlet restarts while a command runs, send the command again.
- When another Windows user of this PC makes it a host or a companion PC, Martlet follows that choice. Home then shows what the host service still needs.

### Keep it the same and fail over

- **Keep Martlet the same on all my computers** shows how many hosts have the newest choices. When it is off, this PC keeps its own choices, settings and memories. The people Martlet recognizes by voice stay shared.
- **Fail over automatically** moves a job to another paired host when its host stops responding. That host gets the job's data, and for speaking also the selected voice.
- A job that moved says where it came from. Choose the first host again to move it back.
- When your other computers use this PC's host service for a job that it doesn't run yet, add that role to the host service.

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

### When a part is off or has no room

The review lists every part of Martlet, where it runs and why. A part can be off for these reasons:

- **You turned it off on its page.** Vision, Hearing, Reading and People (voice recognition) turn on again on their own Companion pages. When a role for the part still runs on a computer, it stays there for your other companion PCs.
- **No computer has room for it.** When no computer has room for the voice engine you chose, Martlet speaks with Chatterbox Nano instead. To use your engine again, free room on a computer for it, then choose it in **Companion › Voice**.
- **No computer has room for a Thinking model, and no free key is saved.** Martlet can't reply until you set one up. A free key from NVIDIA Build is enough.
- **Smart home** isn't set up, or this PC isn't connected to Home Assistant. Home Assistant runs your home all the time, and Martlet never moves it.

For Deep thinking, the review can suggest a smarter model online. Add it in **Companion › Background thinking**. What it thinks about then goes to that service.

When Martlet turns off a role on a computer, its downloads stay there, so turning it on again is quick. A role that is already downloaded starts again without a new download.

## The Devices map

Open **Devices** to see all your computers: this PC in the middle, your other computers, your hosts and the cloud services your jobs use. Choose **Map** or **List**. The list shows by itself when the map is too full, and it has a **Find** box.

Select a computer to see what it does, whether it answers and its Martlet version. Its actions let you give it a job, install, change or remove a role, update it and show its status.

- **What your computers can run** shows how much graphics memory, memory, processor and disk each part uses, from Martlet's estimates.
- **Cloud providers and servers** lists every online service and server that Martlet uses, and what it uses each one for: Thinking, the Thinking pool, Picture making, Singing, Vision, tools and more.
- **Sharing work** sums up which machines do each job.

### Machine cards

Select a computer on the map to open its card.

- **What it does** has one row for each job or role of the computer, with the buttons that change it.
- **Resources** shows what the jobs plan to use, from Martlet's estimates. A range is what the jobs usually hold, then the most they take while they work hardest. **In use now** is what the device reports.
- **Details** shows the hardware and the Martlet version. When a computer hasn't reported them yet, choose **Check connection**. A version older than this PC's shows **Update available**; a newer one means that you should update this PC.
- A Thinking pool model on a host's second or third graphics card is a Thinking pool member of its own. A host with a Thinking pool model joins the pool by itself, unless you keep it out in **Companion › Background thinking**.
- Home Assistant on a host runs on the host's own network. Set it up and manage it in **Companion › Smart home**.
- **Conversation model** shows when Thinking isn't chosen yet. Martlet needs it to answer you. Run a free model on your own computers (on a graphics card, or on the processor when there is none), or choose an online one.
- **Connection method** sets how this PC reaches a host to install or remove roles and update it. **Through Martlet on that computer** (the default) sends the commands over the pinned, signed connection that you paired. Martlet there must be running; until then, the commands wait. Use **SSH** for a Linux computer without Martlet.

What the card says about other computers:

- **In your Martlet network**: one of your computers. A host of your network that isn't paired with this PC yet pairs by itself once this PC reaches it.
- **Asks to join**: allow it in **Share › Your Martlet network** when it shows the same check number.
- **Uses your hosts but isn't in your Martlet network**: it doesn't see your other hosts or computers. Update Martlet on it; it then asks to join by itself, and you allow it on the **Share** page.
- **A friend's computer**: it signed in to your host as a friend and uses only that host's engines (thinking, listening, speaking, lip-sync, reading). It never joins your Martlet network, and your own work comes first. Stop sharing in **Share › Friends**.
- **Shared by a friend**: a friend shares this host with this PC only. It never joins your Martlet network, your other computers don't use it, and its owner's work comes first. **Share › Hosts shared with this PC** lists it.
- A computer on an older Martlet doesn't say whether it is a companion or a host PC. Update it to switch it from the map.

### Add a computer

The **+** card on the map has three choices:

- **Add a computer**: pair another computer once with a one-use code on your private network, so it helps with thinking, speech or lip-sync.
- **Prepare a Linux computer**: get a Linux PC ready for host jobs over SSH. Martlet checks it first, then changes only what you select.
- **Run host services on this PC**: when you have no other computer, let this PC do thinking, speech or lip-sync itself.

### When a computer is away

When a host stops answering, the map shows **Not answering** and how long. Home says that Martlet works with less and tells you what moved. When the computer answers again, Home tells you that it is back.

## Each job has a list of machines

Each job (voice, listening, lip-sync, reading, seeing, hearing, pictures and singing) has a list of machines, in order, on its Companion page. Add this PC, one of your computers, a graphics card or a cloud provider, and turn each one on or off.

- The first machine does the job. When it is busy or offline, the next one takes over. The lists are the same on all your computers.
- **Companion › Thinking** lists the computers that may answer when your Thinking model is busy. The list is empty by default. Another computer's model has no prompt cache for your conversation, so it answers later and pushes its own conversation out of its cache. While the list is empty, a reply waits for its own computer.
- In the Thinking pool, untick a computer to keep every companion PC's pool jobs off it. A computer that you keep for one companion PC takes no Thinking pool work from the others. In the other lists, each machine has its own **Every companion PC** or **Only** choice.
- A job that a host does can have **Fail over automatically**. When the host stops answering for about 30 seconds, the job moves to another of your hosts with the same engine.
- Work goes only to machines that you put on a list or chose as a backup. Martlet never moves your work to a cloud service by itself.

When a computer stops answering for 30 seconds, Home says **Working with less** and tells you what moved. Home also tells you when the computer is back.

### The order of the list

The list on **Companion › Voice** and **Companion › Listening** shows the machines that do the job, in order.

- The first machine that is on does the job. The next ones help when it is busy.
- Use **Up** and **Down** to change the order.
- To turn the job off, turn off every machine in the list, or remove them all.
- Each machine keeps its own settings, such as its engine or model. Select **Settings** under a machine to change them.

### Add a cloud provider to a list

1. Under the list, select **A cloud provider**.
2. Set up the provider, its model and your key in the card below the list.
3. Select **Use**. The provider goes to the top of the list.
4. To use your computers first again, move the provider down with **Down**.

Your key from before stays saved. Leave the key box empty to use it again, or paste a new key. Martlet keeps keys in Windows Credential Manager. A key from NVIDIA Build starts with `nvapi-`.

For a server of your own, type its HTTPS base URL and the exact model ID. For a model app on this PC, such as LM Studio or llama.cpp, choose **This PC › Model app** instead. When a provider stops serving your model, choose another model.

### Where each job can run

Each job page shows where the job runs now, under **Now**. When nothing is chosen yet, Martlet recommends a place.

- **This PC**: Thinking uses Ollama or your own model app on this PC's graphics card. Listening uses Parakeet on the processor, with no setup, or Whisper on the graphics card. Your messages, your voice and the audio stay on this PC, and there is no cost.
- **Another of your computers**: a paired computer does the job with its own graphics card, so this PC stays free. Your messages, your speech or the reply text go to that computer, not to a cloud provider. That computer doesn't keep your speech. There is no per-request charge.
- **A cloud provider**: NVIDIA Build (with a free key), OpenAI, OpenRouter and others. What the job needs (your messages, your recordings or the reply text) leaves this PC, and requests can cost money. You tick a box to agree before Martlet uses the provider.

A voice engine on another computer can have a model licence of its own. The **?** under the list of your computers names it. XTTS-v2, for example, allows only noncommercial use, and Chatterbox puts an inaudible watermark in every reply.

When no microphone or speakers are found, plug them in or turn them on in Windows Sound settings. You can type instead of talking.

### Hand a job to another computer

Select **Use it** next to a computer on the job's page. Martlet asks once before it moves the job.

- When the computer doesn't run the needed role yet, Martlet installs the role there and shows the progress in a run window. The job stays where it is until the role is ready. Then the job moves by itself.
- The question says what the role needs, such as Docker or an NVIDIA graphics card. When Martlet can't tell if the computer has that, it says so.
- Only one voice engine runs on a computer. A new engine stops the other engines there to free the graphics card's memory. Their downloads stay, so switching back is quick. When you switch the engine on the computer that speaks now, speaking pauses until the new engine is ready.
- When speaking leaves a computer, its voice engine stops there. Other computers that still speak with that engine stop speaking until they choose another engine.
- A phone or tablet hosts only while Martlet is open on its screen. Keep it on a charger with the app open, and turn on failover for its jobs.
- When a computer can't set up roles from here, turn the job on in Martlet on that computer.
- An open conversation switches over before its next reply.

**A computer that a friend shares with you** works for this PC only. Your other computers keep your network's choice. The friend's own work comes first, so their computer can turn this PC away while it is busy. Then Martlet uses another of your computers that runs the job, or tries again with the next request. For speaking, the voice's recording goes with each sentence, and the friend's computer keeps nothing. Only the friend can add a job to their computer.

To give a job back to your Setup choice, choose it in the list of who does the job. When that choice is a cloud provider, your data goes there again, and requests can cost money. When the saved key is missing, save it again in Setup.

### When no machine can do a job

Home and the job's page say **Needs attention** when a job doesn't work. Select the **?** to see what it means for you.

- When nobody does **Thinking**, Martlet can't reply. When nobody does **Listening**, you can still type. When nobody does **Speaking**, replies show as text only.
- Each problem has buttons that fix it. **Check** asks the computer again, and **Use ... instead** gives the job back to the choice you made before.
- When you remove a role or forget a computer, Martlet first says what happens to each job. The job moves to another computer by failover, goes back to your Setup choice, or nobody does it. Then choose another place in the job's Companion page or on the **Devices** page.
- When you remove a Thinking pool role, the pool stops running jobs on that computer. If the pool becomes empty, add another member in **Companion › Background thinking**.
- A Parakeet model that isn't downloaded needs to be chosen again in **Companion › Listening**, which downloads it.
- When lifelike lip-sync isn't running on this PC, set it up in **Companion › Lip-sync**. Until then, the mouth follows the voice's loudness.

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
- **The Thinking pool file can't be read or saved**: open **Companion › Background thinking** to set it up again, and check that Martlet can write to its data folder.

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
