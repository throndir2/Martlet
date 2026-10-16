# Multiple Computers

Martlet is one app on all your computers. Talk to her on one PC, and let your strongest computers do the heavy work.

![Devices map](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/devices.png)

## Companion PCs and hosts

- A **companion PC** is where you talk to Martlet. It has the microphone, the speakers, the screen and the character.
- A **host** lends its graphics card and processor. It does jobs such as thinking, listening or speaking for your companion PCs.
- One PC can be both: a companion PC can also run its own host service.

To change what a PC is for, open **Settings › What this PC is for** and choose **Use as my companion PC** or **Use as a Martlet host**. The **Your other computers** list on the same card switches your other computers too. On the Devices map, select another computer and choose **Make it a host PC** or **Make it a companion PC**.

To add a host, see [Setting Up a Host](Setting-Up-a-Host).

## Join your Martlet network

Your computers with Martlet form your **Martlet network**. Pair a host once, and every computer in the network can use it.

1. Install Martlet on the new PC.
2. In the welcome tour, choose **Yes, join my Martlet network**. Later, use **Connect to your other computers** on Home, or **Devices › Add a computer › Martlet on your network**.
3. Pick one of your other computers in the list.
4. Make sure that both screens show the same check number.
5. Choose **Allow** on the other computer. **Turn down** refuses the request.

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

## The Devices map

Open **Devices** to see all your computers: this PC in the middle, your other computers, your hosts and the cloud services your jobs use. Choose **Map** or **List**. The list shows by itself when the map is too full, and it has a **Find** box.

Select a computer to see what it does, whether it answers and its Martlet version. Its actions let you give it a job, install, change or remove a role, update it and show its status.

- **What your computers can run** shows how much graphics memory, memory, processor and disk each part uses, from Martlet's estimates.
- **Cloud providers and servers** lists every online service and server that Martlet uses, and what it uses each one for: Thinking, the Thinking pool, Picture making, Singing, Vision, tools and more.
- **Sharing work** sums up which machines do each job.

## Each job has a list of machines

Each job (voice, listening, lip-sync, reading, seeing, hearing, pictures and singing) has a list of machines, in order, on its Companion page. Add this PC, one of your computers, a graphics card or a cloud provider, and turn each one on or off.

- The first machine does the job. When it is busy or offline, the next one takes over.
- **Companion › Thinking** lists the computers that may answer when your Thinking model is busy. The list is empty by default.
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

## Update every host from your companion PC

- The Devices map shows **Update available** on a host with an older Martlet. Select the host and choose **Update host**.
- **Settings › App updates › Update hosts** updates all your hosts at once.
- **Update paired hosts automatically**, on the same card, keeps your hosts on this PC's version.

A PC that runs Martlet as a host updates its own host service after Martlet updates itself.

## Share with friends

The **Share** page shares your hosts with friends, and your language models with other people's apps. See [Sharing with Friends](Sharing-with-Friends).

More detail: [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md), [One Martlet on all your computers](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md), [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md), [Resource footprints](https://github.com/throndir2/Martlet/blob/main/docs/RESOURCE_FOOTPRINTS.md), [Diagnostics](https://github.com/throndir2/Martlet/blob/main/docs/DIAGNOSTICS.md), [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md).
