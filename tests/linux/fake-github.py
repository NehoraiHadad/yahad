#!/usr/bin/env python3
"""Stand-in for the two GitHub hosts the Linux updater talks to, for acceptance only.

The updater has no source seam by design: its repository, hosts and asset URL
shape are constants, and it verifies TLS. update-acceptance.py therefore points
these names at loopback in /etc/hosts and trusts a throwaway CA, so the unmodified
updater runs. This server answers the way the real API does: /releases/latest is
JSON, an asset URL returns metadata unless octet-stream was asked for, and bytes
come from a redirect to the assets host. Nothing else is served.
Which release is "latest" is read from --state on every request.
"""
import argparse
import http.server
import json
import pathlib
import re
import ssl
import threading
import time

API_HOST = 'api.github.com'
ASSETS_HOST = 'release-assets.githubusercontent.com'
REPOSITORY = 'NehoraiHadad/yahad'
LATEST = f'/repos/{REPOSITORY}/releases/latest'
ASSET = re.compile(rf'/repos/{re.escape(REPOSITORY)}/releases/assets/([0-9]+)')
DOWNLOAD = re.compile(r'/download/([0-9]+)')


def handler(state_path, log_path):
    log_lock = threading.Lock()

    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'
        timeout = 60

        def state(self):
            return json.loads(pathlib.Path(state_path).read_bytes())

        def respond(self, status, body=b'', content_type='application/json', headers=()):
            self.record(status)
            self.send_response(status)
            self.send_header('Content-Type', content_type)
            self.send_header('Content-Length', str(len(body)))
            self.send_header('Connection', 'close')
            for name, value in headers:
                self.send_header(name, value)
            self.end_headers()
            self.wfile.write(body)
            self.close_connection = True

        def record(self, status):
            entry = dict(at=round(time.time(), 3), host=self.headers.get('Host'), path=self.path, status=status,
                         userAgent=self.headers.get('User-Agent'), accept=self.headers.get('Accept'))
            with log_lock, open(log_path, 'a', encoding='utf8') as log:
                log.write(json.dumps(entry) + '\n')

        def do_GET(self):
            host = (self.headers.get('Host') or '').split(':')[0]
            state = self.state()
            assets = {str(item['id']): item for item in state['release']['assets']}
            if host == API_HOST and self.path == LATEST:
                return self.respond(200, json.dumps(state['release']).encode())
            match = ASSET.fullmatch(self.path)
            if host == API_HOST and match and match[1] in assets:
                if 'application/octet-stream' not in (self.headers.get('Accept') or ''):
                    return self.respond(200, json.dumps(assets[match[1]]).encode())
                location = f'https://{ASSETS_HOST}/download/{match[1]}'
                return self.respond(302, headers=[('Location', location)])
            match = DOWNLOAD.fullmatch(self.path)
            if host == ASSETS_HOST and match and match[1] in assets:
                body = pathlib.Path(state['files'][match[1]]).read_bytes()
                return self.respond(200, body, 'application/octet-stream')
            return self.respond(404, b'{"message":"Not Found"}')

        def log_message(self, *_):
            pass

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--state', required=True)
    parser.add_argument('--cert', required=True)
    parser.add_argument('--key', required=True)
    parser.add_argument('--log', required=True)
    parser.add_argument('--bind', default='127.0.0.1')
    parser.add_argument('--port', type=int, default=443)
    args = parser.parse_args()
    server = http.server.ThreadingHTTPServer((args.bind, args.port), handler(args.state, args.log))
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(args.cert, args.key)
    # Handshake in the request thread, so one stalled client cannot block accept.
    server.socket = context.wrap_socket(server.socket, server_side=True, do_handshake_on_connect=False)
    server.daemon_threads = True
    server.serve_forever()


if __name__ == '__main__':
    main()
