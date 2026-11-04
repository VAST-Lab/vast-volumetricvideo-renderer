#!/usr/bin/env python3
"""
tools/stream_ply_sequence.py
Standalone WebSocket server for streaming 4DGS / 3DGS frames to Unity WebGPU and Desktop clients.
Uses only Python 3 standard library (no pip dependencies required).
"""

import sys
import os
import time
import struct
import socket
import hashlib
import base64
import threading

MAGIC_SGST = 0x54534753  # "SGST" Little-endian (bytes 'S', 'G', 'S', 'T')
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

class WebSocketClient:
    def __init__(self, conn, addr):
        self.conn = conn
        self.addr = addr
        self.handshake_done = False

    def send_binary(self, data):
        length = len(data)
        header = bytearray([0x82])  # FIN + binary opcode

        if length < 126:
            header.append(length)
        elif length < 65536:
            header.append(126)
            header.extend(struct.pack(">H", length))
        else:
            header.append(127)
            header.extend(struct.pack(">Q", length))

        try:
            self.conn.sendall(header + data)
            return True
        except Exception:
            return False

def make_stream_packet(frame_idx, timestamp, payload_type, payload_bytes):
    header = struct.pack("<IIII", MAGIC_SGST, frame_idx, int(timestamp * 1000) & 0xFFFFFFFF, payload_type)
    return header + payload_bytes

def handle_client(conn, addr, clients, lock):
    client = WebSocketClient(conn, addr)
    try:
        req = conn.recv(4096).decode('utf-8', errors='ignore')
        key = None
        for line in req.split("\r\n"):
            if line.lower().startswith("sec-websocket-key:"):
                key = line.split(":", 1)[1].strip()
                break

        if not key:
            conn.close()
            return

        accept_val = base64.b64encode(hashlib.sha1((key + WS_GUID).encode('utf-8')).digest()).decode('utf-8')
        response = (
            "HTTP/1.1 101 Switching Protocols\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Sec-WebSocket-Accept: {accept_val}\r\n\r\n"
        )
        conn.sendall(response.encode('utf-8'))
        client.handshake_done = True

        with lock:
            clients.append(client)
        print(f"[+] Client connected from {addr}. Total clients: {len(clients)}")

        # Keep connection open until disconnect
        while True:
            data = conn.recv(1024)
            if not data:
                break
    except Exception:
        pass
    finally:
        with lock:
            if client in clients:
                clients.remove(client)
        try:
            conn.close()
        except Exception:
            pass
        print(f"[-] Client disconnected {addr}. Remaining: {len(clients)}")

def broadcast_loop(frames, fps, clients, lock):
    frame_idx = 0
    interval = 1.0 / fps
    print(f"[*] Broadcast loop active at {fps} FPS with {len(frames)} loaded frames.")

    while True:
        start_time = time.time()
        payload = frames[frame_idx % len(frames)]
        packet = make_stream_packet(frame_idx, frame_idx * interval, 0, payload)

        with lock:
            dead_clients = []
            for client in clients:
                if not client.send_binary(packet):
                    dead_clients.append(client)
            for dead in dead_clients:
                clients.remove(dead)

        frame_idx += 1
        elapsed = time.time() - start_time
        sleep_time = interval - elapsed
        if sleep_time > 0:
            time.sleep(sleep_time)

def main():
    port = 8080
    fps = 30.0
    source_path = ""

    if len(sys.argv) > 1:
        source_path = sys.argv[1]
    if len(sys.argv) > 2:
        port = int(sys.argv[2])
    if len(sys.argv) > 3:
        fps = float(sys.argv[3])

    frames = []

    if source_path and os.path.isfile(source_path):
        print(f"[*] Reading .4dgs file: {source_path}")
        with open(source_path, "rb") as f:
            header_bytes = f.read(64)
            magic, ver, flags, max_splats, sh_deg, frac_bits, gop, rate, total_frames, bx, by, bz, Bx, By, Bz, idx_offset, _ = struct.unpack("<IIIIBBHfIffffffQI", header_bytes)
            f.seek(idx_offset)
            table = []
            for _ in range(total_frames):
                offset, csize, usize, splats, ftype = struct.unpack("<QIIII", f.read(24))
                table.append((offset, csize, ftype))
            for offset, csize, _ in table:
                f.seek(offset)
                frames.append(f.read(csize))
        print(f"[+] Loaded {len(frames)} chunks from .4dgs container.")

    elif source_path and os.path.isdir(source_path):
        files = sorted([os.path.join(source_path, f) for f in os.listdir(source_path) if f.endswith(".spz") or f.endswith(".ply")])
        for f in files:
            with open(f, "rb") as fp:
                frames.append(fp.read())
        print(f"[+] Loaded {len(frames)} files from sequence folder.")

    if not frames:
        print("[!] No source files loaded. Generating 1 dummy keyframe packet for testing.")
        # Minimal dummy payload: 64 bytes
        frames.append(b"\x00" * 64)

    clients = []
    lock = threading.Lock()

    broadcaster = threading.Thread(target=broadcast_loop, args=(frames, fps, clients, lock), daemon=True)
    broadcaster.start()

    server_sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server_sock.bind(("0.0.0.0", port))
    server_sock.listen(10)
    print(f"[*] 4DGS Stream Server listening on ws://0.0.0.0:{port}")

    try:
        while True:
            conn, addr = server_sock.accept()
            t = threading.Thread(target=handle_client, args=(conn, addr, clients, lock), daemon=True)
            t.start()
    except KeyboardInterrupt:
        print("\n[*] Server stopped.")
    finally:
        server_sock.close()

if __name__ == "__main__":
    main()
