"""A5 — can the poison detector in tests/proxy-poison.py fire at all?

Not a smuggling demonstration. A calibration, and the reason tests/proxy.sh
is allowed to report "clean" as a measurement rather than as an absence.

The back end below answers the FIRST request on a connection with TWO
responses - exactly the state a successful desync leaves behind: one extra
response sitting in the pooled upstream connection, waiting to be handed to
whoever asks next. Each of the five proxies is put in front of it in turn and
asked innocent questions.

If a follow-up comes back with somebody else's answer, the detector works. If
every proxy notices the extra response and drops the connection, the detector
is never exercised - and that is the thing worth knowing, because it means
the proxies are doing the protecting and a clean poison column says little
about the origin behind them.

Measured 2026-09-27: all five absorbed it. See
docs/TestingAgainst_Proxies.md.

Run it deliberately, inside the WSL VM on Windows, where Docker is:

    wsl -d Debian -- python3 /mnt/d/.../tests/proxy-calibrate.py

It is not part of tests/proxy.sh. Its job was to answer this question once,
and it is kept so that the answer can be checked rather than believed.
"""

import os, socket, subprocess, threading, time

BACK_PORT = 18097

ONE   = b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Type: text/plain\r\n\r\nplain"
EXTRA = b"HTTP/1.1 418 I'm a teapot\r\nContent-Length: 6\r\nContent-Type: text/plain\r\n\r\nstolen"


def serve(conn):
    conn.settimeout(10)
    served, buf = 0, b""
    try:
        while True:
            try:
                d = conn.recv(65536)
            except (socket.timeout, OSError):
                break
            if not d:
                break
            buf += d
            while b"\r\n\r\n" in buf:
                _, buf = buf.split(b"\r\n\r\n", 1)
                conn.sendall(ONE + EXTRA if served == 0 else ONE)
                served += 1
    finally:
        try:
            conn.close()
        except OSError:
            pass


def back_end(stop):
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", BACK_PORT))
    srv.listen(32)
    srv.settimeout(0.5)
    while not stop.is_set():
        try:
            conn, _ = srv.accept()
        except socket.timeout:
            continue
        threading.Thread(target=serve, args=(conn,), daemon=True).start()
    srv.close()


def get(port, path="/plain", window=2.0):
    try:
        s = socket.create_connection(("127.0.0.1", port), timeout=4)
    except OSError:
        return "conn"
    s.sendall(("GET %s HTTP/1.1\r\nHost: 127.0.0.1:%d\r\n\r\n" % (path, port)).encode())
    out, deadline = b"", time.time() + window
    while time.time() < deadline:
        try:
            s.settimeout(max(0.05, deadline - time.time()))
            d = s.recv(65536)
            if not d:
                break
            out += d
        except (socket.timeout, OSError):
            break
    s.close()
    i = out.find(b"HTTP/1.")
    return out[i+9:i+12].decode("latin-1", "replace") if i >= 0 else "none"


CASES = [

    ("haproxy", 18198, "haproxy:3.4-alpine",
     "/usr/local/etc/haproxy/haproxy.cfg",
     """global
    log stdout format raw local0 warning
defaults
    mode http
    timeout connect 5s
    timeout client 20s
    timeout server 20s
    option http-keep-alive
frontend fe
    bind :%d
    default_backend be
backend be
    server back 127.0.0.1:%d
""", None),

    ("nginx", 18197, "nginx:1.31-alpine",
     "/etc/nginx/conf.d/default.conf",
     """server {
    listen %d;
    location / {
        proxy_pass http://127.0.0.1:%d;
        proxy_http_version 1.1;
        proxy_set_header Connection "";
        proxy_buffering off;
    }
}
""", None),

    ("caddy", 18196, "caddy:2-alpine",
     "/etc/caddy/Caddyfile",
     """{
	admin off
	auto_https off
}
:%d {
	log {
		output discard
	}
	reverse_proxy 127.0.0.1:%d
}
""", None),

    ("httpd", 18195, "httpd:2.4-alpine",
     "/usr/local/apache2/conf/httpd.conf",
     """ServerName proxy
Listen %d
LoadModule mpm_event_module modules/mod_mpm_event.so
LoadModule authz_core_module modules/mod_authz_core.so
LoadModule unixd_module modules/mod_unixd.so
LoadModule log_config_module modules/mod_log_config.so
LoadModule proxy_module modules/mod_proxy.so
LoadModule proxy_http_module modules/mod_proxy_http.so
User daemon
Group daemon
ErrorLog /proc/self/fd/2
LogLevel crit
ProxyRequests Off
ProxyPass / http://127.0.0.1:%d/
""", None),

    ("envoy", 18194, "envoyproxy/envoy:v1.39-latest",
     "/etc/envoy/envoy.yaml",
     """admin:
  address:
    socket_address: {address: 127.0.0.1, port_value: 9902}
static_resources:
  listeners:
  - name: l
    address:
      socket_address: {address: 0.0.0.0, port_value: %d}
    filter_chains:
    - filters:
      - name: envoy.filters.network.http_connection_manager
        typed_config:
          "@type": type.googleapis.com/envoy.extensions.filters.network.http_connection_manager.v3.HttpConnectionManager
          stat_prefix: i
          codec_type: HTTP1
          route_config:
            name: r
            virtual_hosts:
            - name: a
              domains: ["*"]
              routes:
              - match: {prefix: "/"}
                route: {cluster: back, timeout: 20s}
          http_filters:
          - name: envoy.filters.http.router
            typed_config:
              "@type": type.googleapis.com/envoy.extensions.filters.http.router.v3.Router
  clusters:
  - name: back
    type: STATIC
    connect_timeout: 5s
    load_assignment:
      cluster_name: back
      endpoints:
      - lb_endpoints:
        - endpoint:
            address:
              socket_address: {address: 127.0.0.1, port_value: %d}
""", ["-c", "/etc/envoy/envoy.yaml", "--log-level", "off"]),
]


stop = threading.Event()
threading.Thread(target=back_end, args=(stop,), daemon=True).start()
time.sleep(0.5)

print()
print("back end alone: first=%s then=%s   (the extra response is real)"
      % (get(BACK_PORT), get(BACK_PORT)))
print()
print("%-9s %-8s %-30s %s" % ("proxy", "first", "six follow-ups", "verdict"))

for name, port, image, cfg_path, cfg, cmd in CASES:

    path = "/tmp/detector-%s.cfg" % name
    open(path, "w").write(cfg % (port, BACK_PORT))

    subprocess.run(["docker", "rm", "-f", "detector-" + name], capture_output=True)
    run = ["docker", "run", "-d", "--rm", "--name", "detector-" + name,
           "--network", "host", "-v", "%s:%s:ro" % (path, cfg_path), image]
    if cmd:
        run += cmd
    subprocess.run(run, capture_output=True)

    ready = False
    for _ in range(40):
        if get(port, window=1.0) not in ("conn", "none"):
            ready = True
            break
        time.sleep(0.5)

    if not ready:
        print("%-9s %-8s %-30s %s" % (name, "-", "-", "did not come up"))
        subprocess.run(["docker", "rm", "-f", "detector-" + name], capture_output=True)
        continue

    # A fresh connection pair: the poisoning one, then the follow-ups.
    first     = get(port)
    followups = [get(port, window=1.5) for _ in range(6)]
    poisoned  = [f for f in followups if f != "200"]

    print("%-9s %-8s %-30s %s" % (
          name, first, ",".join(followups),
          "DETECTOR FIRES" if poisoned else "clean - the proxy absorbed it"))

    subprocess.run(["docker", "rm", "-f", "detector-" + name], capture_output=True)

print()
stop.set()
time.sleep(0.8)
