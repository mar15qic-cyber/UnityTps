"""Run on a dedicated two-interface Linux test gateway. Dry-run by default.
Each direction affects only UDP 7770..7774. Delays are additional ONE-WAY delays,
not target RTT; first measure native RTT and choose values accordingly.
"""
import argparse
import ipaddress
import math
import shlex
import subprocess


def commands(up, down, server, delay, jitter, loss, reorder):
    result = []
    for interface, address_key, port_key in ((up, "dst", "dport"), (down, "src", "sport")):
        result.append(["tc", "qdisc", "add", "dev", interface, "root", "handle", "71:", "prio", "bands", "3", "priomap"] + ["0"] * 16)
        result.append(["tc", "qdisc", "add", "dev", interface, "parent", "71:3", "handle", "73:",
                       "netem", "delay", f"{delay}ms", f"{jitter}ms", "loss", f"{loss}%", "reorder", f"{reorder}%"])
        for port in range(7770, 7775):
            result.append(["tc", "filter", "add", "dev", interface, "protocol", "ip", "parent", "71:",
                           "prio", "1", "u32", "match", "ip", address_key, f"{server}/32", "match", "ip",
                           "protocol", "17", "0xff", "match", "ip", port_key, str(port), "0xffff", "flowid", "71:3"])
    return result


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--up-interface", required=True)
    p.add_argument("--down-interface", required=True)
    p.add_argument("--server-ip", required=True, type=ipaddress.IPv4Address)
    p.add_argument("--delay-ms", type=float, default=25)
    p.add_argument("--jitter-ms", type=float, default=10)
    p.add_argument("--loss-percent", type=float, default=1)
    p.add_argument("--reorder-percent", type=float, default=0)
    p.add_argument("--apply", action="store_true")
    p.add_argument("--clear", action="store_true")
    a = p.parse_args()
    if a.up_interface == a.down_interface or any(not math.isfinite(x) or x < 0 for x in (a.delay_ms,a.jitter_ms,a.loss_percent,a.reorder_percent)):
        p.error("Distinct dedicated gateway interfaces and nonnegative parameters required")
    if a.loss_percent > 100 or a.reorder_percent > 100:
        p.error("Invalid percentage")
    if a.clear:
        for interface in (a.up_interface,a.down_interface):
            cmd=["tc","qdisc","del","dev",interface,"root","handle","71:"]
            print(shlex.join(cmd))
            if a.apply:
                state=subprocess.check_output(["tc","qdisc","show","dev",interface],text=True)
                if "qdisc prio 71:" not in state:
                    raise RuntimeError("Refusing to remove an unrecognized qdisc")
                subprocess.run(cmd,check=True)
        return
    for cmd in commands(a.up_interface,a.down_interface,a.server_ip,a.delay_ms,a.jitter_ms,a.loss_percent,a.reorder_percent):
        print(shlex.join(cmd))
        if a.apply:
            subprocess.run(cmd,check=True)  # add fails rather than replacing existing shaping


if __name__ == "__main__":
    main()
