# Setting Up a Host

A host lends a computer's graphics card and processor to Martlet. It does jobs (roles) such as thinking, listening, a voice or lip-sync for your companion PCs. Hosts run on Windows, Linux, Macs and ARM64 computers such as a Raspberry Pi 5.

## Ways to set up a host

| Computer | How |
| --- | --- |
| This Windows PC | **Devices › Add a computer › This PC › Set up this PC**. It needs Docker Desktop (WSL 2). |
| Another Windows PC | Install Martlet and Docker Desktop there, and choose **Settings › What this PC is for › Use as a Martlet host**. |
| A Linux computer | **Devices › Add a computer › A Linux computer over SSH**. Martlet sets it up from your PC. |
| A Mac | Run the Mac host's two commands on the Mac ([below](#macs)). |
| Any host, by hand | Run `martlet-host` on the host ([below](#commands-on-the-host)). |

You pair a host once. Every other computer in your [Martlet network](Multiple-Computers#join-your-martlet-network) then pairs with it by itself within a minute.

### The Add a computer window

Open **Devices › Add a computer**. The window has two steps: **Connect** and **Roles**.

1. Under **Martlet on your network**, pick a computer that runs a host and choose **Connect**. Choose **Allow** on that computer when both screens show the same check number. You type nothing. See [Pairing](#pairing).
2. If the computer isn't listed, choose **Enter a pairing code**, or set up a new host under **Or set up a new host**.
3. After you connect, **Choose jobs for this host** lists the jobs (roles). See [Choose the host's jobs](#choose-the-hosts-jobs).

Other things in the window:

- **Join with an invite** is for a PC away from home. Paste the invite that you made at home and sign in. This PC then joins your Martlet network by itself. A friend's host joins as a host shared with this PC: choose it for a job under **Share › Hosts shared with this PC**.
- **Its private network address** (under **A Linux computer over SSH**) is filled in from the computer. Change it only if your other computers reach that computer at a different address.
- For another Windows PC, install Martlet there and choose **Use as a Martlet host** on its Home page. It then shows under **Martlet on your network**.
- **This PC's device ID** is how this PC shows on the host. Martlet sends it by itself when it connects.
- **Update host** updates the host. Its pairings and jobs stay.
- **Forget host** removes the host from this PC only. To remove this PC from the host too, revoke this PC's device ID in the host console.
- **Reset SSH trust** forgets the SSH key that Martlet trusts for a Linux computer and its saved sudo password. Use it only after you reinstall that computer. The next connection asks you to trust the computer again.

### Prepare a Linux computer

**Prepare a Linux computer** (on the map's **Add a computer** card) and **Prepare this computer** (on a Linux host) get an Ubuntu computer ready for host jobs over SSH.

1. Type `user@computer` in **SSH target** and choose **Read status**. Martlet reads the computer first and changes nothing.
2. Tick what you want, or choose **Select missing** to tick everything that isn't in place yet.
3. Choose **Run selected**. Martlet changes only what you ticked. Most changes need administrator rights (sudo), and some installs take several minutes.
4. When the banner says that a restart is needed, choose **Restart it**. Martlet waits for the computer and reads its status again.

- **Virtual display** is for a desktop with no monitor attached. It isn't used while the computer starts in text mode.
- **GPU power settings**: persistence mode keeps GPU jobs ready, and lower power limits can make the card cooler and quieter.
- **Wake-on-LAN** needs a wired network connection. After **Shut it down**, Martlet can start the computer again only with Wake-on-LAN (**Wake it up**).
- **Restart it** stops the work on the computer, Martlet jobs too, until it comes back. If it doesn't come back within 10 minutes, check its power and network, then choose **Read status**.
- If you stop or close the window during setup, some work can stay half done. Run the same item again; that usually finishes it.

## Windows with Docker Desktop

1. Install WSL 2 and Docker Desktop. **Settings › Tools › Prerequisites** can install them for you.
2. Open **Devices › Add a computer** and choose **Set up this PC**.
3. Agree to Windows' prompt. It opens port 9443 in Windows Firewall, for your home network only.

Martlet starts Docker Desktop, builds the host, sets it up and pairs it, with no console. Jobs that need the graphics card need an NVIDIA card and WSL 2 GPU support.

On a host PC, Home lists the steps that are left. Each step ticks by itself when it is done, because Martlet checks them every few seconds.

- The first start of Docker Desktop can take a few minutes. You don't have to wait: set up the host service now, and it carries on as soon as Docker Desktop runs.
- Windows may ask you to allow private-network access for the host service. If Windows Firewall isn't changed, your other PCs may not reach this host.
- **Start host service** and **Set up again** keep your pairings and roles.
- After a start, the host service can take a moment to answer on your network. If **not answering** stays, choose **Set up again**.
- When Docker Desktop doesn't start, or Windows isn't ready for it, the progress window shows the details. See [Docker Desktop or WSL won't start](Troubleshooting#docker-desktop-or-wsl-wont-start).

For another Windows PC, choose **Use as a Martlet host** there. It then shows under **Martlet on your network** on your other PCs: pick it, and choose **Allow** when both screens show the same check number. Or use **Pair your main PC › Show a pairing code** on that PC's Home.

### Windows virtualization

Docker Desktop runs its engine in WSL 2, so Windows needs hardware virtualization, Virtual Machine Platform, Windows Subsystem for Linux (WSL) and their services. Martlet checks them before it starts Docker Desktop.

1. When something is off, Martlet turns on the Windows features and WSL. Windows asks for administrator approval once, and this can take a few minutes.
2. When Windows must restart to finish, Martlet asks first. Save your work, then let Windows restart. Restarting only Docker Desktop can't finish this change.
3. After you sign in again, Martlet opens and continues the setup by itself.

When hardware virtualization is off in the PC's firmware (UEFI/BIOS), Docker Desktop says *Virtualization support not detected*. Martlet can't change the firmware from Windows. On a new PC, Martlet turns on the Windows features first, so that one restart finishes both. Then:

1. Let Martlet restart the PC into its firmware settings. Save your work first.
2. Turn on Intel VT-x or AMD SVM, then save and exit.
3. Sign in again. Martlet continues the setup.

On a virtual machine, ask the administrator of its host to turn on nested virtualization. If WSL 2 or its services still don't start after a restart, repair Virtual Machine Platform and WSL through Windows setup. Don't reset Docker's data.

### When setting up a host fails

The run window shows each step and what Docker Desktop, Windows or the host said. Use it to see why a step stopped.

- **Docker Desktop is installing or starting**: the first start can take a few minutes. Accept Docker's terms if Docker Desktop asks. Installing Docker Desktop means that you accept Docker's Subscription Service Agreement (free for personal use).
- **Docker Desktop gave up starting**: Docker Desktop sometimes checks Windows before Windows is ready. Martlet then restarts Docker Desktop once. If it still can't start, restart Windows and try again. Check that **Task Manager › Performance › CPU** shows *Virtualization: Enabled*.
- **Docker Desktop can't show status here**: older Docker Desktops don't report their status to Martlet. Open Docker Desktop to see it.
- **Administrator approval was declined**: Windows was not changed. Run the step again and approve the prompt.
- **Firewall unchanged**: your other PCs may not reach this host. When Windows treats your network as Public, Martlet asks to mark it as Private and to let your other computers connect to and find this PC. If Windows Firewall also blocks Docker Desktop, allow Docker Desktop in **Windows Security**.
- **This PC is not on a private network**: connect it to your home network first.
- **Hardware details aren't available yet**: update the host from the Devices map, or check the host and try again.
- **Could not find Martlet's prerequisites installer**: reinstall Martlet, then try again.
- **The host engine isn't prepared on this PC yet**: right after Martlet updates itself, a change to this PC's host service runs with the engine that the host service runs now. The host service moves to the new version after that.

See also [Docker Desktop or WSL won't start](Troubleshooting#docker-desktop-or-wsl-wont-start).

## Linux over SSH

1. Make sure the Linux computer has an SSH server. It can be x86_64 or ARM64, with systemd (Ubuntu, Debian, Fedora, openSUSE, Arch, Raspberry Pi OS and others), or a NAS with Docker (TerraMaster, Synology, QNAP).
2. Open **Devices › Add a computer**. Under **A Linux computer over SSH**, type `user@computer` in **SSH target**. When SSH doesn't use port 22, add the port, for example `admin@192.168.1.9:9222`.
3. Choose **Set up over SSH**.
4. Type the account password once. Martlet adds its own SSH key and uses only the key after that. When your own SSH key in `%USERPROFILE%\.ssh` already signs in there, Martlet uses it once instead and asks for no password. When Windows' OpenSSH already trusts the computer, Martlet doesn't ask you to trust it again.
5. When a step needs `sudo`, type the sudo password. Martlet can remember it in Windows Credential Manager.

Martlet runs the host in Docker when the computer has it, and otherwise installs it natively. The host pairs by itself and shows on the Devices map. You never sign in to the Linux computer yourself.

- On a NAS (TerraMaster, Synology, QNAP, Unraid or TrueNAS), install Docker with the NAS's own app first: **Docker Engine** in the TOS App Center, **Container Manager** in Synology's Package Center or **Container Station** in QNAP's App Center. Martlet sets up a NAS only in Docker and installs nothing into the NAS system, so a NAS update can't break the host. Without Docker, Martlet stops and tells you which app to install.
- Martlet shows the computer's SSH key the first time and trusts only that key after. After you reinstall the computer, choose **Reset SSH trust**.
- A computer without internet gets the files it needs from your PC. Martlet cannot send Docker images or models yet.
- **Prepare a Linux computer** (on the map's **Add a computer** card) and **Prepare this computer** (on a host) get an Ubuntu computer ready: updates, Docker, the NVIDIA driver and container toolkit, no sleep, a virtual display, graphics card power limits, tools and Wake-on-LAN. Tick what you want and choose **Run selected**.
- A Linux host on the map can also have **Wake it up**, **Restart it** and **Shut it down**.

### Signing in over SSH

- The first time, Martlet shows the computer's SSH key fingerprint and asks you to trust it. Martlet remembers that key for every later connection and refuses to connect if it changes. After you reinstall the computer, choose **Reset SSH trust**.
- Martlet uses the account password once, to add its own SSH key. It doesn't save the password.
- When your own SSH key signs in, Martlet uses it once to add its key. It doesn't copy or keep your key.
- The sudo password goes only over the SSH connection and never shows in a log. Without it, the steps that need administrator rights stop.

### When setup over SSH stops

Martlet checks the computer first and tells you what is missing:

- **A NAS without Docker**: install Docker with the NAS's own app (see above), then try again.
- **No Docker and no systemd**: Martlet needs systemd to set up the host without Docker. Install Docker Engine, then try again.
- **The account can't use Docker**: use an account with Docker access or sudo, such as the NAS's administrator account.
- **No systemd for the native method**: choose the Docker method.
- **No internet**: Martlet sends a computer without internet what its gateway needs, but only to a Linux host that it runs without Docker. It can't send the Docker images and models of a role yet. Connect the computer to the internet to add a role, or set it up without Docker.
- **Paired, but the host reported an exit code while restarting**: the pairing is done. Check the host's status.
- **Stopped: git is not installed here**: install git with the system's package manager. A computer without internet gets what it needs when you set it up from Martlet.
- **Stopped: there is no Martlet engine in ~/Martlet**: set the computer up again from Martlet.

## Macs

The Mac host lends the Mac's own Ollama and whisper.cpp to your other computers. On Apple silicon it uses the Mac's graphics chip. It runs in the background while you are logged in.

1. Install [Ollama](https://ollama.com/download). For listening, run `brew install whisper-cpp`.
2. Run these two commands in Terminal:

```sh
"/Applications/Martlet.app/Contents/Resources/host/Martlet.Gateway.Host.Linux" macos-setup
"/Applications/Martlet.app/Contents/Resources/host/Martlet.Gateway.Host.Linux" macos-pair
```

A Mac host never offers jobs that need an NVIDIA card. Docker Desktop on a Mac also works, but only on the processor. The Mac host is new and was not tried on a real Mac yet. See [Linux and Mac](Linux-and-Mac).

## ARM64 computers

A Raspberry Pi 5, an NVIDIA DGX Spark or Jetson, and Windows on Arm or Apple silicon with Docker Desktop can be hosts. They can do thinking (Ollama), the Thinking pool, listening (whisper or Parakeet), voice recognition and Home Assistant. A Raspberry Pi runs only small models (1-3B) and needs a board with 8 GB or more.

Cloned voices, singing, picture making, motion and lip-sync need an x86_64 PC with an NVIDIA card. The Devices map shows them as not available and says why. ARM64 hosts were not tried on real ARM64 hardware yet.

## Pairing

- **Set up this PC** and **Set up over SSH** pair by themselves.
- To pair by code, the host shows its address and a short code such as `K7QM-4XPA`. In Martlet, open **Devices › Add a computer › Enter a pairing code**. Type the **Host address** and the **Code**, then choose **Pair with host**.
- A code works once. It stays valid until a computer uses it or you cancel it. The host's jobs pause until then. Five wrong tries close the code.
- With **Martlet on your network**, you type nothing. Both computers show the same check number, so you know that they talk to each other.
- If a computer isn't listed, guest Wi-Fi, a VPN, another subnet or a firewall can hide it. Open Martlet on that computer and turn on **Let my other computers find this PC**, then choose **Find again**. Or enter the address and pairing code that it shows.

To pair a host PC with your main PC from the host PC's Home:

1. On your main PC, choose **Devices › Add a computer**. The host PC is listed under **Martlet on your network**.
2. Press **Connect** there.
3. Press **Allow** on the host PC when both screens show the same check number.

Or choose **Show a pairing code** on the host PC, and type it in **Enter a pairing code** on the other computer. To use the host from more computers, do the same on each one. **Pair another computer** works at any time.

- Paired computers connect only on your private network.
- **Settings › Your other computers › Let my other computers find this PC and ask to use its hosts** uses port 9444. It also covers the hosts that this PC reaches over SSH. Each request shows a check number, and nothing happens until you choose **Allow** on this PC.
- A computer can also ask to join your Martlet network through a host. Allow it only if that computer shows the same check number.

## Choose the host's jobs

After pairing, **Choose jobs for this host** lists the roles. Later, select the host on the Devices map and choose **Install** *role*, **Change** *role* **settings** or **Remove** *role*.

The roles are Thinking, Thinking pool, Listening, Speaking (one voice engine for each host), Lip-sync, Reading, Singing, Picture making, Motion, Voice recognition and Home Assistant. Before Martlet installs a role, it shows what the role needs, its terms and its choices. Your **Install** click is your confirmation. **Remove** keeps the downloads, so adding a role again is fast. While a role installs, the run window shows the progress.

**Recommended setup** on Home can also install the roles for you. On a host PC, the **Add roles** step on Home adds or removes roles at any time.

## Commands on the host

```text
setup               gateway, identity and start at boot (once for each host)
pair                show this host's address and a one-use pairing code
roles               what this host can run
describe <role>     a role's terms, secrets and choices
add <role>          install a role or change its settings, for example add ollama
remove <role>       turn a role off (keeps its downloads)
update              update the host (identity, pairings, roles and data stay)
status | config     show the gateway and roles | print host.json
```

On native Linux:

```sh
git clone https://github.com/throndir2/Martlet ~/Martlet
MARTLET_HOST_ADDRESS=192.168.1.20 ~/Martlet/deploy/host/martlet-host setup
~/Martlet/deploy/host/martlet-host pair
~/Martlet/deploy/host/martlet-host add audio2face
```

For the Docker commands, see the [host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md#docker-any-docker-host-including-windows).

### Host run window

Each change on a host (install, change or remove a role, update, set up) runs in a window that shows its progress.

1. To keep working, choose **Hide** (or press Esc). The task keeps running.
2. To see the task again, open **Background tasks** in Martlet's main window.
3. To stop the task, choose **Cancel task**. Martlet asks first, because the work that isn't finished stops, and you may need to start it again.

When the task is finished, **Hide** becomes **Close**. A new model or a large download can take a while. This PC starts to use the role when it is ready.

### Commands between computers

- On a computer with Martlet, the command goes to the host's gateway, and Martlet there runs it. When Martlet there hasn't checked in, the command waits until Martlet runs there. Open Martlet on that computer, or let it start with Windows. **Cancel task** takes back a command that waits.
- A Linux computer without Martlet is reached over SSH instead.
- A host's service restarts while it updates. Martlet keeps trying while it doesn't answer.
- **Secrets** (keys and tokens that a role asks for): on this PC's host they stay on this PC. Over the paired connection they are kept only in memory until Martlet there takes them, and are then saved there. Over SSH they are sent and saved there. **Already saved** means you can leave the box empty to keep the saved one.
- **Change** *role* **settings** shows what the role runs with now. What you don't change stays as it is. A host with an older Martlet applies all the choices shown; update it to see what it runs with now.
- **Remove** keeps the role's data, so adding it again is quick.
- A computer runs only one voice engine. Installing another one stops the old one first and frees its graphics memory. The downloads stay.
- On a host with several NVIDIA graphics cards, Martlet asks which card a role uses, so each part of Martlet (thinking, listening, its voice) can have a card of its own.

### Roles and what they send

| Role | What goes to the host | Good to know |
|---|---|---|
| Lip-sync | The generated voice audio | |
| Thinking | Your messages and the recent conversation | |
| Thinking pool | A job's text (for a think, the task and the conversation so far) | Background jobs run beside Thinking while the conversation goes on. On a host with several cards, each extra card runs one more model, pinned to a card that no other role uses, so the host works on several jobs at full speed at the same time. |
| Listening | Your recorded speech | It is not stored. |
| Speaking (Chatterbox Turbo, Original, Nano) | The reply text and the voice sample (longer than 5 seconds) | MIT-licensed models. Replies carry an inaudible watermark. Nano runs on the processor when there is no graphics card. |
| Speaking (F5-TTS) | The reply text and the voice sample | |
| Speaking (XTTS-v2) | The reply text and the voice sample | Starts to speak before a sentence is finished. Noncommercial use only. |
| Speaking (GPT-SoVITS) | The reply text and the voice sample (3-10 seconds) | Good for anime-style voices. Starts each sentence as soon as it is made. |
| Speaking (Dia) | The reply text and the voice sample | English only. Apache-2.0. |
| Singing | The lyrics, the style and the voice's recording | A song takes a few minutes. It frees the graphics card when it is idle. |
| Picture making | The picture descriptions | It frees the graphics card when it is idle. |
| Reading | Screenshots | Read in memory and not kept. Reads run beside the conversation, so replies never wait for them. |
| Motion | The English motion descriptions | Downloads about 20 GB. It frees the graphics card when it is idle. |
| Voice recognition | The speech Martlet hears, one utterance at a time | Analyzed in memory and not kept. It uses the same voice models as this PC, so its voiceprints match your voice list. |

## Updates

**Update host** keeps the host's identity, pairings, roles and data. **Settings › App updates › Update hosts** updates all your hosts, and **Update paired hosts automatically** keeps them on your PC's version. After the computer restarts, the host service starts again by itself.

A PC's own host service follows that PC's Martlet version. Martlet updates it by itself in the background, or when the service runs again. **Update host service** on Home does it now. Pairings and roles stay.

### This PC's host service

- Martlet installs its own update first. The host service follows that version right after.
- Martlet waits while it replies to you or hears you. During the update Martlet stays usable, but the host service stops answering for a moment when it restarts at the end.
- When the host service is busy with another change, nothing changes. Martlet tries again at the time it shows.
- When the update stops, the host-runs log shows why. **Settings › App updates › Update hosts** or **Update host service** on Home tries again.
- When you cancel the update, Martlet doesn't start it again by itself for that version.

Another host that is busy shows **Waiting to update**. Nothing changes there, and Martlet tries again every few minutes. When another computer already updates that host, Martlet checks again in a few minutes and shows when the host runs the new version.

### Start with Windows

On a host PC, Martlet starts Docker Desktop and the host's roles by itself, and loads their models. Then the first request from your other computers doesn't wait. Martlet only does what needs no install and no approval. It doesn't start them when:

- Docker Desktop isn't installed. The **Docker Desktop** step on Home installs it.
- The host service isn't set up on this PC. Choose **Set up host service**.
- The host service runs no roles. Add roles on Home.
- Windows isn't ready for Docker Desktop. The **Docker Desktop** step shows what to do.

When the start fails, **Background tasks** shows why, in the task **Start this host's roles**.

### Keep the host awake

A host PC stays awake while Martlet runs, so your other computers can reach its host service at any time. A PC whose own host service serves your other computers stays awake while it serves them.

- The screen can still turn off.
- **Sleep** and shutting down still work.
- When Windows doesn't let Martlet keep the PC awake, the PC can sleep when it is idle. Your other computers then lose its jobs until it wakes.

### When a host can't be used

- **This PC's pairing key can't be read** from Windows Credential Manager: jobs can't run on that host. Pair it again in **Devices › Add a computer**.
- **This PC isn't paired with it now** (it was forgotten here or removed from your Martlet network): add it again in **Devices › Add a computer**, or choose another computer for its jobs.
- **Older than recordings**: the host can't take the hearing test. Update Martlet on that host, then test again.

## A new address after a restart

You don't need to reserve addresses in your router. When a host's computer gets another address, the host listens there and answers on UDP 9444, and your computers find it there by themselves, check its key and keep using the same pairing. A firewall on the host must let UDP 9444 in from your home network.

On a Windows host PC, Martlet moves the host service to the new address by itself. Pairings and roles stay. If Martlet can't move it, **Background tasks** says why. Then choose **Set up again** on Home.

To use your hosts from outside your home or share them with a friend, see [Sharing with Friends](Sharing-with-Friends).

More detail: [Host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md), [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md), [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md), [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Troubleshooting](https://github.com/throndir2/Martlet/blob/main/docs/TROUBLESHOOTING.md).
