"""Check that head packets arrive through avatar_relay.py. Python 3.10+, standard library only.

Connects as a subscriber and prints decoded MAV1 head packets (position + quaternion).
For checking only; it does not record anything.

    python head_probe.py --channel head-A
    python head_probe.py --host 192.168.0.10 --channel head-B --every 1
"""

import argparse
import json
import socket
import struct
import time

POSE_HEADER = struct.Struct("<4sId7f")  # magic, sequence, quest time, px py pz qx qy qz qw
HEAD_MARKER = 0x48  # 'H', the single payload byte HeadPoseSender appends


def read_exactly(sock, size):
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("Relay disconnected")
        data.extend(chunk)
    return bytes(data)


def read_frame(sock):
    size = struct.unpack("!I", read_exactly(sock, 4))[0]
    return read_exactly(sock, size)


def write_frame(sock, payload):
    sock.sendall(struct.pack("!I", len(payload)) + payload)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1", help="machine running avatar_relay.py")
    parser.add_argument("--port", type=int, default=5060)
    parser.add_argument("--channel", default="head-A")
    parser.add_argument("--every", type=int, default=15, help="print every Nth packet (30 Hz sender: 15 = twice a second)")
    args = parser.parse_args()

    with socket.create_connection((args.host, args.port), timeout=5) as sock:
        hello = {"version": 1, "role": "subscriber", "channel": args.channel}
        write_frame(sock, json.dumps(hello).encode())
        ack = read_frame(sock).decode()
        if not json.loads(ack).get("ok"):
            raise SystemExit(f"Relay rejected: {ack}")
        sock.settimeout(None)
        print(f"Subscribed to '{args.channel}' on {args.host}:{args.port}. Waiting for packets (Ctrl+C to stop)...")

        count = 0
        window_start = time.monotonic()
        window_count = 0
        rate = 0.0
        last_sequence = None
        while True:
            packet = read_frame(sock)
            magic, sequence, quest_time, *pose = POSE_HEADER.unpack_from(packet)
            count += 1
            window_count += 1
            now = time.monotonic()
            if now - window_start >= 1.0:
                rate = window_count / (now - window_start)
                window_start, window_count = now, 0

            gap = "" if last_sequence is None or sequence == last_sequence + 1 else f"  (skipped {sequence - last_sequence - 1})"
            last_sequence = sequence
            extra = packet[POSE_HEADER.size:]
            kind = "head" if len(packet) == POSE_HEADER.size + 1 and extra[0] == HEAD_MARKER else f"NOT a head packet ({len(extra)} payload bytes)"
            if count == 1 or count % max(1, args.every) == 0 or kind != "head":
                px, py, pz, qx, qy, qz, qw = pose
                print(f"#{sequence:<6} t={quest_time:9.3f}  pos=({px:+.3f}, {py:+.3f}, {pz:+.3f})  "
                      f"rot=({qx:+.3f}, {qy:+.3f}, {qz:+.3f}, {qw:+.3f})  {len(packet)}B {kind}  {rate:4.1f} Hz{gap}")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
