# Accounts and Household

Everyone at home gets their own Martlet: their own characters, memories and settings, on every computer. The household shares the computers, the paid API keys and the voices Martlet knows.

## Signing in

- Martlet signs you in with your Windows account. You need no password until you add one.
- Your name shows at the bottom left of Martlet's window. Click it to see the account menu.
- Each Windows sign-in is a separate person until that person links it to their account.
- When you add a computer to your Martlet network and allow it, it signs in to the household owner's account and uses the owner's settings.
- What a PC is for (companion PC or host) is the same for every Windows user of that PC.

### Sign-in from outside

A host can let your computers sign in to it while they are away from home, instead of typing a pairing code shown on the host. To set it up, open **Devices › Add a computer**, go to **Roles**, choose the host and choose **Sign-in from outside**.

- A computer that signs in as yours pairs like any other computer and joins your Martlet network by itself.
- A friend's computer uses only this host's engines. It never joins your network.
- Remove a sign-in to take access away.

### The owner account

1. Type an **Account name** and a password of 12 characters or more.
2. Choose **1. Make an authenticator secret**.
3. Add the secret to your authenticator app, for example Google Authenticator, Aegis, 1Password or Bitwarden. Open the link, or type the secret.
4. Type the 6-digit code that your app shows, and choose **3. Save owner account**.
5. Write down the recovery codes and keep them safe. Martlet shows them only once. Each code works one time instead of an authenticator code.

Every sign-in with the owner account needs a code from your authenticator app. If you set up the owner account again, the new one replaces the old one. **New recovery codes** makes new codes, and the old codes stop working. **Remove owner account** takes access away from the computers that signed in with it.

### Allowed sign-ins

An allowed sign-in is an identity at a sign-in provider that is set up on the host. Martlet shows the provider's ID and the account's subject, a stable ID that the provider gives the account. Each identity is one of two kinds:

- **One of my computers**: its computers join your Martlet network and can use all your hosts, settings and memories.
- **A friend**: its computers use only this host's engines (thinking, listening, speaking, lip-sync and reading). They never join your network, never see your settings or memories, and always go after your own work.

To allow someone:

1. Ask them to sign in once with the invite, from the computer that you want to allow.
2. Under **Signed in but not allowed yet**, choose **Allow the newest as one of my computers** or **Allow the newest as a friend**. Or type the provider ID, the subject and a label, choose the kind and choose **Allow**.
3. Ask them to sign in again. Their computer then gets access.

**Make a friend** and **Make one of my computers** change what an identity gets. The host revokes the computers that it signed in with the old access, and they must sign in again. When one of your computers becomes a friend, it loses every host at once: your computers remove it from your Martlet network on their next sync.

**Remove** revokes the computers that the identity signed in. Your own computers among them leave your Martlet network on your computers' next sync, so they lose every host. A friend's computer never joined your network, so it loses only this host. **Being removed from your network** lists the computers that wait for that sync.

### Sign-in providers

People can sign in in the browser with your own identity provider (Authentik, Authelia, Keycloak, Pocket ID or another OpenID Connect provider), Google, Microsoft, Discord or Steam.

1. At the provider, register a client for Martlet:
   - **Google**: Google Cloud console › APIs & Services › Credentials › Create credentials › OAuth client ID › Desktop app.
   - **Microsoft**: Microsoft Entra admin center › App registrations. Add a Mobile and desktop application with the redirect `http://127.0.0.1`.
   - **Discord**: Developer Portal › your application › OAuth2, with the redirect `http://127.0.0.1:<port>/`. Type the same port in **Loopback port**.
   - **Your own provider**: a client with the redirect `http://127.0.0.1` on any port.
   - **Steam** needs no client.
2. In **Sign-in providers for your household**, choose the kind of provider. Type a short ID, the name that people see, the issuer URL (for your own provider), the client ID and the client secret.
3. Choose **Save provider**. Martlet saves the provider on every host of your Martlet network, so you set it up only once.
4. Sign in once from the computer that you want to allow. Then allow it under **Allowed sign-ins**.

The hosts keep the client secret. This PC keeps a copy only to add the provider to a host that joins later. **Add to the other hosts** adds each provider to the hosts that don't have it yet.

**Remove provider** removes it from every host of your household. Computers and friends that sign in with it lose access to those hosts, and your computers that joined with it leave your Martlet network. If nobody can sign in to a host after that, the host's outside access pauses until you set up the owner account or allow an identity again. The host keeps its outside addresses.

### Make an invite

The invite tells a computer away from home where the host answers and which key to expect. It holds no secret, because signing in is what lets a computer in. So you can keep the invite, use it again and send it to a friend that you allowed.

1. Set the host's outside addresses in **Share › Your Martlet network › Outside addresses**. The invite starts with them, and the computer keeps reaching the host after it joins.
2. In **Invite**, add more outside addresses if you need them, each with its port, for example `home.example.net:9443`. Put a space between two addresses.
3. Choose **Make invite**, then **Copy invite**.

An invite without an outside address has only the home address, so it works only where that address is reachable.

### Join from outside home

1. On the computer away from home, open **Devices › Add a computer › Join with an invite**.
2. Paste the invite, or a share link that a friend sent you, and choose **Connect**. Martlet checks the host's key before it sends anything.
3. Sign in with a provider (it opens your browser) or with the owner account: its name, its password and an authenticator code or a recovery code.

What happens next:

- **Your own host**: this PC pairs with the host at once. It joins your Martlet network by itself when one of your computers at home syncs. You don't need a check number.
- **Your household account**: if you signed in to it, this PC joins as one of your computers, and Martlet signs that account in on this PC after the PC has joined. If Martlet then says that the sign-in can't be used any more, sign in from the account button at the bottom left.
- **A friend's host**: this PC uses only its engines (thinking, listening, speaking, lip-sync and reading), only for this PC. It doesn't join your network, the host never sees your settings or memories, and the friend's own work goes first. Choose the host for a job under **Devices › Hosts shared with this PC**.

## Switch person or add a person

Click your name at the bottom left:

- Choose another person to switch to them. Switching ends the conversation, stops listening and watching, and loads that person's settings. You cannot switch while Martlet replies or saves a change. Wait a moment, then try again.
- **Add a person…** makes an account for someone else who uses this Windows sign-in. Type a name and choose **Add**. The new account needs no password, and Martlet switches to it. That person can add a PIN or a password later on the Account page.
- **Sign in as someone else…** signs in to another account with its Martlet password or a linked sign-in, for example on another person's PC in your home.
- **Account…** opens the Account page (below).
- **Lock** locks your account when it has a PIN, a password or Windows Hello.

New accounts stay on this PC until this PC is in your Martlet network. Then they go to your other computers. **Devices** shows how many people are on this PC and on your hosts.

If your account was removed on another computer, choose another account. If it was merged into another account, sign in as that account.

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

**Companion › Profiles › Household characters** lists the characters that other people in your household share. It needs an account for each person, and it shows once Martlet knows who uses it.

- **Use a copy** makes the character your own, with its own memories.
- A character shared together remembers everyone it talks to. Only its owner can change it.
- When its owner makes a character private again, the copies that people made stay theirs. The character keeps the memories that it made while it was shared.
- **Leave** removes the character from your characters on all your computers. Its memories stay with it for the others. You can talk to it again from **Household characters**.

## The Account page

Click your name and choose **Account…**.

- **Martlet password**: type a **User name** and a password of 12 or more characters. Tick **Add an authenticator app** for a code from your phone when you sign in on a new computer. Choose **Save password**. Keep the recovery codes that Martlet shows once. **Remove the authenticator** removes it on every host.
- **This Windows login**: **Link this Windows login** adds this Windows login to your account. **Unlink** removes it.
- **Sign-ins from your household's providers**: choose **Link** to add Google, Microsoft, Authentik, Discord, Steam or another provider. Then you can sign in with it on any of your computers. An owner or admin sets up the providers once in a host's **Sign-in from outside**, and Martlet puts them on every host.
- **Linked services**: **Link VRoid Hub...** shows your VRoid Hub characters in **Companion › Character › Find a character**.
- **On this PC**: **Ask for my password on this PC**, a PIN of 4 to 12 digits (**Set PIN**, **Remove PIN**), **Unlock with Windows Hello**, **Encrypt my files on this PC while locked**, **Remember me on this PC**, **Lock now** and **Sign out of this PC**.
- **Merge another account into this one**: for one person with two accounts. Its characters, memories, conversations and voices move into this account.

A new computer can join as yours. Make an invite in a host's **Sign-in from outside › Make invite**. On the new computer, open **Devices › Add a computer › Join with an invite**, paste the invite and sign in to your household account.

### Sign in with your Martlet password

Your Martlet password is kept on your hosts, not on one PC. To add one, pair a host of your Martlet network under **Devices** first.

- When you save a password, the page shows on how many hosts it is set. Add a password to sign in on other PCs and to unlock on this PC.
- Martlet shows the recovery codes only once. Each code works once, for example when you lose your phone. Keep them somewhere safe.
- When you sign in, one of your hosts checks your user name, password and authenticator code. This PC never keeps your password. It keeps only a check of it, so it can unlock you here later, also without a network.
- If a host's answer doesn't check out, choose another host. If no host answers, make sure that a host is on and try again.
- To unlink this Windows login, add a Martlet password first. After that, this PC asks for your PIN or password.
- At the first start, the e-mail of your Windows login can match an account in your household. Martlet then asks if you want to continue as that person. You must sign in with that person's Martlet password: the e-mail alone never signs anyone in.

When nothing is set up to unlock an account on this PC, or Martlet can't read how it unlocks, sign in with the Martlet password through one of your hosts.

### Linked sign-ins and VRoid Hub

Sign-ins from your household's providers are kept on your hosts. Pair a host of your Martlet network under **Devices** first.

1. Choose **Link** next to the provider.
2. If your account has a Martlet password or a linked sign-in, prove that it's you with one of them first.
3. Sign in with the provider in your browser, then come back to Martlet.

After that, you can sign in to your account with the provider on any of your computers. A new computer joins your network as yours when you sign in with it there. When you **Unlink** a sign-in, computers that joined your network with it leave the network at the next sync. Your account always keeps at least one way to sign in.

**Link VRoid Hub...** links your pixiv account. Martlet then shows your own VRoid Hub characters and the characters that their creators let anyone use. Sign in on VRoid Hub's page in your browser and allow Martlet within 5 minutes. The link belongs to your account on this PC only. After you unlink it, VRoid Hub characters don't show on this PC until you link it again.

### Merge two accounts

Use this when one person has two accounts.

1. Sign in as the account that you want to keep.
2. Open **Account…** and choose the other account under **Merge another account into this one**.
3. Choose **Merge into this account**.
4. Sign in as the other account. If the account that you keep has a Martlet password, sign in as it too.

The other account's characters, memories, conversations and voices move into your account. Its logins sign in to your account, and the other account goes away. You can't merge the household owner's account into another account: sign in as the owner and merge the other account into it. If the merge stops, the other account stays; try again.

Martlet merges accounts with the same name by itself. Each PC makes an account with the name of its Windows login, so when your Windows logins on two PCs have the same name, Martlet knows that they are the same person. Between replies, the PC moves its own account into the household's account of that name, and you see only one account in the picker.

## Privacy notes

- Accounts are kept apart, but not secret from a person with administrator rights on the PC or a host.
- Windows Hello cannot tell apart people who share one Windows login. Use it only if nobody else set up Windows Hello on your Windows login. Use a PIN or a password on a shared login.
- A PIN or a password alone does not keep out a person who uses the same Windows login: that person can remove the lock. Turn on **Encrypt my files on this PC while locked** to keep them out.
- After five wrong PINs or passwords, each new try waits longer, up to 15 minutes.
- **Encrypt my files on this PC while locked** needs a PIN. It encrypts your account's folder while another person uses Martlet or Martlet is closed. If you forget the PIN, this PC's copy can be lost, but your hosts keep your synced things.
- To turn on encryption, type your PIN in the **PIN** box, then tick **Encrypt my files on this PC while locked**. Your PIN opens the files. The page tells you when your Martlet password opens them too. Windows Hello can't open them.
- Another program can keep some files open. Then Martlet can't encrypt them and tries again when it closes. If some files can't be decrypted (they are open in another program or damaged), you can open the account without them.
- Voice ID keeps your voiceprint as numbers only, never as a recording.

More detail: [Accounts and households](https://github.com/throndir2/Martlet/blob/main/docs/ACCOUNTS.md), [Household sign-in providers](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md#household-sign-in-providers), [One Martlet on all your computers](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md#each-persons-settings).
