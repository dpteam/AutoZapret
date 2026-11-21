#!/bin/bash
# AutoZapret для Linux: установка и обновление с GitHub.
#
#   sudo ./install.sh                  поставить последнюю версию
#   sudo ./install.sh --version 0.3.0  конкретную версию
#   sudo ./install.sh --remove         удалить
#   sudo ./install.sh --status         что установлено
#
# Что делает:
#   * скачивает архив с GitHub Releases;
#   * кладёт в /opt/autozapret (движок и списки внутри, ничего докачивать не надо);
#   * создаёт службу systemd с автозапуском;
#   * настройки и свои стратегии при обновлении НЕ трогаются.
#
# Без root не работает: nfqws открывает сокет к ядру и меняет правила nftables.
set -eu

REPO="dpteam/AutoZapret"
PREFIX=/opt/autozapret
UNIT=/etc/systemd/system/autozapret.service
SERVICE=autozapret

ASSET="autozapret-linux-x64.tar.gz"
HERE="$(cd "$(dirname "$0")" && pwd)"

say()  { echo "  $*"; }
head1() { echo ""; echo "== $* =="; }
die()  { echo ""; echo "[ОШИБКА] $*"; exit 1; }

# ---------------------------------------------------------------- аргументы

VERSION=""
ACTION="install"

while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="${2:-}"; shift 2 ;;
        --remove|--uninstall) ACTION="remove"; shift ;;
        --status) ACTION="status"; shift ;;
        --help|-h)
            sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) die "неизвестный аргумент: $1 (см. --help)" ;;
    esac
done

# ---------------------------------------------------------------- статус

if [ "$ACTION" = "status" ]; then
    echo ""
    if [ ! -x "$PREFIX/AutoZapret" ]; then
        echo "  AutoZapret не установлен ($PREFIX пуст)"
        exit 1
    fi

    "$PREFIX/AutoZapret" --status || true
    echo ""
    if systemctl is-enabled --quiet "$SERVICE" 2>/dev/null; then
        echo "  автозапуск: включён"
    else
        echo "  автозапуск: выключен"
    fi
    exit 0
fi

# ---------------------------------------------------------------- удаление

if [ "$ACTION" = "remove" ]; then
    [ "$(id -u)" -eq 0 ] || die "нужен root: sudo $0 --remove"

    head1 "удаление"

    systemctl disable --now "$SERVICE" 2>/dev/null || true
    say "служба остановлена и убрана из автозапуска"

    rm -f "$UNIT"
    systemctl daemon-reload
    say "unit-файл удалён"

    rm -rf "$PREFIX"
    say "файлы удалены"

    echo ""
    echo "Готово. Настройки оставлены в $PREFIX/data — их удалили вместе с папкой."
    echo "Если настраивали systemd-юниты руками — проверьте: systemctl list-units | grep zapret"
    exit 0
fi

# ---------------------------------------------------------------- установка

[ "$(id -u)" -eq 0 ] || die "нужен root: sudo $0"

head1 "проверка системы"

for tool in curl tar; do
    command -v "$tool" >/dev/null 2>&1 || die "нет $tool — установите: apt install $tool"
done
say "curl, tar: есть"

ARCH="$(uname -m)"
if [ "$ARCH" != "x86_64" ] && [ "$ARCH" != "amd64" ]; then
    die "поддерживается только x86_64, у вас $ARCH. Для arm64 соберите сами: dotnet publish linux/AutoZapret.Linux.csproj -r linux-arm64"
fi
say "архитектура: $ARCH"

# nftables или iptables: без них пакеты некуда направлять
if command -v nft >/dev/null 2>&1; then
    say "firewall: nftables ($(nft --version 2>/dev/null | head -1))"
elif command -v iptables >/dev/null 2>&1; then
    say "firewall: iptables"
else
    die "нет ни nftables, ни iptables — установите: apt install nftables"
fi

# ---------------------------------------------------------------- версия

head1 "версия"

if [ -z "$VERSION" ]; then
    # Редирект релиза знает последнюю версию без GitHub API (у него лимит 60/час).
    TAG="$(curl -sI "https://github.com/$REPO/releases/latest" \
           | grep -i '^location:' | sed 's|.*/tag/||' | tr -d '\r\n')"

    [ -n "$TAG" ] || die "не смог узнать последнюю версию (нет сети или GitHub недоступен)"
    VERSION="${TAG#v}"
    say "последняя: $VERSION (тег $TAG)"
else
    say "запрошена: $VERSION"
fi

# ---------------------------------------------------------------- скачивание

head1 "скачивание"

URL="https://github.com/$REPO/releases/download/v$VERSION/$ASSET"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

say "$URL"

HTTP="$(curl -sL -o "$TMP/az.tar.gz" -w '%{http_code}' --max-time 600 "$URL")"

if [ "$HTTP" != "200" ] || [ ! -s "$TMP/az.tar.gz" ]; then
    die "скачать не удалось (HTTP $HTTP). Проверьте, что релиз v$VERSION существует: https://github.com/$REPO/releases"
fi

say "размер: $(du -h "$TMP/az.tar.gz" | cut -f1)"

# Проверка целостности: архив должен быть tar, а не HTML-страница ошибки.
tar tzf "$TMP/az.tar.gz" >/dev/null 2>&1 || die "скачался не архив (возможно, страница ошибки)"

say "архив целый"

# ---------------------------------------------------------------- установка

head1 "установка в $PREFIX"

if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
    systemctl stop "$SERVICE"
    say "служба остановлена"
fi

# Остатки прошлого запуска держат очередь NFQUEUE — иначе новый nfqws получит
# «Operation not permitted» и обход не включится.
pkill -f "$PREFIX/AutoZapret --start" 2>/dev/null || true
pkill -f "$PREFIX/engine/bin/nfqws" 2>/dev/null || true
sleep 1

# Настройки переживают обновление: сохраняем data и возвращаем на место.
KEEP=""
if [ -d "$PREFIX/data" ]; then
    KEEP="$TMP/data-backup"
    cp -a "$PREFIX/data" "$KEEP"
    say "настройки сохранены"
fi

rm -rf "$PREFIX"
mkdir -p "$PREFIX"
tar xzf "$TMP/az.tar.gz" -C "$PREFIX"
chmod +x "$PREFIX/AutoZapret" "$PREFIX/engine/bin/nfqws" 2>/dev/null || true

if [ -n "$KEEP" ]; then
    rm -rf "$PREFIX/data"
    cp -a "$KEEP" "$PREFIX/data"
    say "настройки возвращены"
fi

say "файлы на месте: $(du -sh "$PREFIX" | cut -f1)"

# ---------------------------------------------------------------- служба

head1 "служба"

cat > "$UNIT" <<'UNITFILE'
[Unit]
Description=AutoZapret - DPI bypass (nfqws)
Documentation=https://github.com/dpteam/AutoZapret
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/opt/autozapret/AutoZapret --start
ExecStop=/opt/autozapret/AutoZapret --stop
Restart=on-failure
RestartSec=5
# Правила nftables снимаются по SIGTERM и в ExecStop. Без этого после остановки
# службы пакеты продолжили бы уходить в несуществующую очередь, и интернета
# не было бы вовсе.
KillSignal=SIGTERM
TimeoutStopSec=15
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
UNITFILE

systemctl daemon-reload
systemctl enable --quiet "$SERVICE"
say "автозапуск включён"

systemctl start "$SERVICE"
sleep 4

if ! systemctl is-active --quiet "$SERVICE"; then
    echo ""
    journalctl -u "$SERVICE" --no-pager -n 15 || true
    die "служба не запустилась — лог выше"
fi

say "служба работает"

# ---------------------------------------------------------------- проверка

head1 "проверка"

"$PREFIX/AutoZapret" --status || true

echo ""
echo "  доступность (10 с на каждую):"

for host in www.youtube.com discord.com www.google.com; do
    code=$(curl -4 -s -o /dev/null -w "%{http_code}" --max-time 10 "https://$host" 2>/dev/null || echo "000")
    t=$(curl -4 -s -o /dev/null -w "%{time_total}" --max-time 10 "https://$host" 2>/dev/null || echo "?")
    printf "    %-24s HTTP %-4s %ss\n" "$host" "$code" "$t"
done

echo ""
echo "Готово. AutoZapret $VERSION установлен и включён в автозапуск."
echo ""
echo "  состояние:  sudo $PREFIX/install.sh --status"
echo "  логи:       journalctl -u $SERVICE -f"
echo "  выключить:  sudo systemctl stop $SERVICE"
echo "  включить:   sudo systemctl start $SERVICE"
echo "  обновить:   sudo $PREFIX/install.sh"
echo "  удалить:    sudo $PREFIX/install.sh --remove"
echo ""
echo "Если YouTube не открывается — попробуйте другую стратегию:"
echo "  sudo $PREFIX/AutoZapret --start --strategy flowseal.general-alt9"
echo "  (список: $PREFIX/AutoZapret --strategies)"
