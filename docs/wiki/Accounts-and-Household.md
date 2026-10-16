# Accounts and Household

Everyone at home gets their own Martlet: their own characters, memories and settings, on every computer. The household shares the computers, the paid API keys and the voices Martlet knows.

## Signing in

- Martlet signs you in with your Windows account. You need no password until you add one.
- Your name shows at the bottom left of Martlet's window. Click it to see the account menu.
- Each Windows sign-in is a separate person until that person links it to their account.
- When you add a computer to your Martlet network and allow it, it signs in to the household owner's account and uses the owner's settings.
- What a PC is for (companion PC or host) is the same for every Windows user of that PC.

## Switch person or add a person

Click your name at the bottom left:

- Choose another person to switch to them. Switching ends the conversation, stops listening and watching, and loads that person's settings. You cannot switch while Martlet replies.
- **Add a person…** makes an account for someone else who uses this Windows sign-in. Type a name and choose **Add**. The new account needs no password, and Martlet switches to it.
- **Sign in as someone else…** signs in to another account with its Martlet password or a linked sign-in, for example on another person's PC in your home.
- **Account…** opens the Account page (below).
- **Lock** locks your account when it has a PIN, a password or Windows Hello.

## What each person keeps

Each person has their own:

- Personalities and character profiles, prompts, replies and lorebooks.
- The character shown, how you talk, speech bubbles and the theme.
- Voice ID, reminders and touch temperament.
- Memories, conversation history and the memory brief.
- Creations: pictures, songs and research reports.

A person's things go only to the computers where that person is signed in.

## What the household shares

The household shares:

- The computers and hosts, and who does each job.
- How Martlet thinks, listens and speaks, with the paid API keys and the Thinking pool.
- The people Martlet recognizes by voice, the speaking voices, the character models and their emotes and motions.
- Home Assistant, updates and the sign-in providers.

These travel to all your computers. See [Keep Martlet the same on all my computers](Multiple-Computers#keep-martlet-the-same-on-all-my-computers).

## Link your voice

Open **Companion › People** and tick **This voice is** *your name* on your voice. Martlet then knows when you are talking. Anyone else who speaks gets an answer from your character, and Martlet remembers them by their own voice.

A voice never signs anyone in, unlocks an account or spends money.

## Share characters and memories

- Share a copy of a character, or share it together so everyone talks to the same character. See [Sharing a character with your household](Personality-and-Characters#sharing-a-character-with-your-household).
- Household memories are known to every character. Copy or move facts to the household or to another person. See [Sharing memories with your household](Memory#sharing-memories-with-your-household).

## The Account page

Click your name and choose **Account…**.

- **Martlet password**: type a **User name** and a password of 12 or more characters. Tick **Add an authenticator app** for a code from your phone when you sign in on a new computer. Choose **Save password**. Keep the recovery codes that Martlet shows once. **Remove the authenticator** removes it on every host.
- **This Windows login**: **Link this Windows login** adds this Windows login to your account. **Unlink** removes it.
- **Sign-ins from your household's providers**: choose **Link** to add Google, Microsoft, Authentik, Discord, Steam or another provider. Then you can sign in with it on any of your computers. An owner or admin sets up the providers once in a host's **Sign-in from outside**, and Martlet puts them on every host.
- **Linked services**: **Link VRoid Hub...** shows your VRoid Hub characters in **Companion › Character › Find a character**.
- **On this PC**: **Ask for my password on this PC**, a PIN of 4 to 12 digits (**Set PIN**, **Remove PIN**), **Unlock with Windows Hello**, **Encrypt my files on this PC while locked**, **Remember me on this PC**, **Lock now** and **Sign out of this PC**.
- **Merge another account into this one**: for one person with two accounts. Its characters, memories, conversations and voices move into this account.

A new computer can join as yours. Make an invite in a host's **Sign-in from outside › Make invite**. On the new computer, open **Devices › Add a computer › Join with an invite**, paste the invite and sign in to your household account.

## Privacy notes

- Accounts are kept apart, but not secret from a person with administrator rights on the PC or a host.
- Windows Hello cannot tell apart people who share one Windows login. Use a PIN or a password on a shared login.
- After five wrong PINs or passwords, each new try waits longer, up to 15 minutes.
- **Encrypt my files on this PC while locked** needs a PIN. It encrypts your account's folder while another person uses Martlet or Martlet is closed. If you forget the PIN, this PC's copy can be lost, but your hosts keep your synced things.
- Voice ID keeps your voiceprint as numbers only, never as a recording.

More detail: [Accounts and households](https://github.com/throndir2/Martlet/blob/main/docs/ACCOUNTS.md), [Household sign-in providers](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md#household-sign-in-providers), [One Martlet on all your computers](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md#each-persons-settings).
