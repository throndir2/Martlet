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

## Windows with Docker Desktop

1. Install WSL 2 and Docker Desktop. **Settings › Tools › Prerequisites** can install them for you.
2. Open **Devices › Add a computer** and choose **Set up this PC**.
3. Agree to Windows' prompt. It opens port 9443 in Windows Firewall, for your home network only.

Martlet starts Docker Desktop, builds the host, sets it up and pairs it, with no console. Jobs that need the graphics card need an NVIDIA card and WSL 2 GPU support.

For another Windows PC, choose **Use as a Martlet host** there. It then shows under **Martlet on your network** on your other PCs: pick it, and choose **Allow** when both screens show the same check number. Or use **Pair your main PC › Show a pairing code** on that PC's Home.

## Linux over SSH

1. Make sure the Linux computer has an SSH server. It can be x86_64 or ARM64, with systemd (Ubuntu, Debian, Fedora, openSUSE, Arch, Raspberry Pi OS and others).
2. Open **Devices › Add a computer**. Under **A Linux computer over SSH**, type `user@computer` in **SSH target**.
3. Choose **Set up over SSH**.
4. Type the account password once. Martlet adds its own SSH key and uses only the key after that.
5. When a step needs `sudo`, type the sudo password. Martlet can remember it in Windows Credential Manager.

Martlet runs the host in Docker when the computer has it, and otherwise installs it natively. The host pairs by itself and shows on the Devices map. You never sign in to the Linux computer yourself.

- Martlet shows the computer's SSH key the first time and trusts only that key after. After you reinstall the computer, choose **Reset SSH trust**.
- A computer without internet gets the files it needs from your PC. Martlet cannot send Docker images or models yet.
- **Prepare a Linux computer** (on the map's **Add a computer** card) and **Prepare this computer** (on a host) get an Ubuntu computer ready: updates, Docker, the NVIDIA driver and container toolkit, no sleep, a virtual display, graphics card power limits, tools and Wake-on-LAN. Tick what you want and choose **Run selected**.
- A Linux host on the map can also have **Wake it up**, **Restart it** and **Shut it down**.

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

## Choose the host's jobs

After pairing, **Choose jobs for this host** lists the roles. Later, select the host on the Devices map and choose **Install** *role*, **Change** *role* **settings** or **Remove** *role*.

The roles are Thinking, Thinking pool, Listening, Speaking (one voice engine for each host), Lip-sync, Reading, Singing, Picture making, Motion, Voice recognition and Home Assistant. Before Martlet installs a role, it shows what the role needs, its terms and its choices. Your **Install** click is your confirmation. **Remove** keeps the downloads, so adding a role again is fast.

**Recommended setup** on Home can also install the roles for you.

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

## Updates

**Update host** keeps the host's identity, pairings, roles and data. **Settings › App updates › Update hosts** updates all your hosts, and **Update paired hosts automatically** keeps them on your PC's version. After the computer restarts, the host service starts again by itself.

## A new address after a restart

You don't need to reserve addresses in your router. When a host's computer gets another address, the host listens there and answers on UDP 9444, and your computers find it there by themselves, check its key and keep using the same pairing. A firewall on the host must let UDP 9444 in from your home network.

To use your hosts from outside your home or share them with a friend, see [Sharing with Friends](Sharing-with-Friends).

More detail: [Host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md), [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md), [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md), [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Troubleshooting](https://github.com/throndir2/Martlet/blob/main/docs/TROUBLESHOOTING.md).
