"""Summarize allowlisted JSONL telemetry. Local/synthetic evidence never certifies WAN playability."""
import argparse
import collections
import json
import math
from pathlib import Path


FIELDS = {"matchId", "runId", "kind", "sessionId", "releaseId", "map", "reason", "time", "displayTick", "usedTick",
          "rtt", "frameMs", "error", "rawError", "renderTick", "bufferMs", "serverTick", "inputTick",
          "shotId", "lifeEpoch", "connection", "targetConnection", "queueDepth"}


def percentile(values, fraction=.95):
    values = sorted(v for v in values if isinstance(v, (float, int)) and math.isfinite(v))
    return values[max(0, math.ceil(len(values) * fraction) - 1)] if values else None


def summarize(records):
    groups = collections.defaultdict(list)
    for r in records:
        groups[(r.get("releaseId"), r.get("sessionId"), r.get("map"), r.get("connection"))].append(r)
    output = []
    for identity, rows in sorted(groups.items(), key=lambda item: str(item[0])):
        duration = max(r.get("time", 0) for r in rows) - min(r.get("time", 0) for r in rows)
        rebases = collections.Counter(r.get("reason") for r in rows if r.get("kind") == "rebase")
        abnormal = sum(n for reason, n in rebases.items() if reason not in {"Initial", "DeathRespawn"})
        errors = [r["error"] for r in rows if r.get("kind") == "reconcile" and "error" in r]
        timing = [r["frameMs"] for r in rows if r.get("kind") == "server-tick" and "frameMs" in r]
        rejects = collections.Counter(r.get("reason") for r in rows if r.get("kind") == "shot-reject")
        output.append(dict(zip(("releaseId", "sessionId", "map", "connection"), identity)) | {
            "durationSeconds": duration, "reconcileSamples": len(errors), "errorP95Meters": percentile(errors),
            "abnormalRebases": abnormal, "abnormalRebasesPer10Minutes": abnormal * 600 / duration if duration > 0 else None,
            "rebaseReasons": dict(rebases), "shotRejections": dict(rejects),
            "playerSimulationP95Ms": percentile(timing),
            "serverTickThroughCombatP95Ms": percentile([r["frameMs"] for r in rows if r.get("kind") == "server-tick-total"]),
            "rttP95Ms": percentile([r["rtt"] for r in rows if r.get("kind") == "movement-owner"]),
            "movementMetricGate": "insufficient-evidence" if duration < 600 or len(errors) < 100 else
                "pass" if percentile(errors) <= .1 and abnormal * 600 / duration <= 1 else "fail",
        })
    return {"schemaVersion": 1, "access": "unverified", "experience": "unverified",
            "lagCompensationCorrectness": "unverified", "sessions": output,
            "note": "Attach external network identities, test cases and video review. Per-player simulation timing is not total server tick cost."}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("inputs", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    records = []
    for path in args.inputs:
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            if not line.strip():
                continue
            raw = json.loads(line)
            if not isinstance(raw, dict) or "kind" not in raw:
                raise ValueError("Not a PublicTestTelemetry record")
            records.append({k: v for k, v in raw.items() if k in FIELDS})
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(summarize(records), ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")


if __name__ == "__main__":
    main()
