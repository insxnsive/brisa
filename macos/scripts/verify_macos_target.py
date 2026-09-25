"""Reject native Mach-O files newer than the advertised macOS 13 target."""
from pathlib import Path
import json
import struct
import sys


def minimum_macos(data, maximum=(13, 0, 0)):
    if len(data) < 32:
        raise ValueError('Truncated Mach-O header')
    header = struct.unpack_from('<IiiIIIII', data)
    if header[0] != 0xfeedfacf:
        raise ValueError('Expected a thin 64-bit Mach-O binary')
    offset, end = 32, 32 + header[5]
    if end > len(data):
        raise ValueError('Truncated Mach-O load commands')
    versions = []
    for _ in range(header[4]):
        if offset + 8 > end:
            raise ValueError('Missing Mach-O load command')
        command, size = struct.unpack_from('<II', data, offset)
        if size < 8 or offset + size > end:
            raise ValueError('Malformed Mach-O load command')
        if command in (0x32, 0x24):
            if size < (24 if command == 0x32 else 16):
                raise ValueError('Truncated deployment target')
            if command == 0x32 and struct.unpack_from('<I', data, offset + 8)[0] != 1:
                raise ValueError('Binary is not built for macOS')
            value = struct.unpack_from('<I', data, offset + (12 if command == 0x32 else 8))[0]
            version = (value >> 16, (value >> 8) & 255, value & 255)
            if version > maximum:
                raise ValueError(f'Binary requires macOS {version}; advertised maximum minimum is {maximum}')
            versions.append(version)
        offset += size
    if offset != end or not versions:
        raise ValueError('Missing or inconsistent macOS deployment target')
    return versions


if __name__ == '__main__':
    if len(sys.argv) < 2:
        raise SystemExit('Usage: verify_macos_target.py BINARY [BINARY ...]')
    for argument in sys.argv[1:]:
        path = Path(argument)
        print(json.dumps({'binary': str(path), 'minimumOS': minimum_macos(path.read_bytes())}))
