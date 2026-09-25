#!/usr/bin/env python3
"""Validate the explicit loopback-only transport diagnostic, never a live VPN."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess


EXPECTED = {"schemaVersion": 2, "scope": "loopback-only", "tcp4": True,
            "udp4": True, "dnsA": True, "tcp6": True, "udp6": True,
            "dnsAAAA": True, "familyGate": True, "shutdown": True}


def validate_report(raw):
    if len(raw) > 4096:
        raise ValueError("Transport report exceeds its bound")

    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Duplicate transport report field")
            result[key] = value
        return result

    try:
        result = json.loads(raw, object_pairs_hook=unique_object)
    except (ValueError, RecursionError) as error:
        raise ValueError("Malformed transport report") from error
    if not isinstance(result, dict) or result.keys() != EXPECTED.keys():
        raise ValueError("Unexpected transport report fields")
    if any(type(result[key]) is not type(value) or result[key] != value
           for key, value in EXPECTED.items()):
        raise ValueError("Transport checks did not all pass in loopback scope")
    return result


class AcceptanceFailure(Exception):
    """Fixed diagnostic code; child output is deliberately never included."""


def accept(binary, output):
    binary = Path(binary).resolve()
    result = {"schemaVersion": 2, "scope": "loopback-only", "status": "failed",
              "binary": binary.name}
    try:
        try:
            result["binarySha256"] = hashlib.sha256(binary.read_bytes()).hexdigest()
        except OSError:
            raise AcceptanceFailure("unavailable-binary") from None
        completed = subprocess.run([str(binary), "--self-test"], stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   timeout=35, check=False)
        if completed.returncode != 0:
            raise AcceptanceFailure("self-test-failed")
        if completed.stderr:
            raise AcceptanceFailure("unexpected-stderr")
        checks = validate_report(completed.stdout)
        if hashlib.sha256(binary.read_bytes()).hexdigest() != result["binarySha256"]:
            raise AcceptanceFailure("binary-changed")
        result.update(status="passed", checks=checks)
    except AcceptanceFailure as error:
        result["reason"] = str(error)
    except subprocess.TimeoutExpired:
        result["reason"] = "timeout"
    except OSError:
        result["reason"] = "launch-failed"
    except ValueError:
        result["reason"] = "invalid-report"
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = accept(args.binary, args.output)
    print(f"Loopback transport: {result['status']}")
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
