#!/bin/sh
# Run alongside the linux/amd64 server binary, routes.caddy and systemd unit.
set -eu
cd "$(dirname "$0")"
config=/etc/caddy/Caddyfile
public_url=${PIULINK_PUBLIC_URL:-}
if [ -z "$public_url" ] && grep -Fqx 'https://atipiu.org:8443 {' "$config"; then
    public_url=https://atipiu.org:8443
fi
: "${public_url:?Set PIULINK_PUBLIC_URL to the public HTTPS origin, for example https://connect.example.org}"
endpoint=$(python3 - "$public_url" <<'PY'
from urllib.parse import urlsplit
import re, sys
u = urlsplit(sys.argv[1])
port = 443 if u.port is None else u.port
if (u.scheme != 'https' or not u.hostname or u.path not in ('', '/') or u.query or u.fragment
        or not re.fullmatch(r'[A-Za-z0-9.-]+(?::[0-9]{1,5})?', u.netloc) or not 1 <= port <= 65535):
    sys.exit('Expected an HTTPS hostname with optional port and no path.')
print(f'{u.hostname}:{port}')
PY
)
public_url=${public_url%/}
dest=/usr/local/lib/piulink-rendezvous
unit=piulink-rendezvous.service
backup=$(mktemp -d /var/backups/piulink-rendezvous.XXXXXXXX)
cp -p "$config" "$backup/Caddyfile"
for file in "$dest/server" "$dest/routes.caddy" "/etc/systemd/system/$unit"; do
    if [ -e "$file" ]; then cp -p "$file" "$backup/"; fi
done
restore() {
    cp -p "$backup/Caddyfile" "$config"
    systemctl stop "$unit" || true
    for file in "$dest/server" "$dest/routes.caddy" "/etc/systemd/system/$unit"; do
        old="$backup/$(basename "$file")"
        if [ -f "$old" ]; then cp -p "$old" "$file"; else rm -f "$file"; fi
    done
    systemctl daemon-reload
    if [ -f "$backup/server" ]; then systemctl start "$unit"; fi
    systemctl reload caddy
}
trap 'restore' EXIT
install -d -m 755 "$dest"
install -m 755 server "$dest/server.new"
mv "$dest/server.new" "$dest/server"
install -m 644 routes.caddy "$dest/routes.caddy"
install -m 644 "$unit" "/etc/systemd/system/$unit"
python3 - "$config" "$dest/routes.caddy" "$public_url" <<'PY'
from pathlib import Path
from urllib.parse import urlsplit
import re, sys
config = Path(sys.argv[1])
url = urlsplit(sys.argv[3])
names = {url.netloc, 'https://' + url.netloc}
if url.port in (None, 443):
    names.update((url.hostname, url.hostname + ':443', 'https://' + url.hostname, 'https://' + url.hostname + ':443'))
include = '    import ' + sys.argv[2] + '\n'
text = config.read_text()
pattern = r'(?m)^[ \t]*(?:' + '|'.join(map(re.escape, names)) + r')[ \t]*\{[ \t]*(?:#.*)?\n'
matches = list(re.finditer(pattern, text, re.I))
if len(matches) > 1: raise SystemExit('Duplicate Caddy site blocks')
if matches:
    pos = matches[0].end()
    if not text[pos:].startswith(include):
        config.write_text(text[:pos] + include + text[pos:])
else:
    config.write_text(text.rstrip() + '\n\n' + sys.argv[3] + ' {\n' + include + '}\n')
PY
caddy validate --config "$config" >/dev/null
systemctl daemon-reload
systemctl restart "$unit"
curl -fsS --retry 5 --retry-connrefused http://127.0.0.1:5086/piulink/join >/dev/null
systemctl reload caddy
curl -fsS --retry 5 --retry-all-errors --resolve "$endpoint:127.0.0.1" "$public_url/piulink/join" >/dev/null
systemctl enable "$unit"
trap - EXIT
printf 'Installed. Previous files: %s\n' "$backup"
