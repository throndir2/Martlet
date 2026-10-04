"""Forwards TCP ports: each argument is LISTEN_HOST:PORT=TARGET_HOST:PORT. Used to reach Martlet's host role services, which
listen only on their shared network namespace's loopback."""

import asyncio
import sys


async def pipe(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    try:
        while data := await reader.read(65536):
            writer.write(data)
            await writer.drain()
    except (ConnectionError, OSError):
        pass
    finally:
        writer.close()


def split(address: str) -> tuple[str, int]:
    host, _, port = address.rpartition(":")
    return host or "127.0.0.1", int(port)


async def forward(listen: str, target: str) -> None:
    target_host, target_port = split(target)

    async def handle(client_reader: asyncio.StreamReader, client_writer: asyncio.StreamWriter) -> None:
        try:
            server_reader, server_writer = await asyncio.open_connection(target_host, target_port)
        except OSError:
            client_writer.close()
            return
        await asyncio.gather(pipe(client_reader, server_writer), pipe(server_reader, client_writer))

    host, port = split(listen)
    server = await asyncio.start_server(handle, host, port)
    async with server:
        await server.serve_forever()


async def main(pairs: list[str]) -> None:
    await asyncio.gather(*(forward(*pair.split("=", 1)) for pair in pairs))


if __name__ == "__main__":
    asyncio.run(main(sys.argv[1:]))
