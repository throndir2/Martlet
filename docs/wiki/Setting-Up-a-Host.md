# Setting Up a Host

A Martlet host runs roles for the companion behind a paired gateway.

## Methods

| Method | Use it when |
| --- | --- |
| This PC with Docker Desktop | A Windows PC lends GPU roles through WSL 2 / Docker Desktop. |
| Another Windows PC running Martlet | Set it to **Use as a Martlet host** and pair. |
| SSH, Docker | Linux x86_64 with SSH and Docker. |
| SSH, native | Any x86_64 Linux with systemd, SSH and sudo. |
| On host, Docker/native | You run `martlet-host` manually. |

## Common commands

```text
setup
pair
roles
describe <role>
add <role>
remove <role>
machine
update
status
config
```

Martlet uses `--yes` with choices/secrets on stdin after your click.

## Pairing

Pairing shows an address and short code such as `K7QM-4XPA`. In Martlet, use **Devices › Add a computer › Enter a pairing code**. SSH and This PC setup pair automatically. Codes pause host roles until used or canceled.

## Linux native example

```sh
git clone https://github.com/throndir2/Martlet ~/Martlet
MARTLET_HOST_ADDRESS=192.168.1.20 ~/Martlet/deploy/host/martlet-host setup
~/Martlet/deploy/host/martlet-host pair
~/Martlet/deploy/host/martlet-host add audio2face
```

## Updates

**Update host** runs `martlet-host update`, keeping identity, pairings, roles and data. Hosts report their Martlet version on the Devices map.

More detail: [Host guide](https://github.com/throndir2/Martlet/blob/main/deploy/host/README.md), [Network](https://github.com/throndir2/Martlet/blob/main/docs/NETWORK.md), [Prerequisites](https://github.com/throndir2/Martlet/blob/main/docs/PREREQUISITES.md), [Troubleshooting](https://github.com/throndir2/Martlet/blob/main/docs/TROUBLESHOOTING.md).
