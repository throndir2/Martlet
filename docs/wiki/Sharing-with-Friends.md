# Sharing with Friends

The **Share** page has everything about sharing in one place: your Martlet network, friends, API keys, your language models and the hosts friends share with you. Your own work always comes first, and you can stop any sharing at once.

## The Share page

| Card | What it does |
| --- | --- |
| **Your Martlet network** | Your own computers. **Allow** or **Turn down** a computer that asks to join, and set a host's **Outside addresses**. |
| **Friends** | Share your hosts with a friend's Martlet. It shows once you have a host. |
| **Share your language models** | Let other people's apps use the models your hosts run. |
| **Apps and API keys** | Keys for Home Assistant, scripts and other apps. Each key works on every host in your network. |
| **Hosts shared with this PC** | The hosts that friends share with you. |

## Share your hosts with a friend

1. Open **Share › Friends** and choose **Make a share link**.
2. Type your friend's name.
3. Keep **All my hosts, also the ones I add later**, or clear it and choose the hosts.
4. Choose **Make link**, copy the link and send it to your friend in a private message.
5. Your friend clicks the link, or pastes it in **Share › Hosts shared with this PC › Accept a share link**.

Your friend's Martlet can then use your hosts' engines: thinking, listening, speaking, lip-sync and reading. Picture making and singing are not shared.

- Your friend never joins your Martlet network. They never get your settings, memories, voices, logs, API keys or other computers. Their Martlet uses its own keys for online services.
- Your own requests come first. When you need the same graphics card, your request stops theirs.
- **Share › Friends** lists each friend, the hosts you share with them and their computers. Use **Copy link**, **Share** *host* or **Stop sharing** *host* on their row. **Stop link** ends the link on every host at once.
- A friend can also sign in to a host with their own Google, Microsoft, Discord or Steam account. Set up that provider on the host and send them the host's invite ([below](#use-your-hosts-from-outside-your-home)). After they sign in once, they show in **Friends**: choose **Share** *host*.

Anyone who has the link can use what it shares. Send it only to your friend.

## Use a friend's hosts

**Share › Hosts shared with this PC** lists each host a friend shares with you. It shows the engines and the language models that the host offers.

- Choose **Use for thinking**, **Use for listening**, **Use for speaking**, **Use for lip-sync** or **Use for reading** to give a job to it. This choice is for this PC only. This PC uses only the host's engines.
- **Forget** gives the jobs back and removes the host. A shared host that you forget on one PC stays in your Martlet network for your other computers, and this PC doesn't pair with it again by itself.
- Your friend's own work comes first. When they need their host, your Martlet uses your own computers or tries again later.
- Your friend's host sees the requests that this PC sends there, but never your settings or memories.

## Use your hosts from outside your home

Your laptop can be a companion PC away from home while your hosts at home do the work. A friend outside your home needs the same setup.

1. Set up sign-in on the host. Open **Devices › Add a computer**, go to **Roles**, choose the host and choose **Sign-in from outside**. Make an owner account with a password and an authenticator app, and keep the recovery codes. Or set up a sign-in provider (Google, Microsoft, Discord, Steam or your own OpenID Connect provider). Martlet saves a provider on every host.
2. Give the host an outside address:
   - The best way is a private network such as Tailscale, ZeroTier or WireGuard. Add the host's address on it (for example `100.101.102.103:9443`) as an outside address. Nothing opens to the internet.
   - For this PC's host service, select this PC on the Devices map and choose **Outside access for this PC's host service**. Tick **Let Martlet open a port on my router for it (UPnP)** and choose **Save and restart gateway**. If your internet provider shares your address with other homes, use Tailscale.
   - For another host, choose **Outside access** on the map or **Outside addresses** in **Share › Your Martlet network**. Forward the port on your router yourself.
3. In **Sign-in from outside**, choose **Make invite** and copy it. The invite holds no secret.
4. On the laptop, open **Devices › Add a computer › Join with an invite**. Paste the invite, choose **Connect** and sign in. The laptop then joins your Martlet network by itself.

A host gets outside addresses only after you set up its sign-in. Every request from outside still needs a pairing, a sign-in or a share link. After a few wrong tries, the host makes each new try wait longer.

## Share your language models

People who don't use Martlet can use the models your hosts run with Ollama (the Thinking and Thinking pool roles). They use any app that works with the OpenAI API, such as Open WebUI, SillyTavern or the OpenAI Python library.

1. Open **Share › Share your language models** and choose **Give someone access**.
2. Type who the key is for.
3. Under **Their requests**, choose **Your Martlet first (recommended)** or **Same priority as your background work**.
4. Choose when the key **Expires**: never, in 30 days, in 90 days or in a year.
5. Choose **Make key**. Copy the text that Martlet shows and send it in a private message. It has the address, the key, the models and a first request.

Martlet does not keep the key and cannot show it again. You can change a key's priority on its row at any time. **Revoke** stops the key on every host, also its running requests.

- With **Your Martlet first**, any request of yours stops theirs. With the other choice, they run beside your background work, but your conversations still stop them.
- Their requests use the same context size as yours, so your host never loads its model again for them.
- Your cloud models and API keys are never shared. A key also never reaches your settings, memories, voices, characters or logs.

## One address for all your hosts

Apps outside your home need an `https` address with a real certificate. Put a reverse proxy (Nginx or Caddy) in front of one host that is always on. That host runs a request when it has the model. Otherwise it sends the request to the host that has it.

1. Choose a name, for example `llm.example.com`, and point it at your home's internet address in your DNS.
2. Make your router send TCP port 443 to the proxy computer.
3. Choose **Show proxy setup**. Copy the Nginx or Caddy setup to the proxy. Caddy gets the certificate by itself; for Nginx, use certbot.
4. Type the address in **Address to give people**, choose the host in **sends to** and choose **Save**. Your other computers get the same address.

The proxy sends only the OpenAI API. Pairing and sign-in stay at home. At home, an app that can skip certificate checks can also use `https://<host>:9443/v1`.

## Apps and API keys

Choose **Create API key** to let Home Assistant, a script or another app use your hosts. Name the key after the app, and choose what it may do and when it expires. Martlet shows the key once. **Revoke** stops it everywhere.

## Privacy and safety

- Send share links, invites and keys only in private messages.
- Stop sharing at any time: **Stop link**, **Stop sharing**, **Revoke** or **Remove from network**.
- Prefer a private network such as Tailscale to an open router port.
- Martlet's own computers check each host's key. A proxy that ends the encrypted connection works only for the OpenAI API, not for your own computers.

More detail: [OpenAI-compatible endpoint](https://github.com/throndir2/Martlet/blob/main/docs/OPENAI_ENDPOINT.md), [Network: sharing with a link and with friends](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md#sharing-with-a-link), [Reaching your network from outside home](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md#reaching-your-network-from-outside-home), [API keys](https://github.com/throndir2/Martlet/blob/main/docs/API.md).
