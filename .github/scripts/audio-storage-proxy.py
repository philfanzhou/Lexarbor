"""Isolated test proxy: preserve signed Host, strip /s3/, never log URLs."""
import http.client
import http.server
import pathlib
import sys

upstream_port = int(sys.argv[1])

class Proxy(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if not self.path.startswith('/s3/'):
            self.send_error(404)
            return
        connection = http.client.HTTPConnection('127.0.0.1', upstream_port, timeout=10)
        try:
            connection.request('GET', self.path[3:], headers={
                key: value for key, value in self.headers.items()
                if key.lower() not in ('connection', 'accept-encoding')
            })
            response = connection.getresponse()
            data = response.read()
            self.send_response(response.status)
            for key, value in response.getheaders():
                if key.lower() not in ('transfer-encoding', 'connection', 'content-length'):
                    self.send_header(key, value)
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)
        finally:
            connection.close()

    def log_message(self, *_args):
        pass

server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Proxy)
pathlib.Path(sys.argv[2]).write_text(str(server.server_port))
server.serve_forever()
