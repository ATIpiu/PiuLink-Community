"""Integration regression for HTTPS close and mutual TLS session reuse.

Run against a local test Sunshine with an unpaired, throwaway client certificate:
  python https_idle_connection.py client-cert.pem client-key.pem
No pairing or streaming command is sent. The certificate must be unpaired so
the server sends its authentication-denied response immediately after TLS.
"""
import argparse
import json
import socket
import ssl
import time


def check(cert, key, port):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE  # Loopback-only test, never production.
    context.minimum_version = context.maximum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(cert, key)
    connections = []

    def connect(session=None):
        raw = socket.create_connection(('127.0.0.1', port), timeout=2)
        try:
            connection = context.wrap_socket(raw, server_hostname='localhost', session=session)
        except BaseException:
            raw.close()
            raise
        connections.append(connection)
        return connection

    try:
        first = connect()
        data = b''
        while b'\r\n\r\n' not in data:
            chunk = first.recv(1)
            if not chunk:
                raise AssertionError('Incomplete HTTP response headers')
            data += chunk
            if len(data) > 65536:
                raise AssertionError('Response headers too large')
        header, body = data.split(b'\r\n\r\n', 1)
        length = next(int(line.split(b':', 1)[1]) for line in header.split(b'\r\n')
                      if line.lower().startswith(b'content-length:'))
        while len(body) < length:
            chunk = first.recv(length - len(body))
            if not chunk:
                raise AssertionError('Truncated HTTP body')
            body += chunk
        # Do not read close_notify or close first: model an idle HTTP pool entry.
        for mode, session in [('fresh', None), ('resumed', first.session)]:
            started = time.monotonic()
            connection = connect(session)
            elapsed = round((time.monotonic() - started) * 1000)
            if mode == 'resumed' and not connection.session_reused:
                raise AssertionError('Server did not resume the TLS session')
            print(json.dumps({'mode': mode, 'handshake_ms': elapsed,
                              'session_reused': connection.session_reused}), flush=True)

        # GameStream responses request connection-close. The server must release the native
        # socket promptly instead of leaving it in CLOSE_WAIT until an object is collected.
        first.settimeout(2)
        if first.recv(1) != b'':
            raise AssertionError('Server did not close the completed HTTPS response')
    finally:
        for connection in connections:
            connection.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('certificate')
    parser.add_argument('key')
    parser.add_argument('--port', type=int, default=47984)
    parser.add_argument('--repeats', type=int, default=3)
    args = parser.parse_args()
    for _ in range(args.repeats):
        check(args.certificate, args.key, args.port)
