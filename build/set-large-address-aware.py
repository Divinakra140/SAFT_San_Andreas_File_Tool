#!/usr/bin/env python3
"""Set IMAGE_FILE_LARGE_ADDRESS_AWARE on a published 32-bit exe.

SAFT is a 32-bit process. By default that means 2 GB of user-mode address space, however much
memory the machine has. With this flag set, a 32-bit process running on a 64-bit host gets 4 GB
instead - and every machine SAFT actually runs on is a 64-bit host, including the Retroid, whose
SD865 is ARM64. Winlator emulates a 32-bit Windows for the app; the hardware underneath is not the
constraint, the exe's own flag is.

Why it matters here: what runs out is not memory but CONTIGUOUS address space. The pre-install
analysis allocates arrays past the 85 KB Large Object Heap threshold, the LOH is not compacted by
default, and so the process can fail to place a 256 KB array while holding only 40 MB live. That is
why the crash was intermittent and why it never reproduced on a 64-bit machine.

This does NOT fix that. It doubles the room the allocator has to find a gap in, which moves the odds
a long way but leaves the cause - roughly 194 MB of allocation churn per analysis - in place.

Done as a post-publish byte patch because .NET has no MSBuild property for this flag, and editbin is
a Windows-only tool unavailable when publishing from macOS.

Layout: the PE header's offset is the little-endian uint32 at 0x3c; the characteristics field is 22
bytes into that header; the flag is bit 0x0020.
"""
import struct
import sys

FLAG = 0x0020
PE_OFFSET_AT = 0x3C
CHARACTERISTICS_AT = 22
MACHINE_I386 = 0x014C


def main(path: str) -> int:
    data = bytearray(open(path, "rb").read())

    pe = struct.unpack_from("<I", data, PE_OFFSET_AT)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        print(f"  not a PE file, leaving alone: {path}")
        return 1

    machine = struct.unpack_from("<H", data, pe + 4)[0]
    if machine != MACHINE_I386:
        # A 64-bit build already has the whole address space; the flag is meaningless there.
        print(f"  not 32-bit (machine 0x{machine:04x}), nothing to do")
        return 0

    offset = pe + CHARACTERISTICS_AT
    before = struct.unpack_from("<H", data, offset)[0]
    if before & FLAG:
        print(f"  already large-address-aware (0x{before:04x})")
        return 0

    struct.pack_into("<H", data, offset, before | FLAG)
    open(path, "wb").write(data)
    print(f"  LARGE_ADDRESS_AWARE set: 0x{before:04x} -> 0x{before | FLAG:04x}  (2 GB -> 4 GB)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
