# Multiple Computers

Martlet is one app across your computers.

![Devices map](https://raw.githubusercontent.com/throndir2/Martlet/main/docs/images/devices.png)

## Roles

A **companion PC** is where you talk, hear replies and show the character. A **host PC** lends CPU/GPU work. One computer can be both.

## Devices map

Open **Devices** to see this PC, other desktops, hosts and cloud services. Selecting a device shows roles, connection status, update controls and actions to hand jobs to it.

## Martlet network

Pair a host once and other member desktops can pair with it automatically. A new desktop joins through **Connect to your other computers** or **Devices › Add a computer › Martlet on your network**. Check that both screens show the same number before allowing.

## Shared state

**Devices › Settings for all devices › Keep Martlet the same on all my computers** shares job placement, settings, API key references, memories, recognized voices, speaking voices, characters, Home Assistant and logs through paired hosts. Device-specific microphones, screens, local tools and startup choices stay local.

## Failover and sharing work

Failover moves a job only to another of your own hosts running the same engine. It never silently moves to a cloud provider. **Devices › Sharing work** can let busy speaking/listening hosts pass work to another host.

More detail: [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md), [Cluster](https://github.com/throndir2/Martlet/blob/main/docs/CLUSTER.md), [Diagnostics](https://github.com/throndir2/Martlet/blob/main/docs/DIAGNOSTICS.md), [Platforms](https://github.com/throndir2/Martlet/blob/main/docs/PLATFORMS.md).
