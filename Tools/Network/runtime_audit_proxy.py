"""Bounded loopback UDP test gateway. Delays are additional ONE-WAY milliseconds.

Only explicitly configured loopback ports are bound; no firewall, global network or
system settings are changed. Loss applies to UDP datagrams, including packets that
the game transport will retransmit. A supplied stop file or deadline ends the run.
"""
import argparse
import heapq
import json
import random
import selectors
import socket
import time
from pathlib import Path


def atomic_json(path, data):
    temp = path.with_suffix(path.suffix + ".tmp")
    try:
        temp.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
        temp.replace(path)
        return True
    except PermissionError:
        # A Windows reader can briefly deny rename/delete sharing. Diagnostics must
        # neither terminate packet forwarding nor sleep inside its timing loop.
        # The next publication replaces the stale snapshot one second later.
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", required=True, type=Path)
    parser.add_argument("--server-port", type=int, default=27770)
    parser.add_argument("--ports", type=int, nargs="+", default=[27780, 27781, 27782])
    parser.add_argument("--seconds", type=float, default=3600)
    args = parser.parse_args()
    if len(set(args.ports)) != len(args.ports) or args.server_port in args.ports:
        parser.error("Ports must be distinct")
    args.directory.mkdir(parents=True, exist_ok=True)
    selector = selectors.DefaultSelector()
    server = ("127.0.0.1", args.server_port)
    peers, counts = {}, {}
    for port in args.ports:
        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        udp.bind(("127.0.0.1", port))
        udp.setblocking(False)
        selector.register(udp, selectors.EVENT_READ, port)
        counts[str(port)] = dict(up=0, down=0, dropped_up=0, dropped_down=0, sent=0, errors=0)
    config = dict(seq=0, delay_ms=0, jitter_ms=0, loss_percent=0, seed=927, caseId="baseline")
    rng = random.Random(config["seed"])
    pending = []
    serial = 0
    skipped_publications = 0
    start = last_config = last_state = time.monotonic()
    evidence = (args.directory / "proxy.events.jsonl").open("w", encoding="utf-8")
    try:
        while time.monotonic() - start < min(max(args.seconds, 1), 3600):
            now = time.monotonic()
            if (args.directory / "proxy.stop").exists():
                break
            if now - last_config >= .05:
                last_config = now
                try:
                    incoming = json.loads((args.directory / "network.json").read_text(encoding="utf-8-sig"))
                    if incoming.get("seq", 0) > config.get("seq", 0):
                        config = incoming
                        rng = random.Random(config.get("seed", 927))
                        evidence.write(json.dumps(dict(kind="config", utc=time.time(), config=config)) + "\n")
                        evidence.flush()
                except (FileNotFoundError, json.JSONDecodeError, PermissionError):
                    pass
            for key, _ in selector.select(.002):
                udp, port = key.fileobj, key.data
                try:
                    payload, source = udp.recvfrom(65535)
                except (BlockingIOError, ConnectionResetError):
                    continue
                down = source == server
                if not down:
                    peers[port] = source
                destination = peers.get(port) if down else server
                if destination is None:
                    continue
                direction = "down" if down else "up"
                counter = counts[str(port)]
                counter[direction] += 1
                setting = config.get("peers", {}).get(str(port), config)
                if rng.random() * 100 < min(max(float(setting.get("loss_percent", 0)), 0), 100):
                    counter["dropped_" + direction] += 1
                    continue
                delay = max(0, float(setting.get("delay_ms", 0)))
                jitter = max(0, float(setting.get("jitter_ms", 0)))
                sequence = setting.get("jitter_sequence_ms", [])
                if sequence:
                    delay = float(sequence[serial % len(sequence)])
                else:
                    delay += rng.uniform(-jitter, jitter)
                serial += 1
                heapq.heappush(pending, (now + max(0, delay) / 1000, serial, udp, destination, payload, port))
            while pending and pending[0][0] <= time.monotonic():
                _, _, udp, destination, payload, port = heapq.heappop(pending)
                try:
                    udp.sendto(payload, destination)
                    counts[str(port)]["sent"] += 1
                except OSError:
                    counts[str(port)]["errors"] += 1
            if now - last_state >= 1:
                last_state = now
                if not atomic_json(args.directory / "proxy.state.json", dict(utc=time.time(), config=config, counts=counts,
                            pending=len(pending), skippedPublications=skipped_publications,
                            peers={str(k): list(v) for k, v in peers.items()})):
                    skipped_publications += 1
    finally:
        evidence.close()
        for key in list(selector.get_map().values()):
            key.fileobj.close()
        selector.close()
        atomic_json(args.directory / "proxy.final.json", dict(utc=time.time(), counts=counts, pending=len(pending), skippedPublications=skipped_publications))


if __name__ == "__main__":
    main()
