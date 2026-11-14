# Setting Up a Host

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Users
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Desktop/HostsWindow.xaml`, `src/Martlet.Desktop/HostSetup.cs`, `src/Martlet.Core/Installation`, `src/Martlet.Gateway.Host.Linux`, `deploy/host/martlet-host`

A host runs jobs for your companion PCs. These can include thinking, listening, speaking and lip-sync. The jobs available depend on the host and its hardware.

## Ways to set up a host

- **This Windows PC:** open **Devices › Add a computer** and set up this PC. The host service uses Docker Desktop.
- **Another Windows PC:** install Martlet there and choose **Use as a Martlet host**. Set up its host service there.
- **Linux over SSH:** open **Devices › Add a computer**, enter the SSH target and choose **Set up over SSH**. The Linux computer needs an SSH server.
- **Mac:** use the native Mac host commands. See [Linux and Mac](Linux-and-Mac) and the [host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md).

### The Add a computer window

Under **Martlet on your network**, select a computer and connect. Check the number on both computers before you allow the request. If it is not listed, choose **Enter a pairing code**. After connecting, **Choose jobs for this host** shows available roles.

### Prepare a Linux computer

**Prepare a Linux computer** on the Devices map reads the computer's status over SSH. Select the changes you want before you run them. Some changes need administrator rights.

## Windows with Docker Desktop

Install Docker Desktop and WSL 2, then set up the host service on the Windows PC. Martlet shows setup progress. Windows can ask for approval to change firewall settings. Jobs that use an NVIDIA graphics card also need WSL GPU support.

## Linux over SSH

Enter the SSH target in **Devices › Add a computer** and choose **Set up over SSH**. Check the host key before you trust it. Martlet can use Docker or a native Linux installation. A native installation needs systemd. The [host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md) has the requirements and commands.

## Macs

The native Mac host uses installed Ollama and whisper.cpp. It runs as a background agent while you are logged in. See [Linux and Mac](Linux-and-Mac) for setup and current limitations.

## Pairing

To pair by code, open **Devices › Add a computer › Enter a pairing code**. Enter the host address and the code shown on the host. A code is for one use. The host's interactive pairing command waits for the code to be used or cancelled.

A paired computer can also join your Martlet network. See [Join your Martlet network](Multiple-Computers#join-your-martlet-network). Outside access has separate sign-in and pairing rules; see [Sharing with Friends](Sharing-with-Friends#use-your-hosts-from-outside-your-home).

## Choose the host's jobs

After pairing, **Choose jobs for this host** lists the available roles. Later, select the host on the Devices map to install, change or remove roles. Check the requirements and terms before you install a role. Removing a role keeps its downloads.

## Commands on the host

The host guide describes `setup`, `pair`, `roles`, `describe`, `add`, `remove`, `update`, `status` and `config`. See the [host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md) for native and Docker instructions.

### Host run window

A host task shows its progress in a run window. **Hide** leaves the task running. Open **Background tasks** to see it again. **Cancel task** asks before stopping the task.

### Commands between computers

A command sent through Martlet waits for Martlet on the target computer to run. A Linux host without Martlet can instead be managed over SSH. Role changes and host updates can restart services and interrupt their work.

## Updates

Choose **Update host** on the Devices map, or use **Settings › App updates › Update hosts**. The host update keeps its identity, pairings, roles and data. A Windows PC's host service follows that PC's Martlet version.

## A new address after a restart

Hosts can advertise a changed home address. Your computers check the host key before they continue to use the pairing. Local network discovery needs the applicable firewall access.

## After you reinstall a host

Martlet can restore a managed Linux host after it verifies that the computer is the same one. Recovery depends on the saved connection and identity evidence. If verification fails, Martlet stops. See the [network guide](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md) and [host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md) before you reinstall.

For outside access and friend sharing, see [Sharing with Friends](Sharing-with-Friends).
