#!/bin/bash
# Получение движка обхода для Linux.
#
#   ./tools/fetch-engine.sh
#   ./tools/fetch-engine.sh --dir ~/.local/share/AutoZapret/engine
#
# Что делает: скачивает официальный релиз zapret (bol-van/zapret) и кладёт
# оттуда nfqws под эту архитектуру. Собирать из исходников не нужно: в релизе
# уже лежат статические бинарники для x86_64, arm64, arm, mips и других.
#
# Почему не git clone, как на Windows: там бандл — это репозиторий с готовыми
# exe. Здесь релизный tar.gz содержит и бинарники, и списки, и он меньше.
#
# Почему вообще nfqws: он понимает те же --dpi-desync, что winws (это один и
# тот же код с разными источниками пакетов), поэтому каталог стратегий
# работает без переделки.
set -u

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET="$REPO_ROOT/engine"

while [ $# -gt 0 ]; do
    case "$1" in
        --dir) TARGET="$2"; shift 2 ;;
        *) echo "неизвестный аргумент: $1"; exit 2 ;;
    esac
done

say() { echo "  $*"; }
fail() { echo ""; echo "[ERROR] $*"; exit 1; }

# ---------------------------------------------------------------- архитектура

ARCH="$(uname -m)"

case "$ARCH" in
    x86_64|amd64)   ZARCH="linux-x86_64" ;;
    aarch64|arm64)  ZARCH="linux-arm64" ;;
    armv7l|armv8l)  ZARCH="linux-arm" ;;
    i686|i386)      ZARCH="linux-x86" ;;
    mips64)         ZARCH="linux-mips64" ;;
    mips)           ZARCH="linux-mips" ;;
    *) fail "архитектура $ARCH не поддерживается релизом zapret" ;;
esac

say "архитектура: $ARCH -> $ZARCH"
say "каталог:     $TARGET"

if [ "$(id -u)" -ne 0 ] && [ ! -w "$(dirname "$TARGET")" ]; then
    say "ВНИМАНИЕ: может понадобиться sudo — каталог $TARGET недоступен на запись"
fi

# ---------------------------------------------------------------- релиз

TAG="$(curl -sI https://github.com/bol-van/zapret/releases/latest \
       | grep -i '^location:' | sed 's|.*/tag/||' | tr -d '\r\n')"

if [ -z "$TAG" ]; then
    fail "не смог узнать последний релиз zapret (нет сети или GitHub недоступен)"
fi

say "релиз:       $TAG"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

URL="https://github.com/bol-van/zapret/releases/download/$TAG/zapret-$TAG.tar.gz"
say "скачиваю:    $URL"

HTTP="$(curl -sL -o "$TMP/zapret.tar.gz" -w '%{http_code}' --max-time 300 "$URL")"

if [ "$HTTP" != "200" ] || [ ! -s "$TMP/zapret.tar.gz" ]; then
    fail "скачать не удалось (HTTP $HTTP). Проверьте сеть."
fi

say "размер:      $(du -h "$TMP/zapret.tar.gz" | cut -f1)"

tar xzf "$TMP/zapret.tar.gz" -C "$TMP" || fail "архив не распаковался"

SRC="$(find "$TMP" -maxdepth 1 -type d -name 'zapret-*' | head -1)"
[ -n "$SRC" ] || fail "в архиве нет каталога zapret-*"

# ---------------------------------------------------------------- раскладка

mkdir -p "$TARGET/bin" "$TARGET/lists" "$TARGET/lua" || fail "не создал $TARGET"

for tool in nfqws tpws; do
    if [ -f "$SRC/binaries/$ZARCH/$tool" ]; then
        cp -f "$SRC/binaries/$ZARCH/$tool" "$TARGET/bin/$tool"
        chmod +x "$TARGET/bin/$tool"
        say "положен:     bin/$tool"
    fi
done

# Списки. На Windows они приходят из бандла Flowseal; здесь берём те, что уже
# лежат в репозитории (engine/lists), иначе — из архива zapret.
if [ -d "$REPO_ROOT/engine/lists" ] && [ "$(ls -A "$REPO_ROOT/engine/lists" 2>/dev/null)" ]; then
    for f in "$REPO_ROOT/engine/lists"/*.txt; do
        [ -f "$f" ] || continue
        cp -f "$f" "$TARGET/lists/" 2>/dev/null
    done
    say "списки:      из engine/lists репозитория"
elif [ -d "$SRC/ipset" ]; then
    for f in "$SRC/ipset"/*.txt; do
        [ -f "$f" ] || continue
        cp -f "$f" "$TARGET/lists/" 2>/dev/null
    done
    say "списки:      из архива zapret"
else
    say "ВНИМАНИЕ: списков нет — обход будет работать только по доменам"
fi

# ---------------------------------------------------------------- проверка

if [ ! -x "$TARGET/bin/nfqws" ]; then
    fail "nfqws не появился в $TARGET/bin"
fi

say ""
say "проверка:    $("$TARGET/bin/nfqws" --version 2>&1 | head -1)"

if command -v iptables >/dev/null 2>&1; then
    say "iptables:    $(iptables --version 2>&1 | head -1)"
else
    say "ВНИМАНИЕ: iptables не найден — без него пакеты не попадут в NFQUEUE"
fi

if [ "$(id -u)" -ne 0 ]; then
    say ""
    say "Запускать обход нужно от root: sudo ./AutoZapret"
fi

echo ""
echo "Готово."
