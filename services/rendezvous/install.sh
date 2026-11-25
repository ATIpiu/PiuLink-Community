#!/bin/sh
# Run alongside the linux/amd64 server binary, routes.caddy and systemd unit.
set -eu
cd "$(dirname "$0")"
public_url=${PIULINK_PUBLIC_URL:?Set PIULINK_PUBLIC_URL to the public HTTPS origin, for example https://connect.example.org}
endpoint=$(python3 - "$public_url" <<'PY'
from urllib.parse import urlsplit
import re, sys
u = urlsplit(sys.argv[1])
if (u.scheme != 'https' or not u.hostname or u.path not in ('', '/') or u.query or u.fragment
        or not re.fullmatch(r'[A-Za-z0-9.-]+(?::[0-9]{1,5})?', u.netloc) or not 1 <= (u.port or 443) <= 65535):
    sys.exit('Expected an HTTPS hostname with optional port and no path.')
print(f'{u.hostname}:{u.port or 443}')
PY
)
public_url=${public_url%/}
dest=/usr/local/lib/piulink-rendezvous
unit=piulink-rendezvous.service
config=/etc/caddy/Caddyfile
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
import sys
config = Path(sys.argv[1])
site = sys.argv[3] + ' {\n'
include = '    import ' + sys.argv[2] + '\n'
text = config.read_text()
if text.count(site) > 1: raise SystemExit('Duplicate Caddy site blocks')
if site + include not in text:
    config.write_text(text.replace(site, site + include) if site in text else text.rstrip() + '\n\n' + site + include + '}\n')
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
