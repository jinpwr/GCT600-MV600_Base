"""Send a fake head pose to avatar_relay.py so HeadPoseReceiver can be tested without a Quest.

Connects as a publisher (same handshake as avatar_relay.py), then sends MAV1 packets:
"<4sId7f" header (magic, sequence, timestamp, px py pz qx qy qz qw) + 1 byte of filler.
The head moves on a horizontal circle (radius 0.5 m, height 1.6 m) in the receiver's
displayOrigin space. Python 3.10+, standard library only.
"""

import argparse
import json
import math
import socket
import struct
import sys
import time

POSE_HEADER = struct.Struct("<4sId7f")
FILLER = b"\x00"

HEIGHT = 1.6          # metres above displayOrigin (WebcamOrigin sits on the floor)
RADIUS = 0.5
CENTER_FORWARD = 1.5  # circle centre in front of displayOrigin (+Z), where FakeHead_A is
PERIOD = 4.0          # seconds per revolution


def write_frame(sock, payload):
    sock.sendall(struct.pack("!I", len(payload)) + payload)


def read_exactly(sock, count):
    data = b""
    while len(data) < count:
        chunk = sock.recv(count - len(data))
        if not chunk:
            raise ConnectionError("Relay closed the connection")
        data += chunk
    return data


def read_frame(sock, maximum=1024):
    size = struct.unpack("!I", read_exactly(sock, 4))[0]
    if not 0 < size <= maximum:
        raise ValueError(f"Invalid frame size: {size}")
    return read_exactly(sock, size)


def connect(host, port, channel):
    sock = socket.create_connection((host, port), timeout=5)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    hello = {"version": 1, "role": "publisher", "channel": channel}
    write_frame(sock, json.dumps(hello).encode("utf-8"))
    ack = json.loads(read_frame(sock))
    if not ack.get("ok"):
        sock.close()
        raise ConnectionError(f"Relay rejected connection: {ack.get('error', ack)}")
    return sock


def make_packet(sequence, timestamp, t):
    angle = 2 * math.pi * t / PERIOD
    x = RADIUS * math.cos(angle)
    z = CENTER_FORWARD + RADIUS * math.sin(angle)
    return POSE_HEADER.pack(b"MAV1", sequence & 0xFFFFFFFF, timestamp, x, HEIGHT, z, 0.0, 0.0, 0.0, 1.0) + FILLER


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--channel", default="quest-A-head")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5060)
    parser.add_argument("--rate", type=float, default=30, help="packets per second")
    args = parser.parse_args()
    if not 1 <= args.port <= 65535:
        parser.error("port must be between 1 and 65535")
    if not 0 < args.rate <= 120:
        parser.error("rate must be in (0, 120]")

    try:
        sock = connect(args.host, args.port, args.channel)
    except (OSError, ValueError) as exc:
        print(f"Could not connect to {args.host}:{args.port}: {exc}", file=sys.stderr)
        return 1

    print(f"Publishing fake head on channel '{args.channel}' at {args.rate:g} Hz. Ctrl+C to stop.")
    interval = 1.0 / args.rate
    start = time.monotonic()
    sequence = 0
    next_send = start
    try:
        with sock:
            while True:
                now = time.monotonic()
                write_frame(sock, make_packet(sequence, now, now - start))
                sequence += 1
                next_send += interval
                time.sleep(max(0.0, next_send - time.monotonic()))
    except KeyboardInterrupt:
        print("Stopped.")
    except OSError as exc:
        print(f"Connection lost: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
