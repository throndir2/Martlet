# Sharing with Friends

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Users
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Desktop/MainWindow.ShareLinks.cs`, `src/Martlet.Desktop/MainWindow.ShareLinks.ChangeShareLinkHostAsync.cs`, `src/Martlet.Desktop/MainWindow.FrontDoor.cs`, `src/Martlet.Desktop/OpenAiDialogs.cs`, `src/Martlet.Gateway/GatewaySecurity.cs`

The **Share** page lets you share hosts with friends and give other apps access to your hosts. Share links and keys give access to anyone who has them. Send them privately.

## Share your hosts with a friend

1. Open **Share › Friends** and choose **Make a share link**.
2. Enter your friend's name. Choose all your hosts, including future hosts, or select specific hosts.
3. Choose **Make link**. Copy the link and send it to your friend privately.
4. Your friend opens the link or pastes it under **Share › Hosts shared with this PC › Accept a share link**.

The link gives access to the selected hosts' engines, not your settings, memories or API keys. Your friend does not join your Martlet network. Your own work has priority.

**Copy link** works on the computer that made the link. If a host has no outside address, your friend can reach it only on your home network. Set up outside access and copy the link again if necessary.

Choose **Stop link** to stop sharing the link. A host that does not answer receives the change when it next answers. For a link with selected hosts, you can also change the hosts it shares.

## Use a friend's hosts

Open **Share › Hosts shared with this PC** to see a friend's hosts. Choose a host for an available job there. The choice applies to this PC, not your other computers. Your friend's own work has priority. **Forget** removes the host from this PC.

## Use your hosts from outside your home

A computer away from home needs a way to reach the host and permission to use it. Set up sign-in on the host first. An owner account with an authenticator or a sign-in provider can provide sign-in. Use **Sign-in from outside** to make an invite. The invite does not contain a secret.

Give the host an outside address, such as an address on a private network. On the other computer, open **Devices › Add a computer › Join with an invite**, paste the invite and sign in. A friend's invite can instead give access to a shared host.

### Outside addresses

Open **Outside addresses** on a host under **Share › Your Martlet network**, or use **Outside access** on the Devices map. Enter up to four addresses, separated by commas. An address includes its port. An empty list removes the outside addresses.

Your computers try the home address first and check the same host key for an outside address. A proxy that ends the encrypted connection cannot be used for this paired connection. Outside access is paused if the host has no usable sign-in. See the [network guide](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md) for router and container settings.

## Share your language models

Apps that use the OpenAI-compatible API can use the language models your hosts offer.

1. Open **Share › Share your language models** and choose **Give someone access**.
2. Enter a name, choose the request priority and expiry, then choose **Make key**.
3. Copy the details and send them privately. Martlet cannot show the key again.

**Your Martlet first** puts your work ahead of their requests. **Same priority as your background work** lets their requests run beside background work; live conversations still come first. Revoke a lost key and make another. The key does not give access to your settings, memories, voices, other engines or cloud model keys.

## One address for all your hosts

A public HTTPS address can send requests to a host's **front door**. The front door serves the OpenAI-compatible API and Martlet's web page. It can forward requests for a model to another host.

### Open the front door

1. Open **Share › Share your language models**.
2. Under **Front door**, choose a host and a port. Use a port from 1024 to 65535, other than that host's gateway port.
3. Choose **Open front door** and confirm. The gateway restarts.

Choose **Show setup** for proxy and tunnel instructions. Point the public address at the front door, not the gateway port. Save the address under **Address to give people**, then choose **Check address**. See the [OpenAI-compatible endpoint guide](https://github.com/throndir2/Martlet/blob/main/docs/OPENAI_ENDPOINT.md) for details.

### Get an https address

Choose **Show setup** for instructions for Tailscale Funnel, a reverse proxy or Cloudflare Tunnel. The public address needs a trusted certificate. Save the address and choose **Check address**. The [endpoint guide](https://github.com/throndir2/Martlet/blob/main/docs/OPENAI_ENDPOINT.md) describes the options.

## Apps and API keys

Use **Share › Apps and API keys › Create API key** to give an app its own access. Choose only the permissions it needs. Copy the key when it is shown. You cannot see it again. Use **Revoke** to remove access; hosts that are offline receive the change when they answer.

For details, see [API keys](https://github.com/throndir2/Martlet/blob/main/docs/API.md) and [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md).
