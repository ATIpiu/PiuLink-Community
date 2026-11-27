"""Exercise the Rust bridge with real sockets on three distinct loopback addresses."""
import os
from pathlib import Path
import socket
import subprocess
import sys
import time

binary = Path(sys.argv[1]).resolve(strict=True)
front, host, client_ip = "127.0.0.2", "127.0.0.3", "127.0.0.1"
base = 50789
bridge = subprocess.Popen([binary, front, host, client_ip, str(base)], stdin=subprocess.PIPE,
                          stderr=subprocess.PIPE, env=dict(os.environ, PIULINK_PARENT_PIPE="1"))
try:
    assert b"ready" in bridge.stderr.readline()
    for p in (base - 5, base, base + 21):
        with socket.socket() as server, socket.socket() as client:
            server.bind((host, p))
            server.listen()
            server.settimeout(3)
            client.bind((client_ip, 0))
            client.settimeout(3)
            client.connect((front, p))
            client.sendall(b"request")
            client.shutdown(socket.SHUT_WR)
            incoming, _ = server.accept()
            with incoming:
                with incoming.makefile("rb") as stream:
                    assert stream.read() == b"request"
                incoming.sendall(b"response")
            with client.makefile("rb") as stream:
                assert stream.read() == b"response"
    for p in (base + 9, base + 10, base + 11):
        with socket.socket(type=socket.SOCK_DGRAM) as server:
            server.bind((host, p))
            server.settimeout(3)
            for attempt in range(2):
                with socket.socket(type=socket.SOCK_DGRAM) as client:
                    client.bind((client_ip, 0))
                    client.settimeout(3)
                    data = os.urandom(1200)
                    if attempt:
                        # A new port acquires the mapping only after the previous lease expires.
                        client.sendto(b"new mapping", (front, p))
                        server.settimeout(.25)
                        try:
                            server.recvfrom(2048)
                        except socket.timeout:
                            pass
                        else:
                            raise AssertionError("Active UDP endpoint changed")
                        time.sleep(12)
                        server.settimeout(3)
                    client.sendto(data, (front, p))
                    payload, peer = server.recvfrom(2048)
                    assert data == payload
                    server.sendto(payload, peer)
                    assert client.recv(2048) == data
                # A delayed packet can elicit ICMP after the client closes.
                server.sendto(data, peer)
                time.sleep(.05)
            with socket.socket(type=socket.SOCK_DGRAM) as other:
                other.bind(("127.0.0.4", 0))
                other.sendto(b"wrong client", (front, p))
                server.settimeout(.15)
                try:
                    server.recvfrom(2048)
                except socket.timeout:
                    pass
                else:
                    raise AssertionError("An unconfigured client reached the host")
    print("All three TCP paths passed half-close tests; all three UDP paths passed round trips, reconnects and source filtering.")
finally:
    bridge.stdin.close()
    assert bridge.wait(timeout=5) == 0
