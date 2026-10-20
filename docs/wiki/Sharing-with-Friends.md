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

Your friend's Martlet can then use your hosts' engines: thinking, listening, speaking, lip-sync and reading. Picture making and singing are not shared. A link for all your hosts also covers the hosts you add later, and your friend's Martlet finds them by itself.

If a host has no outside address, your friend can reach it only on your home network. To share it over the internet, select the host on the Devices map and choose **Outside access** ([below](#use-your-hosts-from-outside-your-home)). Then copy the link again.

- Your friend never joins your Martlet network. They never get your settings, memories, voices, logs, API keys or other computers. Their Martlet uses its own keys for online services.
- Your own requests come first. When you need the same graphics card, your request stops theirs.
- **Share › Friends** lists each friend, the hosts you share with them and their computers. Use **Copy link**, **Share** *host* or **Stop sharing** *host* on their row. When you share one more host with a link, your friend's Martlet finds it on its next check.
- **Copy link** works only on the computer that made the link. On another computer, copy it on the computer that made it, or stop the link and make a new one.
- **Stop sharing** *host* takes away their computers' access to that host at once. A request of theirs that is running there stops.
- **Stop link** ends the link on every host at once, also on hosts you add later. Their computers lose access, and their running requests stop. A host that doesn't answer now removes the link as soon as it answers.
- If a host doesn't have the link yet, Martlet adds it each time the Friends card checks your hosts. A host with an older Martlet needs an update first.
- You can have up to 32 share links at a time. Stop one to make a new one.
- A friend can also sign in to a host with their own Google, Microsoft, Discord or Steam account. Set up that provider on the host and send them the host's invite (**Sign-in from outside › Make invite**, [below](#use-your-hosts-from-outside-your-home)). After they sign in once, they show in **Friends**: choose **Share** *host*.

Anyone who has the link can use what it shares. Send it only to your friend. You can stop it at any time.

## Use a friend's hosts

**Share › Hosts shared with this PC** lists each host a friend shares with you. It shows the engines and the language models that the host offers.

To accept a share link, click it, or choose **Accept a share link** and paste it (it starts with `martlet://share/`). Martlet signs in to your friend's hosts as a friend. Hosts that your friend adds to the link later are added here by themselves.

- Choose **Use for thinking**, **Use for listening**, **Use for speaking**, **Use for lip-sync** or **Use for reading** to give a job to it. This choice is for this PC only. This PC uses only the host's engines.
- **Forget** gives the jobs back and removes the host. A shared host that you forget on one PC stays in your Martlet network for your other computers, and this PC doesn't pair with it again by itself. Your friend's host still lists this PC until your friend stops sharing it with you. If no device does a job after you forget the host, choose another one in **Companion** or **Devices**.
- If your friend stops sharing a host, its row says so. Choose **Forget**, or sign in again with a new invite from your friend.
- Your friend's own work comes first. When they need their host, your Martlet uses your own computers or tries again on the next request.
- Your friend's host sees the requests that this PC sends there, but never your settings, memories or API keys.

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

### Outside addresses

The **Outside access** dialog of a host lists the addresses that reach its gateway from outside your home.

- Type up to four addresses, separated by commas, as `name:port`, `IPv4:port` or `[IPv6]:port`. Leave the box empty to remove them.
- Desktops try the home address first. They use an outside address only when the home address doesn't answer. They always check the same host key, so a proxy that ends TLS (the encryption) doesn't work.
- **Save and restart gateway** restarts the host's gateway. Your other computers learn the addresses on their next sync.
- With outside addresses, the host also listens on its own Tailscale address and on the addresses that you name.
- To forward a port by hand, open your router's settings and forward a TCP port to the host's home address. Then add your public address with that port.
- **Let Martlet open a port on my router for it (UPnP)** asks your router to open a port. UPnP must be on in the router. Martlet first checks that your router has a real internet address. If your internet provider shares one address among many homes, use Tailscale instead. Windows asks once to let connections from outside your home through the firewall. Clear the box to close the port again.
- If the host doesn't take the new settings, Martlet closes the router port again. If Windows Firewall still lets only your home network in, open **Outside access** again and agree to the Windows prompt.

A host behind Docker can't tell a connection from home apart from one from outside. Keep **Treat every connection as coming from outside home** on when the port is reachable from outside. Typed pairing codes then need **Allow typed pairing codes from outside home**, also at home. Martlet pairing with its own host service doesn't need it.

Until sign-in works on the host, the dialog says why and offers **Set up sign-in first**. You can still remove addresses and change how connections are treated. While sign-in is off on a host that has outside addresses, outside access is paused.

## Share your language models

People who don't use Martlet can use the models your hosts run with Ollama (the Thinking and Thinking pool roles). They use any app that works with the OpenAI API, such as Open WebUI, SillyTavern or the OpenAI Python library.

1. Open **Share › Share your language models** and choose **Give someone access**.
2. Type who the key is for.
3. Under **Their requests**, choose **Your Martlet first (recommended)** or **Same priority as your background work**.
4. Choose when the key **Expires**: never, in 30 days, in 90 days or in a year.
5. Choose **Make key**. Copy the text that Martlet shows and send it in a private message. It has the address, the key, the models and a first request.

Martlet does not keep the key and cannot show it again. If the key is lost, revoke it and make a new one. You can change a key's priority on its row at any time. **Revoke** stops the key on every host, also its running requests.

If you haven't set an address to give people yet, the text has a home address. It works only inside your home, and only in apps that can skip certificate checks. To share outside your home, first give the front door an address (see [One address for all your hosts](#one-address-for-all-your-hosts)).

- With **Your Martlet first**, any request of yours stops theirs, and theirs wait while yours run. With **Same priority as your background work**, they run beside your Thinking pool's work, but your live conversations still stop them.
- Their requests use the same context size as yours, so your host never loads its model again for them.
- Your cloud models and API keys are never shared. A key also never reaches your settings, memories, voices, characters, other engines or logs.

## One address for all your hosts

Apps outside your home need one `https` address with a real certificate. That address goes to the **front door**: one of your hosts that is always on. The front door runs a request when it has the model. Otherwise it sends the request to the host that has it, so it needs no graphics card of its own.

### Open the front door

The front door is a second, public port on one host. It serves only Martlet's web page and the OpenAI API, and it answers "not found" for everything else. Pairing, sign-in, your network and your settings stay at home. Every request needs a key that you made, and your own work comes first.

1. Open **Share › Share your language models**.
2. Under **Front door**, choose a host that is always on.
3. Type a port from 1024 to 65535 that is not the gateway's own port (9443). 9445 is a good choice.
4. Choose **Open front door**. The host's gateway restarts for a few seconds.

Only one host has the front door. When you open it on another host, it closes on the first one. **Close front door** stops apps and the web page from reaching your models through your address. Keys stay, and you can open it again at any time.

The front door listens on its home address and on `127.0.0.1`. So a proxy on your home network, or Tailscale on the same computer, can reach it. If this PC can't run commands on your hosts, run `martlet-host exposure --front-door 9445` on the host.

### Get an https address

Choose **Show setup** next to **Address to give people**. It shows the exact text for your name and your front door. Then pick one way:

1. **Tailscale Funnel** (no domain, no router change):
   1. Install Tailscale on the front door's computer and sign in.
   2. Run the command from **Show setup** on that computer.
   3. Tailscale prints your address, such as `https://<computer>.<tailnet>.ts.net`.
2. **Nginx Proxy Manager, Nginx or Caddy** (your own domain):
   1. Point the name, for example `llm.example.com`, at your home's internet address in your DNS. You can also keep it behind Cloudflare's proxy.
   2. Make your router send TCP port 443 to the proxy's computer.
   3. Add the site from **Show setup** to the proxy. Caddy gets the certificate by itself. For Nginx, use certbot. For Nginx Proxy Manager, Martlet can do it for you (see below).
3. **Cloudflare Tunnel** (a domain on Cloudflare, no router change):
   1. Add the rule from **Show setup** to `cloudflared` on any computer at home.

Then type the address, for example `https://llm.example.com/v1`, in **Address to give people**. Choose **Save**, then **Check address**. Your other computers get the same address.

Send everything to the front door's port. Never point a proxy at the gateway's own port (9443). Behind Cloudflare's proxy, a reply that doesn't stream must finish within 100 seconds. Streamed replies have no such limit.

At home, an app that can skip certificate checks can also use the front door's home address, such as `https://<host>:9445/v1`.

### Set it up in Nginx Proxy Manager

Open the front door and save the address to give people first.

1. Choose **Show setup**, then **Set it up in Nginx Proxy Manager**.
2. Martlet looks for Nginx Proxy Manager on the front door's computer (port 81). When it finds it, it fills in the address.
3. Type the email and password of your Nginx Proxy Manager account. Martlet uses them once to sign in and doesn't save them.
4. Martlet tells you what it found and what it will do:
   - A site that already has the name is changed, not added again. Martlet changes only where it sends people and turns on the settings for streamed and long replies. The site keeps its certificate.
   - For a new site, Martlet uses a certificate that already covers the name. Otherwise it asks Let's Encrypt for one. Your name must then reach the proxy on port 80.
   - When the site already goes to the front door, Martlet changes nothing.
5. Choose **Change it** or **Add it**. Martlet then checks the address.

### Check the address

**Check address** asks your address for the list of models without a key. It sends no key and changes nothing.

- **Works**: the address reaches your front door. People with a key can use it now.
- **Reaches a Martlet gateway, but not its front door**: point your proxy at the front door's port, not at the gateway's own port.
- **Your proxy answers but can't reach Martlet**: check that the proxy sends to the front door's home address and port.
- **Redirects**: save the address that it ends at.
- **Answers, but not as Martlet**: check that the proxy sends everything to the front door.
- **No trusted certificate**: give your proxy a real certificate, for example from Let's Encrypt, or use Tailscale Funnel.
- **Nothing answered** or **didn't answer in 15 seconds**: check the DNS name, your router's port 443 and the proxy.

## Apps and API keys

Give each app or script its own key, so you can see what uses your hosts and revoke one key without the others. Your own computers never need a key.

1. Open **Share › Apps and API keys** and choose **Create API key**.
2. Name the key after the app, for example *Home Assistant*.
3. Under **It may**, tick only what the app needs: see status and logs; use thinking, listening, speaking and lip-sync; use screen understanding; update hosts and change their roles; or use your hosts' language models.
4. Choose when the key **Expires**: never, in 30 days, in 90 days or in a year.
5. Choose **Create key**. Copy the key now and keep it in the app's secret settings.

Martlet doesn't keep the key and can't show it again. If you lose it, revoke it and make a new one. The window also shows each host's address, its host key pin (it never changes) and an example request.

- A key works on every host in your Martlet network. It reaches each host within a minute. With no host paired yet, it reaches your hosts as soon as you pair one.
- A host with an older Martlet can't use keys. Choose **Update host** for it.
- **Revoke** stops the key on every host as soon as each host hears about it (at once for hosts that answer now). Apps that use it stop working. You can't undo it: make a new key if the app needs access again.

## Privacy and safety

- Send share links, invites and keys only in private messages. Anyone who has a share link or a key can use what it gives.
- Stop sharing at any time: **Stop link**, **Stop sharing**, **Revoke** or **Remove from network**.
- Prefer a private network such as Tailscale to an open router port.
- Martlet's own computers check each host's key. A proxy that ends the encrypted connection works only for the OpenAI API, not for your own computers.

More detail: [OpenAI-compatible endpoint](https://github.com/throndir2/Martlet/blob/main/docs/OPENAI_ENDPOINT.md), [Network: sharing with a link and with friends](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md#sharing-with-a-link), [Reaching your network from outside home](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md#reaching-your-network-from-outside-home), [API keys](https://github.com/throndir2/Martlet/blob/main/docs/API.md).
