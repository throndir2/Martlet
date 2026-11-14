# Multiple Computers

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Users
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Desktop/MainWindow.xaml`, `src/Martlet.Desktop/MainWindow.Network.cs`, `src/Martlet.Desktop/MainWindow.SettingsSync.cs`, `src/Martlet.Core/Cluster`

Martlet can use your computers together. A companion PC is where you talk to Martlet. A host runs jobs for your companion PCs. A companion PC can also run a host service.

## Companion PCs and hosts

On a Windows PC, open **Settings › What this PC is for**. Choose **Use as my companion PC** or **Use as a Martlet host**. For host setup, see [Setting Up a Host](Setting-Up-a-Host).

### Switch another computer from here

Turn on **Devices › Settings for all devices › Keep Martlet the same on all my computers**. Select the computer on the Devices map, or use **Your other computers** in Settings. Choose the action to make it a host PC or a companion PC, then confirm. The other computer follows the request while Martlet runs there, or when it next starts.

## Join your Martlet network

1. Install Martlet on the new PC.
2. In the welcome tour, choose **Yes, join my Martlet network**. You can also open **Devices › Add a computer**.
3. Under **Martlet on your network**, choose a computer and connect.
4. Check that both computers show the same check number. Choose **Allow** on the other computer only if you made the request and the numbers match.

Your computers can pair with hosts in your network. If discovery does not find a computer, use **Enter a pairing code**. For a computer away from home, see [Use your hosts from outside your home](Sharing-with-Friends#use-your-hosts-from-outside-your-home).

### When your other computers can't find this PC

Open Martlet on the computer with the host. Check that **Let my other computers find this PC and ask to use its hosts** is on. A firewall, a guest network or a VPN can prevent discovery. You can pair with a code instead.

### Allow or remove a computer

**Share › Your Martlet network** shows your computers and join requests. Check the number before you allow a request. Use **Remove from network** to remove a computer or host.

### Outside addresses

Outside addresses tell your computers how to reach a host away from home. See [Outside addresses](Sharing-with-Friends#outside-addresses). A host key is checked even when an outside address is used.

### Let your other computers manage this PC

In **Settings › Your other computers**, you can allow your paired computers to update Martlet here and manage this PC's host service. Commands sent through Martlet wait when Martlet on the target computer is not running. See [Commands between computers](Setting-Up-a-Host#commands-between-computers).

### Keep it the same and fail over

The shared-settings switch is on by default. It shares choices through your paired hosts. The people Martlet recognizes by voice are shared even when the switch is off. A job can also use its failover choice to move to another host when its host stops answering.

## Recommended setup

**Recommended setup** on Home proposes a setup for your computers. Review its choices before you choose **Reconfigure**. A locked job keeps its model unless you choose a suggestion or unlock it. See [Recommended setups](https://github.com/throndir2/Martlet/blob/main/docs/RECOMMENDED_SETUPS.md).

## The Devices map

Open **Devices** to see your computers, hosts and the services they use. Select a computer for its details and available actions. The map can show status, roles and updates. You can also use the list view.

### Machine cards

Select a computer on the map to see its details and actions. Its connection method controls how Martlet manages it. The available actions depend on the computer.

## Each job has a list of machines

A job's page can list the machines that do the job. The list is ordered. When the first machine is busy, the next can take the job. The lists are shared across your computers.

### The order of the list

Use **Up** and **Down** on the job's list to change the order. The job uses the machines that are enabled in the list.

### Hand a job to another computer

Choose the computer on the job's page. Martlet shows the available action and asks before a change that needs confirmation.

### When no machine can do a job

Check the job's page and the Devices map for its status. If no machine can do Thinking, Martlet cannot reply. If Listening is unavailable, you can still type.

## Keep Martlet the same on all my computers

**Devices › Settings for all devices › Keep Martlet the same on all my computers** shares settings and other data through your paired hosts. These include job choices, memories and characters. Each computer keeps its own device choices, such as its microphone and speakers.

A computer that cannot use a shared choice yet keeps its own choice and reports why. **Use this PC's settings on all my computers** replaces shared settings with this PC's settings. Check the choices before you use it.

## Update every host from your companion PC

The Devices map can show an update for a host. Select the host and choose **Update host**. **Settings › App updates › Update hosts** updates paired hosts.

## Share with friends

A friend's shared host is separate from your Martlet network. See [Sharing with Friends](Sharing-with-Friends).

For technical details, see [One Martlet on all your computers](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md) and [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md).
