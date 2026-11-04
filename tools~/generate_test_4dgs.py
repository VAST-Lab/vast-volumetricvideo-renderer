#!/usr/bin/env python3
"""
tools/generate_test_4dgs.py
Generates a synthetic 4DGS (.4dgs) volumetric video file and PLY/SPZ test sequence
for testing 4D Gaussian Splatting in Unity 6000.6 WebGPU.
Uses only Python standard library.
"""

import sys
import os
import math
import struct

def wrap_zstd_raw_frame(data: bytes) -> bytes:
    """Wraps uncompressed bytes in an RFC 8878 Zstandard frame using raw blocks."""
    out = bytearray()
    # Magic Number
    out.extend(struct.pack("<I", 0xFD2FB528))
    # Frame Header Descriptor: singleSegment=1 (bit 5: 32), sizeFlag=2 (bits 6-7: 128) -> 160 (0xA0)
    out.append(0xA0)
    # Frame Content Size (4 bytes)
    out.extend(struct.pack("<I", len(data)))

    max_block = 128 * 1024
    offset = 0
    while offset < len(data):
        block_size = min(len(data) - offset, max_block)
        is_last = (offset + block_size) >= len(data)
        # Block Header (3 bytes): Bit 0: isLast, Bits 1-2: type (0=Raw), Bits 3-23: size
        header = (1 if is_last else 0) | (0 << 1) | (block_size << 3)
        out.append(header & 0xFF)
        out.append((header >> 8) & 0xFF)
        out.append((header >> 16) & 0xFF)
        out.extend(data[offset:offset + block_size])
        offset += block_size

    return bytes(out)

def encode_smallest3_rotation(x, y, z, w):
    """Encodes normalized quaternion into SPZ smallest-3 packed uint32."""
    components = [x, y, z, w]
    abs_comp = [abs(c) for c in components]
    largest = abs_comp.index(max(abs_comp))
    if components[largest] < 0:
        components = [-c for c in components]

    packed = largest
    for c in range(4):
        if c == largest:
            continue
        mag = min(511, int(round(511.0 * (abs(components[c]) / 0.7071067811865475))))
        sign = 512 if components[c] < 0 else 0
        packed = (packed << 10) | sign | mag

    return packed

def make_spz_v4_keyframe(splats):
    """Encodes a list of splat dicts into a valid SPZ v4 binary payload."""
    count = len(splats)
    fraction_scale = 4096.0 # 12 fractional bits

    pos_stream = bytearray()
    alpha_stream = bytearray()
    col_stream = bytearray()
    scale_stream = bytearray()
    rot_stream = bytearray()

    for s in splats:
        # Positions (fixed-24)
        for val in s["pos"]:
            fval = int(round(val * fraction_scale)) & 0xFFFFFF
            pos_stream.append(fval & 0xFF)
            pos_stream.append((fval >> 8) & 0xFF)
            pos_stream.append((fval >> 16) & 0xFF)

        # Alpha (1 byte)
        alpha_stream.append(int(round(s.get("alpha", 1.0) * 255.0)) & 0xFF)

        # Color (3 bytes: (dc * 0.15 + 0.5) * 255)
        for c in s.get("color", (1.0, 1.0, 1.0)):
            col_stream.append(int(round(max(0.0, min(1.0, c)) * 255.0)) & 0xFF)

        # Scale (3 bytes: (log(s) + 10) * 16)
        for sc in s.get("scale", (0.05, 0.05, 0.05)):
            log_s = math.log(max(1e-5, sc))
            scale_stream.append(int(round(max(0, min(255, (log_s + 10.0) * 16.0)))))

        # Rotation (4 bytes: smallest-3 uint32)
        qx, qy, qz, qw = s.get("rot", (0.0, 0.0, 0.0, 1.0))
        packed_rot = encode_smallest3_rotation(qx, qy, qz, qw)
        rot_stream.extend(struct.pack("<I", packed_rot))

    raw_streams = [bytes(pos_stream), bytes(alpha_stream), bytes(col_stream), bytes(scale_stream), bytes(rot_stream)]
    compressed_streams = [wrap_zstd_raw_frame(st) for st in raw_streams]

    # SPZ v4 Header (32 bytes)
    out = bytearray()
    out.extend(struct.pack("<IIIBBBBIIII",
        0x5053474E,     # Magic: "NGSP"
        4,              # Version: 4
        count,          # Splat Count
        0,              # Degree: 0
        12,             # FractionalBits: 12
        0,              # Flags: 0
        5,              # StreamCount: 5
        32,             # TocOffset = 32
        0, 0, 0         # Reserved
    ))

    # TOC (5 * 16 bytes = 80 bytes)
    for c_st, r_st in zip(compressed_streams, raw_streams):
        out.extend(struct.pack("<QQ", len(c_st), len(r_st)))

    # Streams
    for c_st in compressed_streams:
        out.extend(c_st)

    return bytes(out)

def make_delta_pframe(prev_splats, curr_splats, delta_scale=0.001):
    """Encodes a temporal P-frame delta stream between two frames."""
    count = len(curr_splats)
    mask_bytes = (count + 7) // 8

    mask = bytearray(mask_bytes)
    dx_list = []
    dy_list = []
    dz_list = []

    for i in range(count):
        px0, py0, pz0 = prev_splats[i]["pos"]
        px1, py1, pz1 = curr_splats[i]["pos"]

        dx = int(round((px1 - px0) / delta_scale))
        dy = int(round((py1 - py0) / delta_scale))
        dz = int(round((pz1 - pz0) / delta_scale))

        dx = max(-32767, min(32767, dx))
        dy = max(-32767, min(32767, dy))
        dz = max(-32767, min(32767, dz))

        dx_list.append(dx)
        dy_list.append(dy)
        dz_list.append(dz)

        # Mark active
        if dx != 0 or dy != 0 or dz != 0:
            mask[i // 8] |= (1 << (i % 8))

    delta_payload = bytearray()
    # 8-byte header: float deltaPosScale, byte flags, byte res[3]
    delta_payload.extend(struct.pack("<fBBBB", delta_scale, 0, 0, 0, 0))
    delta_payload.extend(mask)
    for v in dx_list:
        delta_payload.extend(struct.pack("<h", v))
    for v in dy_list:
        delta_payload.extend(struct.pack("<h", v))
    for v in dz_list:
        delta_payload.extend(struct.pack("<h", v))

    return wrap_zstd_raw_frame(bytes(delta_payload)), len(delta_payload)

def generate_sphere_splats(num_splats=400, radius=1.0, t=0.0):
    """Generates a sphere of animated oscillating splats with time parameter t."""
    splats = []
    phi_golden = (1.0 + math.sqrt(5.0)) / 2.0 # Golden ratio

    # Pulsing scale and oscillating rotation
    pulse_r = radius * (1.0 + 0.25 * math.sin(t * 2.0 * math.pi))
    rot_angle = t * 2.0 * math.pi

    qw = math.cos(rot_angle * 0.5)
    qy = math.sin(rot_angle * 0.5)

    for i in range(num_splats):
        y = 1.0 - (i / float(num_splats - 1)) * 2.0
        rad_at_y = math.sqrt(max(0.0, 1.0 - y * y))
        theta = 2.0 * math.pi * i / phi_golden

        x = math.cos(theta) * rad_at_y * pulse_r
        z = math.sin(theta) * rad_at_y * pulse_r
        y_pos = y * pulse_r

        # Rotating colors around hue ring
        r = 0.5 + 0.5 * math.sin(theta + t * 4.0)
        g = 0.5 + 0.5 * math.sin(theta + t * 4.0 + 2.0)
        b = 0.5 + 0.5 * math.sin(theta + t * 4.0 + 4.0)

        splats.append({
            "pos": (x, y_pos, z),
            "color": (r, g, b),
            "scale": (0.04, 0.04, 0.04),
            "rot": (0.0, qy, 0.0, qw),
            "alpha": 0.95
        })

    return splats

def generate_4dgs_file(output_path, num_frames=60, gop_size=30, splat_count=300, fps=30.0):
    """Generates a complete .4dgs binary container file."""
    print(f"[*] Generating {num_frames} frames of synthetic 4DGS splats (splatCount={splat_count}, GOP={gop_size})...")

    chunks = []
    index_entries = []
    prev_splats = None

    for f in range(num_frames):
        t = f / float(fps)
        curr_splats = generate_sphere_splats(num_splats=splat_count, radius=1.0, t=t)

        is_keyframe = (f % gop_size) == 0 or prev_splats is None

        if is_keyframe:
            spz_bytes = make_spz_v4_keyframe(curr_splats)
            chunks.append(spz_bytes)
            index_entries.append({
                "compressedSize": len(spz_bytes),
                "uncompressedSize": len(spz_bytes),
                "splatCount": splat_count,
                "frameType": 0 # KeyframeSpz
            })
        else:
            delta_bytes, usize = make_delta_pframe(prev_splats, curr_splats, delta_scale=0.001)
            chunks.append(delta_bytes)
            index_entries.append({
                "compressedSize": len(delta_bytes),
                "uncompressedSize": usize,
                "splatCount": splat_count,
                "frameType": 1 # DeltaPFrame
            })

        prev_splats = curr_splats

    # Assemble .4dgs container
    header_size = 64
    table_size = num_frames * 24

    # Calculate offsets
    current_offset = header_size
    for i in range(num_frames):
        index_entries[i]["offset"] = current_offset
        current_offset += index_entries[i]["compressedSize"]

    index_table_offset = current_offset

    # Build Header (64 bytes)
    header_bytes = struct.pack("<IIIIBBHfIffffffQI",
        0x53474434,         # Magic: "4DGS"
        1,                  # Version: 1
        3,                  # Flags: DeltaEncoded | Loopable
        splat_count,        # maxSplatCount
        0,                  # shDegree: 0
        12,                 # fractionalBits: 12
        gop_size,           # gopSize
        fps,                # frameRate
        num_frames,         # totalFrames
        -1.8, -1.8, -1.8,   # boundsMin
        1.8, 1.8, 1.8,      # boundsMax
        index_table_offset, # frameIndexTableOffset
        0                   # reserved (4 bytes)
    )
    assert len(header_bytes) == 64

    # Build Index Table
    table_bytes = bytearray()
    for entry in index_entries:
        table_bytes.extend(struct.pack("<QIIII",
            entry["offset"],
            entry["compressedSize"],
            entry["uncompressedSize"],
            entry["splatCount"],
            entry["frameType"]
        ))
    assert len(table_bytes) == table_size

    # Write file
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    with open(output_path, "wb") as f:
        f.write(header_bytes)
        for chunk in chunks:
            f.write(chunk)
        f.write(table_bytes)

    total_bytes = os.path.getsize(output_path)
    print(f"[+] Successfully wrote .4dgs file: {output_path} ({total_bytes:,} bytes, {num_frames} frames)")
    return output_path

def main():
    out_file = "test_data/animated_sphere.4dgs"
    if len(sys.argv) > 1:
        out_file = sys.argv[1]

    generate_4dgs_file(out_file, num_frames=60, gop_size=30, splat_count=400, fps=30.0)

if __name__ == "__main__":
    main()
