#!/usr/bin/env bash
# Publishes the Linux app and builds agenticsdr_${VERSION}_amd64.deb (default 0.1.0).
# On the target machine, apt asks for the OpenAI endpoint, model, and key.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
version="${VERSION:-0.1.0}"
deb_name="agenticsdr_${version}_amd64.deb"
stage="$root/packaging/stage/linux-x64"
pkg="$root/packaging/stage/deb"
rm -rf "$pkg"
mkdir -p "$stage" "$root/packaging/out" \
  "$pkg/DEBIAN" \
  "$pkg/usr/lib/agenticsdr" \
  "$pkg/usr/bin" \
  "$pkg/usr/share/applications"
dotnet publish "$root/Host/AgenticSdr.Host.csproj" -c Release -r linux-x64 --self-contained true -o "$stage"
cp -a "$stage"/. "$pkg/usr/lib/agenticsdr/"
# Maintainer scripts must be LF. A Windows checkout stores them as CRLF, which breaks the shebang.
copy_unix() {
  local mode="$1" src="$2" dest="$3"
  sed 's/\r$//' "$src" > "$dest"
  chmod "$mode" "$dest"
}
copy_unix 0755 "$root/packaging/linux/agenticsdr.sh" "$pkg/usr/bin/agenticsdr"
copy_unix 0644 "$root/packaging/linux/agenticsdr.desktop" "$pkg/usr/share/applications/agenticsdr.desktop"
copy_unix 0644 "$root/packaging/linux/debian/control" "$pkg/DEBIAN/control"
# Keep the control Version: line aligned with the archive name when VERSION is overridden.
sed -i.bak -E "s/^Version:.*/Version: ${version}/" "$pkg/DEBIAN/control" && rm -f "$pkg/DEBIAN/control.bak"
copy_unix 0755 "$root/packaging/linux/debian/config" "$pkg/DEBIAN/config"
copy_unix 0755 "$root/packaging/linux/debian/postinst" "$pkg/DEBIAN/postinst"
copy_unix 0755 "$root/packaging/linux/debian/postrm" "$pkg/DEBIAN/postrm"
copy_unix 0644 "$root/packaging/linux/debian/templates" "$pkg/DEBIAN/templates"
if command -v dpkg-deb >/dev/null 2>&1; then
  dpkg-deb --root-owner-group --build "$pkg" "$root/packaging/out/$deb_name"
elif command -v wsl >/dev/null 2>&1; then
  to_wsl() {
    local win drive rest
    win="$(cygpath -w "$1")"
    drive="${win:0:1}"
    drive="${drive,,}"
    rest="${win:3}"
    rest="${rest//\\//}"
    printf '/mnt/%s/%s' "$drive" "$rest"
  }
  # A Windows drive reports directories as mode 777, which dpkg-deb rejects.
  # Copy onto the Linux filesystem, set package modes, and build there.
  MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' \
    wsl -e bash -s -- "$(to_wsl "$pkg")" "$(to_wsl "$root/packaging/out/$deb_name")" <<'EOF'
set -euo pipefail
src="$1"
dest="$2"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cp -a "$src" "$tmp/deb"
find "$tmp/deb" -type d -exec chmod 0755 {} +
find "$tmp/deb" -type f -exec chmod 0644 {} +
chmod 0755 "$tmp/deb/DEBIAN/config" "$tmp/deb/DEBIAN/postinst" "$tmp/deb/DEBIAN/postrm"
chmod 0755 "$tmp/deb/usr/bin/agenticsdr" "$tmp/deb/usr/lib/agenticsdr/AgenticSdr.Host"
if [ -e "$tmp/deb/usr/lib/agenticsdr/createdump" ]; then
  chmod 0755 "$tmp/deb/usr/lib/agenticsdr/createdump"
fi
dpkg-deb --root-owner-group --build "$tmp/deb" "$dest"
EOF
else
  echo "dpkg-deb was not found. The package tree is in $pkg. Install dpkg-dev and run: dpkg-deb --root-owner-group --build packaging/stage/deb packaging/out/$deb_name" >&2
  exit 1
fi
echo "Wrote $root/packaging/out/$deb_name"
