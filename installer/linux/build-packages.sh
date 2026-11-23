#!/bin/bash
# Linux installers from the PyInstaller folder qt/dist/kadr:
#   Kadr-Linux-amd64.deb  (Ubuntu, Debian, Mint — double-click opens the software installer)
#   Kadr-Linux-x86_64.rpm (Fedora, openSUSE)
# Needs fpm (gem install fpm) and rpm. Usage: installer/linux/build-packages.sh 1.0.0
set -euo pipefail
VER="${1:-1.0.0}"
cd "$(dirname "$0")/../../qt"

rm -rf pkgroot && mkdir -p pkgroot/opt pkgroot/usr/bin pkgroot/usr/share/applications
cp -r dist/kadr pkgroot/opt/kadr
ln -s /opt/kadr/kadr pkgroot/usr/bin/kadr
for s in 16 24 32 48 64 128 256 512; do
  d="pkgroot/usr/share/icons/hicolor/${s}x${s}/apps"
  mkdir -p "$d"
  cp "../icons/kadr-$s.png" "$d/kadr.png"
done

cat > pkgroot/usr/share/applications/kadr.desktop <<'EOF'
[Desktop Entry]
Type=Application
Name=Kadr
Name[ru]=Кадр
Name[uk]=Кадр
Comment=Screenshots with drawing and upload
Comment[ru]=Снимки экрана с пометками и загрузкой
Comment[uk]=Знімки екрана з позначками та завантаженням
Exec=kadr %F
Icon=kadr
Terminal=false
Categories=Graphics;Utility;
Keywords=screenshot;capture;upload;
MimeType=image/png;image/jpeg;image/gif;image/webp;image/bmp;
StartupNotify=false
EOF

cat > after.sh <<'EOF'
#!/bin/sh
update-desktop-database -q /usr/share/applications 2>/dev/null || true
gtk-update-icon-cache -q -f -t /usr/share/icons/hicolor 2>/dev/null || true
exit 0
EOF
chmod +x after.sh

COMMON=(-s dir -n kadr -v "$VER" --description "Kadr: screenshots with drawing tools and upload to Catbox, Imgur or your own service"
        --url "https://github.com/marseillered-design/kadr" --maintainer "Kadr" --vendor "Kadr"
        --after-install after.sh --after-remove after.sh -C pkgroot)
fpm "${COMMON[@]}" -t deb -a amd64 --deb-no-default-config-files -p "Kadr-Linux-amd64.deb" .
fpm "${COMMON[@]}" -t rpm -a x86_64 --rpm-os linux -p "Kadr-Linux-x86_64.rpm" .
ls -la Kadr-Linux-*
