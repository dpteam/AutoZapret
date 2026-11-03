# Конфиги zapret: разбор `zapret-discord-youtube` (v1) и `snanvl-zapret2` (zapret2)

Источники: `README.md`, `general*.bat` (22 файла в каждом репо), `service.bat` (v1 — 1311 строк, v2 — 1266),
`lists/`, `utils/`, `.service/`, `bin/`, `lua/`. Версия обеих сборок: `LOCAL_VERSION=1.10.4` (`service.bat:2`),
`.service/version.txt` = `1.10.4`.

Ключевой вывод для GUI: **v1 и zapret2 — это два разных движка с разным синтаксисом.**
v1 → `bin\winws.exe` + флаги `--dpi-desync*`. v2 → `bin\winws2.exe` + Lua-движок `--lua-desync` + `lua\*.lua`.
Генерировать «одну и ту же командную строку» нельзя: набор стратегий совпадает по смыслу, но не по букве.

---

## 1. Варианты командной строки winws и что меняет каждая стратегия

### 1.1 Общий каркас (одинаков для всех `general*.bat`)

Все файлы имеют один и тот же прелюд:

```bat
@echo off
chcp 65001 > nul
cd /d "%~dp0"
call service.bat status_zapret
call service.bat check_updates
call service.bat load_game_filter
call service.bat load_user_lists
set "BIN=%~dp0bin\"
set "LISTS=%~dp0lists\"
cd /d %BIN%
start "zapret: %~n0" /min "%BIN%winws.exe" <аргументы...>
```

- `status_zapret` — если служба `zapret` уже RUNNING, `:test_service ... soft` печатает предупреждение и делает `pause`
  (запуск вручную при установленной службе блокируется).
- `load_game_filter` — читает `utils\game_filter.enabled`, выставляет `%GameFilterTCP%`/`%GameFilterUDP%`.
  По умолчанию (файла нет) обе переменные = `12` → в `--wf-*` попадает порт `12`, т.е. «ничего».
- `load_user_lists` — создаёт отсутствующие `*-user.txt` (см. §3).

### 1.2 Полные литералы (v1)

**`general.bat`** (базовая стратегия; `%GameFilterTCP%`/`%GameFilterUDP%` подставляются из `utils\game_filter.enabled`):

```bat
start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443,2053,2083,2087,2096,8443,%GameFilterTCP% --wf-udp=443,19294-19344,50000-50100,%GameFilterUDP% ^
--filter-udp=443 --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-fake-discord="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-fake-stun="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-repeats=6 --new ^
--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media --dpi-desync=multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern="%BIN%tls_clienthello_www_google_com.bin" --new ^
--filter-tcp=443 --hostlist="%LISTS%list-google.txt" --ip-id=zero --dpi-desync=multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern="%BIN%tls_clienthello_www_google_com.bin" --new ^
--filter-tcp=80,443 --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=multisplit --dpi-desync-split-seqovl=568 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern="%BIN%tls_clienthello_4pda_to.bin" --new ^
--filter-udp=443 --ipset="%LISTS%ipset-all.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
--filter-tcp=80,443,8443 --ipset="%LISTS%ipset-all.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=multisplit --dpi-desync-split-seqovl=568 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern="%BIN%tls_clienthello_4pda_to.bin" --new ^
--filter-tcp=%GameFilterTCP% --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=multisplit --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n3 --dpi-desync-split-seqovl=568 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern="%BIN%tls_clienthello_4pda_to.bin" --new ^
--filter-udp=%GameFilterUDP% --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=12 --dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp="%BIN%ACTIVE_GAME_UDP.bin" --dpi-desync-cutoff=n2
```

**`general (ALT).bat`** (эталон «fakedsplit»-семейства):

```bat
start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443,2053,2083,2087,2096,8443,%GameFilterTCP% --wf-udp=443,19294-19344,50000-50100,%GameFilterUDP% ^
--filter-udp=443 --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-fake-discord="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-fake-stun="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-repeats=6 --new ^
--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media --dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls="%BIN%tls_clienthello_www_google_com.bin" --new ^
--filter-tcp=443 --hostlist="%LISTS%list-google.txt" --ip-id=zero --dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls="%BIN%tls_clienthello_www_google_com.bin" --new ^
--filter-tcp=80,443 --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls="%BIN%stun.bin" --dpi-desync-fake-tls="%BIN%tls_clienthello_www_google_com.bin" --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --new ^
--filter-udp=443 --ipset="%LISTS%ipset-all.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
--filter-tcp=80,443,8443 --ipset="%LISTS%ipset-all.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls="%BIN%stun.bin" --dpi-desync-fake-tls="%BIN%tls_clienthello_www_google_com.bin" --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --new ^
--filter-tcp=%GameFilterTCP% --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n4 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls="%BIN%stun.bin" --dpi-desync-fake-tls="%BIN%tls_clienthello_www_google_com.bin" --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --dpi-desync-fake-unknown="%BIN%stun.bin" --dpi-desync-fake-unknown="%BIN%tls_clienthello_www_google_com.bin" --new ^
--filter-udp=%GameFilterUDP% --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=12 --dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp="%BIN%ACTIVE_GAME_UDP.bin" --dpi-desync-cutoff=n3
```

**`general (FAKE TLS AUTO).bat`** (единственная стратегия с hex/`!`-фейками):

```bat
start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443,2053,2083,2087,2096,8443,%GameFilterTCP% --wf-udp=443,19294-19344,50000-50100,%GameFilterUDP% ^
--filter-udp=443 --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-fake-discord="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-fake-stun="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-repeats=6 --new ^
--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=badseq --dpi-desync-fake-tls=0x00000000 --dpi-desync-fake-tls=^! --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --new ^
--filter-tcp=443 --hostlist="%LISTS%list-google.txt" --ip-id=zero --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=badseq --dpi-desync-fake-tls=0x00000000 --dpi-desync-fake-tls=^! --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --new ^
--filter-tcp=80,443 --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=badseq --dpi-desync-fake-tls=0x00000000 --dpi-desync-fake-tls=^! --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --new ^
--filter-udp=443 --ipset="%LISTS%ipset-all.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
--filter-tcp=80,443,8443 --ipset="%LISTS%ipset-all.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=badseq --dpi-desync-fake-tls=0x00000000 --dpi-desync-fake-tls=^! --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --new ^
--filter-tcp=%GameFilterTCP% --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake,multidisorder --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n4 --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=badseq --dpi-desync-fake-tls=0x00000000 --dpi-desync-fake-tls=^! --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --dpi-desync-fake-unknown=0x00000000 --dpi-desync-fake-unknown="%BIN%tls_clienthello_www_sferum_ru.bin" --new ^
--filter-udp=%GameFilterUDP% --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=10 --dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp="%BIN%ACTIVE_GAME_UDP.bin" --dpi-desync-cutoff=n2
```

> ⚠ Для C#: `^!` в bat — это экранирование `!` от cmd. В argv winws реально уходит
> `--dpi-desync-fake-tls=!` (литеральный восклицательный знак). Передавать как есть, без `^`.

**`general (SIMPLE FAKE).bat`** — самое простое семейство (только `fake`):

```bat
start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443,2053,2083,2087,2096,8443,%GameFilterTCP% --wf-udp=443,19294-19344,50000-50100,%GameFilterUDP% ^
--filter-udp=443 --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-fake-discord="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-fake-stun="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-repeats=6 --new ^
--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls="%BIN%tls_clienthello_www_google_com.bin" --new ^
--filter-tcp=443 --hostlist="%LISTS%list-google.txt" --ip-id=zero --dpi-desync=hostfakesplit --dpi-desync-fooling=ts --dpi-desync-hostfakesplit-mod=host=www.google.com --new ^
--filter-tcp=80,443 --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls="%BIN%tls_clienthello_www_google_com.bin" --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --new ^
--filter-udp=443 --ipset="%LISTS%ipset-all.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
--filter-tcp=80,443,8443 --ipset="%LISTS%ipset-all.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls="%BIN%tls_clienthello_www_google_com.bin" --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --new ^
--filter-tcp=%GameFilterTCP% --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n4 --dpi-desync-fooling=ts --dpi-desync-fake-tls="%BIN%tls_clienthello_www_google_com.bin" --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --dpi-desync-fake-unknown="%BIN%stun2.bin" --dpi-desync-fake-unknown="%BIN%tls_clienthello_www_google_com.bin" --new ^
--filter-udp=%GameFilterUDP% --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=12 --dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp="%BIN%ACTIVE_GAME_UDP.bin" --dpi-desync-cutoff=n3
```

**`general (EXP).bat`** — единственная v1-стратегия с `--filter-l7=quic` вместо `--filter-udp=443`
и с `--filter-l7=discord,stun,unknown` + двумя `--dpi-desync-fake-discord`/`--dpi-desync-fake-unknown-udp`:

```bat
start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443,2053,2083,2087,2096,8443,%GameFilterTCP% --wf-udp=443,19294-19344,50000-50100,%GameFilterUDP% ^
--filter-l7=quic --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun,unknown --dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-fake-discord="%BIN%quic_initial_www_google_com.bin" --dpi-desync-fake-discord="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-fake-stun="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-fake-unknown-udp="%BIN%quic_initial_www_google_com.bin" --dpi-desync-fake-unknown-udp="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-repeats=4 --dpi-desync-cutoff=n4 --new ^
--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media --dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern="%BIN%tls_clienthello_www_google_com.bin" --dpi-desync-fake-tls="%BIN%tls_clienthello_www_google_com.bin" --new ^
--filter-tcp=443 --hostlist="%LISTS%list-google.txt" --ip-id=zero --dpi-desync=hostfakesplit --dpi-desync-fooling=ts --dpi-desync-hostfakesplit-mod=host=www.google.com --new ^
--filter-tcp=80,443 --hostlist="%LISTS%list-general.txt" --hostlist="%LISTS%list-general-user.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake,multisplit --dpi-desync-split-seqovl=480 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=4 --dpi-desync-split-seqovl-pattern="%BIN%stun2.bin" --dpi-desync-fake-tls="%BIN%tls_clienthello_www_sferum_ru.bin" --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --new ^
--filter-udp=443 --ipset="%LISTS%ipset-all.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic="%BIN%quic_initial_www_google_com.bin" --new ^
--filter-tcp=80,443,8443 --ipset="%LISTS%ipset-all.txt" --hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" --hostlist-exclude-domains=fonts.googleapis.com --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake,multisplit --dpi-desync-split-seqovl=480 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=4 --dpi-desync-split-seqovl-pattern="%BIN%stun2.bin" --dpi-desync-fake-tls="%BIN%tls_clienthello_www_sferum_ru.bin" --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --new ^
--filter-tcp=%GameFilterTCP% --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake,multisplit --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n4 --dpi-desync-split-seqovl=664 --dpi-desync-split-pos=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8 --dpi-desync-split-seqovl-pattern="%BIN%tls_clienthello_www_sferum_ru.bin" --dpi-desync-fake-tls="%BIN%stun2.bin" --dpi-desync-fake-tls="%BIN%tls_clienthello_www_sferum_ru.bin" --dpi-desync-fake-http="%BIN%tls_clienthello_www_sferum_ru.bin" --new ^
--filter-udp=%GameFilterUDP% --ipset="%LISTS%ipset-all.txt" --ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" --dpi-desync=fake --dpi-desync-repeats=5 --dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp="%BIN%quic_initial_4pda_to.bin" --dpi-desync-fake-unknown-udp="%BIN%ACTIVE_GAME_UDP.bin" --dpi-desync-cutoff=n4
```

### 1.3 Таблица стратегий v1: что реально меняется

Правила (rules) нумеруем как в §1.2: R1=UDP443 QUIC, R2=Discord Voice, R3=Discord Media(TCP 2053…),
R4=YouTube(list-google), R5=домены(TCP 80,443), R6=UDP443 ipset, R7=TCP ipset, R8=GameFilter TCP, R9=GameFilter UDP.
Во **всех** стратегиях R1, R2, R6, R9 идентичны базовым (кроме явно указанных отличий) — это «несущая» часть.

| Стратегия | Ключевое отличие (verbatim-фрагменты) | Цель |
|---|---|---|
| `general` | R3–R5,R7,R8: `--dpi-desync=multisplit --dpi-desync-split-seqovl=681` (R3/R4, `tls_clienthello_www_google_com.bin`) / `=568` (R5,R7,R8, `tls_clienthello_4pda_to.bin`), `--dpi-desync-split-pos=1` | универсал: Discord + YouTube |
| `ALT` | R3–R5,R7,R8: `--dpi-desync=fake,fakedsplit --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-repeats=6`, fake-tls = `stun.bin`+`tls_clienthello_www_google_com.bin`, fake-http = sferum | универсал |
| `ALT2` | R3–R5,R7,R8: `multisplit --dpi-desync-split-seqovl=652 --dpi-desync-split-pos=2`, pattern = tls_google во всех правилах (в т.ч. R5 вместо 4pda) | универсал, «сдвиг на 2» |
| `ALT3` | R3–R5,R7,R8: `--dpi-desync=fake,hostfakesplit --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` (общие) / `sni=ya.ru`, `--dpi-desync-hostfakesplit-mod=host=www.google.com,altorder=1` / `host=ya.ru,altorder=1`, `--dpi-desync-fooling=ts` | универсал (hostfakesplit) |
| `ALT4` | `--dpi-desync=fake,multisplit --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=1000 --dpi-desync-repeats=6`, fake-tls = `stun.bin`+google | универсал (badseq 1000) |
| `ALT5` | **`:: NOT RECOMMENDED`** в шапке. Добавлено правило `--filter-l3=ipv4 --filter-tcp=80,443,2053,2083,2087,2096,8443 --dpi-desync=syndata,multidisorder` (без hostlist/ipset, только ipset-exclude); R8 = `syndata,multidisorder --dpi-desync-cutoff=n4`; R9 `--dpi-desync-repeats=14 --dpi-desync-cutoff=n3` | «глушит всё» — агрессивный |
| `ALT6` | Как `general`, но seqovl/pattern унифицированы: `--dpi-desync-split-seqovl=681` + `tls_clienthello_www_google_com.bin` во всех R3–R8 | универсал (google-паттерн везде) |
| `ALT7` | `multisplit --dpi-desync-split-pos=2,sniext+1 --dpi-desync-split-seqovl=679`; R7 = `--dpi-desync=syndata`; R8 = `syndata --dpi-desync-cutoff=n4` | универсал + syndata для ipset |
| `ALT8` | `--dpi-desync=fake --dpi-desync-fake-tls-mod=none --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=2 --dpi-desync-repeats=6`, fake-http = sferum; R8 добавляет `--dpi-desync-fake-unknown=` | универсал (fake без TLS-мода) |
| `ALT9` | `--dpi-desync=hostfakesplit --dpi-desync-repeats=4 --dpi-desync-fooling=ts` (R5: `ts,md5sig`), `--dpi-desync-hostfakesplit-mod=host=www.google.com` (R3/R4) / `host=ozon.ru` (R5,R7–R8) | универсал |
| `ALT10` | `--dpi-desync=fake --dpi-desync-fooling=ts --dpi-desync-repeats=6`, fake-tls = `stun.bin` + `tls_clienthello_4pda_to.bin`; R3 дополнительно `--dpi-desync-fake-tls-mod=none` | универсал |
| `ALT11` | `--dpi-desync=fake,multisplit --dpi-desync-fooling=ts --dpi-desync-repeats=8`, R3/R4 seqovl=681+tls_google, R5/R7/R8 seqovl=664+`tls_clienthello_www_sferum_ru.bin`; fake-tls = `stun2.bin`+sferum | универсал |
| `ALT12` | Как ALT11, но R2: `--dpi-desync-fake-discord="%BIN%stun.bin" --dpi-desync-fake-discord="%BIN%ACTIVE_DISCORD_UDP.bin" --dpi-desync-repeats=3`; R4 = `hostfakesplit host=www.google.com` | Discord-голос усилен |
| `ALT13` | `--dpi-desync=fake,hostfakesplit --dpi-desync-hostfakesplit-mod=host=mail.ru,altorder=1 --dpi-desync-fooling=ts --dpi-desync-repeats=5`, fake-tls = `tls_clienthello_sochi_park.bin`+`stun2.bin`; R4 `hostfakesplit host=www.google.com` **без `--ip-id=zero`** | универсал (mail.ru-фейк) |
| `EXP` | `--filter-l7=quic` вместо `--filter-udp=443` в R1; R2 `--filter-l7=discord,stun,unknown` + двойные fake-discord/fake-unknown-udp; R5/R7 `fake,multisplit --dpi-desync-split-seqovl=480 --dpi-desync-split-seqovl-pattern="%BIN%stun2.bin"`; R8 seqovl=664; `--dpi-desync-cutoff=n4` | экспериментальная |
| `FAKE TLS AUTO` | `--dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=11 --dpi-desync-fooling=badseq --dpi-desync-fake-tls=0x00000000 --dpi-desync-fake-tls=^! --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` | универсал, автогенерация TLS |
| `FAKE TLS AUTO ALT` | `--dpi-desync=fake,fakedsplit --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=2 --dpi-desync-repeats=8 --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` | универсал |
| `FAKE TLS AUTO ALT2` | `--dpi-desync=fake,multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=10000000 --dpi-desync-repeats=8 --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com` | универсал (гигантский badseq) |
| `FAKE TLS AUTO ALT3` | То же, что ALT2, но `--dpi-desync-fooling=ts` (без badseq-increment) | универсал |
| `SIMPLE FAKE` | Только `--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=tls_clienthello_www_google_com.bin`; R4 = `hostfakesplit host=www.google.com` | универсал, минимум магии |
| `SIMPLE FAKE ALT` | То же, но `--dpi-desync-fooling=badseq --dpi-desync-badseq-increment=2`, fake-tls = `stun2.bin`+tls_google; R8 `--dpi-desync-cutoff=n3` | универсал |
| `SIMPLE FAKE ALT2` | То же, но `--dpi-desync-fooling=ts`, fake-tls = `stun2.bin`+`tls_clienthello_www_sferum_ru.bin`; R8 `--dpi-desync-cutoff=n5`, R9 `repeats=12 --dpi-desync-cutoff=n3` | универсал |

**Целевые ресурсы по правилам (общее для всех стратегий):**

| Правило | Что покрывает | Механизм отбора |
|---|---|---|
| R1 UDP/443 | QUIC: YouTube, Google, Discord, Cloudflare | `--hostlist` (list-general + user) |
| R2 UDP 19294-19344, 50000-50100 | голос Discord, STUN | `--filter-l7=discord,stun` |
| R3 TCP 2053,2083,2087,2096,8443 | медиа Discord (CDN на нестандартных портах) | `--hostlist-domains=discord.media` |
| R4 TCP 443 | YouTube / Google | `--hostlist=list-google.txt` + `--ip-id=zero` |
| R5 TCP 80,443 | остальные заблокированные домены | `--hostlist=list-general.txt` |
| R6 UDP/443 ipset | «всё по IP» QUIC | `--ipset=ipset-all.txt` |
| R7 TCP 80,443,8443 ipset | «всё по IP» TLS | `--ipset` |
| R8/R9 GameFilter | игры и прочее на портах >1023 | `--filter-tcp/udp=%GameFilterTCP/UDP%` + `--dpi-desync-any-protocol=1` |

### 1.4 Каркас v2 (zapret2) и полные литералы

Отличия каркаса: `set "LUA=%~dp0lua\"`, `"%BIN%winws2.exe" --debug=0 --wf-tcp-out=... --wf-udp-out=...`,
далее три `--lua-init`, затем профили `--name="..." ... --new`. Никаких `--dpi-desync*` вообще.

**`general.bat` (v2)** — полный литерал:

```bat
start "zapret: %~n0" /min "%BIN%winws2.exe" --debug=0 --wf-tcp-out=80,443,2053,2083,2087,2096,8443,%GameFilterTCP% --wf-udp-out=443,19294-19344,50000-50100,%GameFilterUDP% ^

--lua-init=@"%LUA%blobs.lua" ^
--lua-init=@"%LUA%zapret-lib.lua" ^
--lua-init=@"%LUA%zapret-antidpi.lua" ^

--name="Domain Bypass Quic" ^
--filter-udp=443 ^
--hostlist="%LISTS%list-general.txt"         --hostlist="%LISTS%list-general-user.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=quic_initial ^
--lua-desync=fake:blob=quic_google:repeats=6 ^
--new ^

--name="Discord Voice" ^
--filter-udp=19294-19344,50000-50100 ^
--filter-l7=discord,stun ^
--payload=discord_ip_discovery,stun ^
--lua-desync=fake:blob=ACTIVE_DISCORD:repeats=6 ^
--new ^

--name="Discord Media" ^
--filter-tcp=2053,2083,2087,2096,8443 ^
--hostlist-domains=discord.media ^
--payload=tls_client_hello ^
--lua-desync=multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google ^
--new ^

--name="Youtube" ^
--filter-tcp=443 ^
--hostlist="%LISTS%list-google.txt" ^
--payload=tls_client_hello ^
--lua-desync=multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google:ip_id=zero ^
--new ^

--name="Domain Bypass TLS" ^
--filter-tcp=80,443 ^
--hostlist="%LISTS%list-general.txt"         --hostlist="%LISTS%list-general-user.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=tls_client_hello,http_req ^
--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=tls_4pda ^
--new ^

--name="IP Bypass Quic" ^
--filter-udp=443 ^
--ipset="%LISTS%ipset-all.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=quic_initial ^
--lua-desync=fake:blob=quic_google:repeats=6 ^
--new ^

--name="IP Bypass TLS" ^
--filter-tcp=80,443,8443 ^
--ipset="%LISTS%ipset-all.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=tls_client_hello,http_req ^
--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=tls_4pda ^
--new ^

--name="GameFilter Bypass TCP" ^
--filter-tcp=%GameFilterTCP% ^
--ipset="%LISTS%ipset-all.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--out-range=-n3 ^
--payload=all ^
--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=tls_4pda:payload=all ^
--new ^

--name="GameFilter Bypass UDP" ^
--filter-udp=%GameFilterUDP% ^
--ipset="%LISTS%ipset-all.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--out-range=-n2 ^
--payload=all ^
--lua-desync=fake:blob=ACTIVE_GAME:repeats=12:payload=all
```

**`general (ALT).bat` (v2)** — полный литерал (эталон `fake`+`fakedsplit` через Lua):

```bat
start "zapret: %~n0" /min "%BIN%winws2.exe" --debug=0 --wf-tcp-out=80,443,2053,2083,2087,2096,8443,%GameFilterTCP% --wf-udp-out=443,19294-19344,50000-50100,%GameFilterUDP% ^

--lua-init=@"%LUA%blobs.lua" ^
--lua-init=@"%LUA%zapret-lib.lua" ^
--lua-init=@"%LUA%zapret-antidpi.lua" ^

--name="Domain Bypass Quic" ^
--filter-udp=443 ^
--hostlist="%LISTS%list-general.txt"         --hostlist="%LISTS%list-general-user.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=quic_initial ^
--lua-desync=fake:blob=quic_google:repeats=6 ^
--new ^

--name="Discord Voice" ^
--filter-udp=19294-19344,50000-50100 ^
--filter-l7=discord,stun ^
--payload=discord_ip_discovery,stun ^
--lua-desync=fake:blob=ACTIVE_DISCORD:repeats=6 ^
--new ^

--name="Discord Media" ^
--filter-tcp=2053,2083,2087,2096,8443 ^
--hostlist-domains=discord.media ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=tls_google:tcp_ts=-600000:repeats=6 ^
--lua-desync=fakedsplit:tcp_ts=-600000:repeats=6 ^
--new ^

--name="Youtube" ^
--filter-tcp=443 ^
--hostlist="%LISTS%list-google.txt" ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=tls_google:tcp_ts=-600000:ip_id=zero:repeats=6 ^
--lua-desync=fakedsplit:tcp_ts=-600000:ip_id=zero:repeats=6 ^
--new ^

--name="Domain Bypass TLS" ^
--filter-tcp=80,443 ^
--hostlist="%LISTS%list-general.txt"         --hostlist="%LISTS%list-general-user.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=stun:tcp_ts=-600000:repeats=6 ^
--lua-desync=fake:blob=tls_google:tcp_ts=-600000:repeats=6 ^
--lua-desync=fakedsplit:tcp_ts=-600000:repeats=6 ^
--payload=http_req ^
--lua-desync=fake:blob=tls_sferum:tcp_ts=-600000:repeats=6 ^
--lua-desync=fakedsplit:tcp_ts=-600000:repeats=6 ^
--new ^

--name="IP Bypass Quic" ^
--filter-udp=443 ^
--ipset="%LISTS%ipset-all.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=quic_initial ^
--lua-desync=fake:blob=quic_google:repeats=6 ^
--new ^

--name="IP Bypass TLS" ^
--filter-tcp=80,443,8443 ^
--ipset="%LISTS%ipset-all.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=stun:tcp_ts=-600000:repeats=6 ^
--lua-desync=fake:blob=tls_google:tcp_ts=-600000:repeats=6 ^
--lua-desync=fakedsplit:tcp_ts=-600000:repeats=6 ^
--payload=http_req ^
--lua-desync=fake:blob=tls_sferum:tcp_ts=-600000:repeats=6 ^
--lua-desync=fakedsplit:tcp_ts=-600000:repeats=6 ^
--new ^

--name="GameFilter Bypass TCP" ^
--filter-tcp=%GameFilterTCP% ^
--ipset="%LISTS%ipset-all.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--out-range=-n4 ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=stun:tcp_ts=-600000:repeats=6 ^
--lua-desync=fake:blob=tls_google:tcp_ts=-600000:repeats=6 ^
--lua-desync=fakedsplit:tcp_ts=-600000:repeats=6 ^
--payload=http_req ^
--lua-desync=fake:blob=tls_sferum:tcp_ts=-600000:repeats=6 ^
--lua-desync=fakedsplit:tcp_ts=-600000:repeats=6 ^
--payload=unknown ^
--lua-desync=fake:blob=stun:tcp_ts=-600000:repeats=6:payload=all ^
--lua-desync=fake:blob=tls_google:tcp_ts=-600000:repeats=6:payload=all ^
--lua-desync=fakedsplit:tcp_ts=-600000:repeats=6:payload=all ^
--new ^

--name="GameFilter Bypass UDP" ^
--filter-udp=%GameFilterUDP% ^
--ipset="%LISTS%ipset-all.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--out-range=-n3 ^
--payload=all ^
--lua-desync=fake:blob=ACTIVE_GAME:repeats=12:payload=all
```

**`general (FAKE TLS AUTO).bat` (v2)** — обратите внимание на `--blob=zero4:0x00000000` **до** `--lua-init`:

```bat
start "zapret: %~n0" /min "%BIN%winws2.exe" --debug=0 --wf-tcp-out=80,443,2053,2083,2087,2096,8443,%GameFilterTCP% --wf-udp-out=443,19294-19344,50000-50100,%GameFilterUDP% ^

--blob=zero4:0x00000000 ^

--lua-init=@"%LUA%blobs.lua" ^
--lua-init=@"%LUA%zapret-lib.lua" ^
--lua-init=@"%LUA%zapret-antidpi.lua" ^

--name="Domain Bypass Quic" ^
--filter-udp=443 ^
--hostlist="%LISTS%list-general.txt"         --hostlist="%LISTS%list-general-user.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=quic_initial ^
--lua-desync=fake:blob=quic_google:repeats=11 ^
--new ^

--name="Discord Voice" ^
--filter-udp=19294-19344,50000-50100 ^
--filter-l7=discord,stun ^
--payload=discord_ip_discovery,stun ^
--lua-desync=fake:blob=ACTIVE_DISCORD:repeats=6 ^
--new ^

--name="Discord Media" ^
--filter-tcp=2053,2083,2087,2096,8443 ^
--hostlist-domains=discord.media ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=zero4:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=fake:blob=fake_default_tls:tls_mod=rnd,dupsid,sni=www.google.com:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=multidisorder:pos=1,midsld ^
--new ^

--name="Youtube" ^
--filter-tcp=443 ^
--hostlist="%LISTS%list-google.txt" ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=zero4:tcp_seq=-10000:tcp_ack=-66000:ip_id=zero:repeats=11 ^
--lua-desync=fake:blob=fake_default_tls:tls_mod=rnd,dupsid,sni=www.google.com:tcp_seq=-10000:tcp_ack=-66000:ip_id=zero:repeats=11 ^
--lua-desync=multidisorder:pos=1,midsld:ip_id=zero ^
--new ^

--name="Domain Bypass TLS" ^
--filter-tcp=80,443 ^
--hostlist="%LISTS%list-general.txt"         --hostlist="%LISTS%list-general-user.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=zero4:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=fake:blob=fake_default_tls:tls_mod=rnd,dupsid,sni=www.google.com:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=multidisorder:pos=1,midsld ^
--payload=http_req ^
--lua-desync=fake:blob=tls_sferum:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=multidisorder:pos=1,midsld ^
--new ^

--name="IP Bypass Quic" ^
--filter-udp=443 ^
--ipset="%LISTS%ipset-all.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=quic_initial ^
--lua-desync=fake:blob=quic_google:repeats=11 ^
--new ^

--name="IP Bypass TLS" ^
--filter-tcp=80,443,8443 ^
--ipset="%LISTS%ipset-all.txt" ^
--hostlist-exclude="%LISTS%list-exclude.txt" --hostlist-exclude="%LISTS%list-exclude-user.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt"   --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=zero4:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=fake:blob=fake_default_tls:tls_mod=rnd,dupsid,sni=www.google.com:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=multidisorder:pos=1,midsld ^
--payload=http_req ^
--lua-desync=fake:blob=tls_sferum:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=multidisorder:pos=1,midsld ^
--new ^

--name="GameFilter Bypass TCP" ^
--filter-tcp=%GameFilterTCP% ^
--ipset="%LISTS%ipset-all.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--out-range=-n4 ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=zero4:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=fake:blob=fake_default_tls:tls_mod=rnd,dupsid,sni=www.google.com:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=multidisorder:pos=1,midsld ^
--payload=http_req ^
--lua-desync=fake:blob=tls_sferum:tcp_seq=-10000:tcp_ack=-66000:repeats=11 ^
--lua-desync=multidisorder:pos=1,midsld ^
--payload=unknown ^
--lua-desync=fake:blob=zero4:tcp_seq=-10000:tcp_ack=-66000:repeats=11:payload=all ^
--lua-desync=fake:blob=tls_sferum:tls_mod=rnd,sni=www.google.com:tcp_seq=-10000:tcp_ack=-66000:repeats=11:payload=all ^
--lua-desync=multidisorder:pos=1,midsld:payload=all ^
--new ^

--name="GameFilter Bypass UDP" ^
--filter-udp=%GameFilterUDP% ^
--ipset="%LISTS%ipset-all.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--out-range=-n2 ^
--payload=all ^
--lua-desync=fake:blob=ACTIVE_GAME:repeats=10:payload=all
```

**`general (EXP).bat` (v2)** — единственный файл с inline-Lua через `--lua-init` и `zero256`:

```bat
--lua-init=@"%LUA%blobs.lua" ^
--lua-init=@"%LUA%zapret-lib.lua" ^
--lua-init=@"%LUA%zapret-antidpi.lua" ^
--lua-init="zero256=string.rep(string.char(0),256)" ^
...
--name="Discord Voice" ^
--filter-udp=19294-19344,50000-50100 ^
--filter-l7=discord,stun,unknown ^
--out-range=-n4 ^
--payload=discord_ip_discovery ^
--lua-desync=fake:blob=quic_google:repeats=4 ^
--lua-desync=fake:blob=ACTIVE_DISCORD:repeats=4 ^
--payload=stun ^
--lua-desync=fake:blob=ACTIVE_DISCORD:repeats=4 ^
--payload=unknown ^
--lua-desync=fake:blob=quic_google:repeats=4:payload=all ^
--lua-desync=fake:blob=ACTIVE_DISCORD:repeats=4:payload=all ^
--new ^
...
--name="GameFilter Bypass TCP" ^
--filter-tcp=%GameFilterTCP% ^
--ipset="%LISTS%ipset-all.txt" ^
--ipset-exclude="%LISTS%ipset-exclude.txt" --ipset-exclude="%LISTS%ipset-exclude-user.txt" ^
--out-range=-n4 ^
--payload=tls_client_hello ^
--lua-desync=fake:blob=stun2:tcp_ts=-600000:repeats=8 ^
--lua-desync=fake:blob=tls_sferum:tcp_ts=-600000:repeats=8 ^
--lua-desync=multisplit:pos=1:seqovl=661:seqovl_pattern=tls_sferum ^
--payload=http_req ^
--lua-desync=fake:blob=tls_sferum:tcp_ts=-600000:repeats=8 ^
--lua-desync=multisplit:pos=1:seqovl=661:seqovl_pattern=tls_sferum ^
--payload=unknown ^
--lua-desync=fake:blob=zero256:tcp_ts=-600000:repeats=8:payload=all ^
--lua-desync=multisplit:pos=1:seqovl=661:seqovl_pattern=tls_sferum:payload=all ^
--new ^
```

### 1.5 Таблица стратегий v2 и мэппинг v1→v2

| v2 стратегия | Отличительные `--lua-desync` (verbatim) | Аналог v1 |
|---|---|---|
| `general` | `multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google` / `:568:...tls_4pda` | `general` |
| `ALT` | `fake:blob=tls_google:tcp_ts=-600000:repeats=6` + `fakedsplit:tcp_ts=-600000:repeats=6` | `ALT` |
| `ALT2` | `multisplit:pos=2:seqovl=652:seqovl_pattern=tls_google` | `ALT2` |
| `ALT3` | `fake:blob=fake_default_tls:tls_mod=rnd,dupsid,sni=www.google.com:tcp_ts=-600000` + `hostfakesplit:host=www.google.com:tcp_ts=-600000` (домены: `sni=ya.ru`/`host=ya.ru`) | `ALT3` |
| `ALT4` | `fake:blob=tls_google:tcp_seq=1000:tcp_ack=-66000:tcp_ts_up:repeats=6` + `multisplit` | `ALT4` |
| `ALT5` | `:: NOT RECOMMENDED`; `--name="TCP Bypass" --filter-l3=ipv4 --filter-tcp=80,443,2053,2083,2087,2096,8443 --lua-desync=syndata --payload=tls_client_hello,http_req --lua-desync=multidisorder` | `ALT5` |
| `ALT6` | `multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google` во всех правилах | `ALT6` |
| `ALT7` | `multisplit:pos=2,sniext+1:seqovl=679:seqovl_pattern=tls_google`; IP Bypass TLS / GameFilter = `syndata` | `ALT7` |
| `ALT8` | `fake:blob=fake_default_tls:tcp_seq=2:tcp_ack=-66000:tcp_ts_up:repeats=6` (http_req → `blob=tls_sferum`) | `ALT8` |
| `ALT9` | `hostfakesplit:host=www.google.com:tcp_ts=-600000:repeats=4`, домены `host=ozon.ru:tcp_ts=-600000:tcp_md5:repeats=4` | `ALT9` |
| `ALT10` | `fake:blob=tls_google:tcp_ts=-600000:repeats=6` / `blob=tls_4pda` | `ALT10` |
| `ALT11` | `fake:blob=tls_google:tcp_ts=-600000:repeats=8` + `multisplit:pos=1:seqovl=681` (домены: `stun2`+`tls_sferum`, seqovl=661) | `ALT11` |
| `ALT12` | Discord Voice разбит на `--payload=discord_ip_discovery` (`blob=stun`+`ACTIVE_DISCORD`, repeats=3) и `--payload=stun`; Youtube = `hostfakesplit` | `ALT12` |
| `ALT13` | `fake:blob=tls_sochi_park:tcp_ts=-600000:repeats=5` + `stun2` + `hostfakesplit:host=mail.ru` | `ALT13` |
| `EXP` | `--filter-l7=quic`, `--lua-init="zero256=..."`, `blob=zero256`, `multisplit:pos=1:seqovl=661:seqovl_pattern=tls_sferum`, `--out-range=-n4` | `EXP` |
| `FAKE TLS AUTO` | `--blob=zero4:0x00000000`, `fake:blob=zero4:tcp_seq=-10000:tcp_ack=-66000:repeats=11` + `multidisorder:pos=1,midsld` | `FAKE TLS AUTO` |
| `FAKE TLS AUTO ALT` | `fake:blob=fake_default_tls:tls_mod=rnd,dupsid,sni=www.google.com:tcp_seq=2:tcp_ack=-66000:tcp_ts_up:repeats=8` + `fakedsplit:pos=1:...` | `FAKE TLS AUTO ALT` |
| `FAKE TLS AUTO ALT2` | `...:tcp_seq=10000000:tcp_ack=-66000:repeats=8` + `multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google` | `FAKE TLS AUTO ALT2` |
| `FAKE TLS AUTO ALT3` | `...:tcp_ts=-600000:repeats=8` + `multisplit:pos=1:seqovl=681` | `FAKE TLS AUTO ALT3` |
| `SIMPLE FAKE` | `fake:blob=tls_google:tcp_ts=-600000:repeats=6`; Youtube = `hostfakesplit:host=www.google.com` | `SIMPLE FAKE` |
| `SIMPLE FAKE ALT` | `fake:blob=stun2:tcp_seq=2:tcp_ack=-66000:tcp_ts_up:repeats=6` + `blob=tls_google` | `SIMPLE FAKE ALT` |
| `SIMPLE FAKE ALT2` | `fake:blob=stun2:tcp_ts=-600000:repeats=6` + `blob=tls_sferum` | `SIMPLE FAKE ALT2` |

**Правила перевода v1 → v2 (для генератора):**

| v1 | v2 |
|---|---|
| `--dpi-desync=fake` + `--dpi-desync-fake-X="…\N.bin"` + `--dpi-desync-repeats=N` | `--lua-desync=fake:blob=<alias>:repeats=N` (алиас из `lua\blobs.lua`) |
| `--dpi-desync=multisplit --dpi-desync-split-pos=P --dpi-desync-split-seqovl=S --dpi-desync-split-seqovl-pattern=…bin` | `--lua-desync=multisplit:pos=P:seqovl=S:seqovl_pattern=<alias>` |
| `--dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld` | `--lua-desync=multidisorder:pos=1,midsld` |
| `--dpi-desync=hostfakesplit --dpi-desync-hostfakesplit-mod=host=X[,altorder=1]` | `--lua-desync=hostfakesplit:host=X` |
| `--dpi-desync=fakedsplit --dpi-desync-fakedsplit-pattern=0x00` | `--lua-desync=fakedsplit` |
| `--dpi-desync=syndata` | `--lua-desync=syndata` |
| `--dpi-desync-fooling=ts` | `:tcp_ts=-600000` |
| `--dpi-desync-fooling=badseq --dpi-desync-badseq-increment=N` | `:tcp_seq=N` (+`tcp_ack=-66000` в новых) |
| `--dpi-desync-fooling=md5sig` | `:tcp_md5` |
| `--dpi-desync-fake-tls-mod=rnd,dupsid,sni=X` | `:tls_mod=rnd,dupsid,sni=X` |
| `--ip-id=zero` | `:ip_id=zero` |
| `--dpi-desync-any-protocol=1` | `--payload=all` |
| `--dpi-desync-cutoff=nN` | `--out-range=-nN` |
| `--dpi-desync=fake-tls=0x00000000` | `--blob=zero4:0x00000000` + `:blob=zero4` |
| `--filter-udp=443` (QUIC) | `--filter-udp=443 --payload=quic_initial` |
| `--wf-tcp=` / `--wf-udp=` | `--wf-tcp-out=` / `--wf-udp-out=` |
| — | `--name="…"` (обязателен для читаемости профиля) |
| — | `--lua-init=@"…\lua\*.lua"` (3 файла, всегда первыми) |

---

## 2. Полный набор флагов

### 2.1 Обязательные (без них winws не работает / не фильтрует)

| Флаг | Смысл |
|---|---|
| `--wf-tcp=порты` (v1) | WinDivert-фильтр: какие TCP-порты вообще перехватывать (в обе стороны) |
| `--wf-udp=порты` (v1) | То же для UDP |
| `--wf-tcp-out=`, `--wf-udp-out=` (v2) | То же, но только исходящее направление |
| `--new` | Разделитель профилей (desync-инстансов). Всё после — новый профиль со своими фильтрами |
| `--filter-tcp=`, `--filter-udp=` | Отбор пакетов внутри профиля по порту (подмножество `--wf-*`) |
| `--dpi-desync=<список>` (v1) | Список техник через запятую, напр. `fake,multisplit`. Ядро стратегии |
| `--lua-desync=<func:arg:arg>` (v2) | То же в zapret2: вызов Lua-функции из `zapret-antidpi.lua` |

### 2.2 Селекторы (какие пакеты/соединения попадут под desync)

| Флаг | Смысл | Файл? |
|---|---|---|
| `--hostlist=` | Список доменов для обработки (можно несколько раз) | да |
| `--hostlist-exclude=` | Домены-исключения | да |
| `--hostlist-domains=` | Inline-список доменов прямо в аргументе (`discord.media`) | нет |
| `--hostlist-exclude-domains=` | Inline-исключения доменов (`fonts.googleapis.com`) | нет |
| `--ipset=` | Список IP/подсетей для обработки | да |
| `--ipset-exclude=` | IP-исключения | да |
| `--filter-l7=discord,stun[,quic,unknown]` | Отбор по прикладному протоколу (L7-парсер) | нет |
| `--filter-l3=ipv4` | Только IPv4 | нет |
| `--payload=quic_initial, tls_client_hello, http_req, discord_ip_discovery, stun, unknown, all` (v2) | К какому типу payload применять desync; задаёт «секции» внутри профиля | нет |
| `--out-range=-nN` (v2) | Эквивалент v1 `--dpi-desync-cutoff=nN`: применять только к первым N пакетам потока | нет |

### 2.3 Техники (`--dpi-desync=`)

`fake`, `multisplit`, `multidisorder`, `fakedsplit`, `hostfakesplit`, `syndata`, `fake,multisplit`,
`fake,fakedsplit`, `fake,hostfakesplit`, `fake,multidisorder`, `syndata,multidisorder`.
В v2 им соответствуют одноимённые функции в `lua\zapret-antidpi.lua` (плюс `drop`, `send`, `pktmod`,
`http_domcase`, `http_hostcase`, `http_methodeol`, `http_unixeol`, `synack_split`, `synack`, `wsize`,
`wssize`, `tls_client_hello_clone`, `rst`, `oob`, `udplen`, `multidisorder_legacy`).

### 2.4 Настройки техник (опциональные «крутилки»)

| Флаг | Смысл |
|---|---|
| `--dpi-desync-repeats=N` | Сколько раз отправить фейк/сегменты (в файлах 3…14) |
| `--dpi-desync-split-pos=` | Позиция разреза: число, `2`, `sniext+1`, `1,midsld`, `host`, `midsld` |
| `--dpi-desync-split-seqovl=N` | Перекрытие sequence на N байт (568/652/664/679/681/480) |
| `--dpi-desync-split-seqovl-pattern=путь` | Чем заполнять перекрытие (bin-файл) |
| `--dpi-desync-fakedsplit-pattern=0x00` | Байт-заполнитель для `fakedsplit` |
| `--dpi-desync-hostfakesplit-mod=host=X[,altorder=1]` | Подменный хост для `hostfakesplit` |
| `--dpi-desync-fake-tls=путь\|0xHEX\|!` | Фейковый TLS ClientHello (можно несколько) |
| `--dpi-desync-fake-tls-mod=none\|rnd,dupsid,sni=…` | Модификация фейкового TLS (`none` = отключить автоподстановку SNI) |
| `--dpi-desync-fake-http=путь` | Фейковый HTTP-запрос |
| `--dpi-desync-fake-quic=путь` | Фейковый QUIC Initial |
| `--dpi-desync-fake-discord=путь` | Фейк для Discord-голоса |
| `--dpi-desync-fake-stun=путь` | Фейк для STUN |
| `--dpi-desync-fake-unknown=путь`, `--dpi-desync-fake-unknown-udp=путь` | Фейк для неопознанного трафика |
| `--dpi-desync-fooling=ts\|badseq\|md5sig\|datanoack` | Способ «обмануть» стек: TS-опция / неверный seq / MD5-подпись |
| `--dpi-desync-badseq-increment=N` | Величина сдвига seq при `badseq` (2 / 1000 / 10000000) |
| `--dpi-desync-any-protocol=1` | Применять ко всем протоколам, не только TLS/HTTP/QUIC |
| `--dpi-desync-cutoff=nN` | Прекратить обработку после N пакетов |
| `--ip-id=zero` | Обнулять IP ID (для YouTube/Google — важно) |
| `--debug=0` (v2) | Уровень лога |
| `--chdir` (v2, добавляет service.bat) | Сменить рабочий каталог на каталог exe — чтобы `blobs.lua` нашёл `bin/*.bin` |
| `--name="…"` (v2) | Имя профиля |
| `--lua-init=@путь` (v2) | Загрузить Lua-файл; допускается inline-код: `--lua-init="zero256=string.rep(...)"` |
| `--blob=имя:0xHEX` (v2) | Определить бинарный blob инлайн |

### 2.5 Флаги, принимающие пути

`--hostlist`, `--hostlist-exclude`, `--ipset`, `--ipset-exclude`,
`--dpi-desync-fake-tls`, `--dpi-desync-fake-http`, `--dpi-desync-fake-quic`,
`--dpi-desync-fake-discord`, `--dpi-desync-fake-stun`, `--dpi-desync-fake-unknown`,
`--dpi-desync-fake-unknown-udp`, `--dpi-desync-split-seqovl-pattern`,
`--lua-init=@…` (v2).

**Важно для C#:** в bat все пути берутся как `"%~dp0bin\…"` / `"%~dp0lists\…"` и передаются
**в кавычках** внутри argv. В `ProcessStartInfo.Arguments` кавычки нужны обязательно — иначе winws
не примет путь с пробелами (README прямо требует ставить zapret в путь без пробелов/кириллицы).

---

## 3. Файлы списков (`lists/`)

| Файл | Тип | Формат | Происхождение | Обновление |
|---|---|---|---|---|
| `lists/list-general.txt` | hostlist (домены для обхода) | по одному домену в строке; `^dns.google` — «только точное совпадение», без `^` поддомены тоже матчатся | ручной, из репозитория Flowseal (59 строк: Cloudflare/Discord/7tv/live-video и т.п.) | с обновлением релиза |
| `lists/list-general-user.txt` | hostlist пользователя | тот же формат; создаётся автоматически с `# Never leave this file empty` + `domain.example.abc` | пользователь | вручную; в `.gitignore` |
| `lists/list-google.txt` | hostlist YouTube/Google | 22 строки: `youtube.com`, `googlevideo.com`, `ytimg.com`, `play.google.com`, `fonts.googleapis.com`, `www.gstatic.com`, `google.ru`… | репозиторий | с релизом |
| `lists/list-exclude.txt` | hostlist-exclude | 129 доменов: банки (сбер, ВТБ, Т-банк), маркетплейсы (ozon, WB), Яндекс/VK, Steam, Microsoft, Riot/Epic, госуслуги, античит-игры | репозиторий | с релизом |
| `lists/list-exclude-user.txt` | hostlist-exclude пользователя | домен в строке; создаётся с `domain.example.abc` | пользователь | вручную; `.gitignore` |
| `lists/ipset-all.txt` | ipset | CIDR/одиночный IP в строке. **В поставке = `203.0.113.113/32`** (RFC5737-заглушка) = режим `none` | заменяется командой `Update IPSet List` | `curl`/`Invoke-WebRequest` c `https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt` |
| `lists/ipset-all.txt.backup` | бэкап | реальный список, 572 927 байт (~24k подсетей) | тот же URL, скачанный ранее | создаётся при переключении IPSet Filter |
| `lists/ipset-exclude.txt` | ipset-exclude | 11 строк: `0.0.0.0/8`, `10.0.0.0/8`, `127.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `169.254.0.0/16`, `224.0.0.0/4`, `100.64.0.0/10`, `::1`, `fc00::/7`, `fe80::/10` | репозиторий | с релизом |
| `lists/ipset-exclude-user.txt` | ipset-exclude пользователя | CIDR; создаётся с `203.0.113.113/32` | пользователь | вручную; `.gitignore` |

**Трёхрежимный IPSet Filter** (`service.bat:1045-1107`, статус определяется *по содержимому файла*):
- `lineCount==0` → **`any`**: под фильтр попадает любой IP (агрессивно, ломает часть сайтов);
- содержит `203.0.113.113/32` → **`none`**: ipset не фильтруется;
- иначе → **`loaded`**: реальный список.

Переключение: `loaded → none` (переименовать в `.backup`, записать заглушку), `none → any` (обнулить файл),
`any → loaded` (удалить файл, восстановить из `.backup`).

**Прочие служебные файлы:**
- `utils/game_filter.enabled` — `mode=disabled|all|tcp|udp`, `tcp=<диапазон>`, `udp=<диапазон>` (по умолчанию `1024-65535`). В `.gitignore`.
- `utils/check_updates.enabled` — флаг авто-проверки обновлений (содержимое `ENABLED`).
- `utils/targets.txt` — цели для тестов: `KeyName = "https://host"` (HTTP+TLS+ping) или `KeyName = "PING:1.2.3.4"` (только ping).
- `utils/test zapret.ps1` — 41 КБ, движок тестирования стратегий.
- `.service/hosts` — 69 строк для hosts-файла: GitHub (`140.82.121.3 github.com`, `146.75.22.132 *.githubusercontent.com` + IPv6 `2606:50c0:8000::154`) и Telegram (`149.154.167.220 …`). Применяется **вручную** (блокнот + explorer).
- `.service/ipset-service.txt` — 572 927 байт, источник `ipset-all.txt`.
- `.service/version.txt` — `1.10.4`, сравнивается с `raw.githubusercontent.com/.../main/.service/version.txt`.

---

## 4. `service.bat` — что он делает

### 4.1 Меню (v1 и v2 идентичны, `v1:54-112`)

```
:: SERVICE      1.Install Service  2.Remove Services  3.Check Status
:: SETTINGS     4.Game Filter  5.IPSet Filter  6.Auto-Update Check  7.Replace active fakes
:: UPDATES      8.Update IPSet List  9.Update Hosts File  10.Check for Updates
:: TOOLS        11.Run Diagnostics  12.Run Tests
0.Exit
```

### 4.2 Elevation / UAC (`v1:35-51`)

```bat
if "%1"=="admin" (
    call :check_command chcp
    call :check_command find
    call :check_command findstr
    call :check_command netsh
    call :load_user_lists
    echo Started with admin rights
) else (
    call :check_extracted
    call :check_command powershell
    echo Requesting admin rights...
    powershell -NoProfile -Command "Start-Process 'cmd.exe' -ArgumentList '/c \"\"%~f0\" admin\"' -Verb RunAs"
    exit
)
```

Т.е. **всегда** `Start-Process -Verb RunAs` → новый `cmd.exe` с аргументом `admin`. Проверяется
наличие `chcp/find/findstr/netsh/powershell` в PATH и наличие папки `bin\`.

### 4.3 Установка службы (`v1:229-365`, `v2:262-320`)

v1: перечисление `*.bat` (кроме `service*`), сортировка с натуральным порядком цифр
(`[Regex]::Replace($_.Name,'(\d+)',{PadLeft(8,'0')})`), выбор → **парсинг bat-файла**
(извлекаются все строки начиная с той, где встречается `winws.exe`, снимается `^`-континуация,
разворачиваются кавычки, склеиваются `--opt value` для `sni host altorder`), затем:

```bat
net stop zapret >nul 2>&1
sc delete zapret >nul 2>&1
sc create zapret binPath= "\"%BIN_PATH%winws.exe\" <ARGS>" DisplayName= "zapret" start= auto
sc description zapret "Zapret DPI bypass software"
sc start zapret
reg add "HKLM\System\CurrentControlSet\Services\zapret" /v zapret-discord-youtube /t REG_SZ /d "<имя bat>" /f
```

v2: то же, но `winws2.exe` и **`set "args= --chdir"`** — в binPath попадает `--chdir` как первый аргумент
(чтобы `blobs.lua` нашёл `bin/*.bin` относительно exe), а все кавычки просто экранируются как `\"`.
Парсер в v2 проще: `set "temp_args=!line:%QUOTE%=\%QUOTE%!"`, машинерия `mergeargs` удалена.

Имя выбранной стратегии хранится в реестре и показывается в заголовке меню (`:get_strategy_name`).

### 4.4 Удаление (`v1:194-225`, v2 почти идентично)

```bat
sc query "zapret" >nul 2>&1
if !errorlevel!==0 ( net stop zapret & sc delete zapret )
tasklist /FI "IMAGENAME eq winws.exe" | find /I "winws.exe" > nul
if !errorlevel!==0 ( taskkill /IM winws.exe /F > nul )
sc query "WinDivert" >nul 2>&1
if !errorlevel!==0 ( net stop "WinDivert" & sc delete "WinDivert" )
net stop "WinDivert14" >nul 2>&1
sc delete "WinDivert14" >nul 2>&1
```

v2 дополнительно убивает **оба** процесса: `taskkill /IM winws.exe /IM winws2.exe /F`.

### 4.5 Связь «служба ↔ `--wf-*`»

Служба — это просто `sc create` с **той же командной строкой winws**, что в bat. WinDivert-драйвер
не регистрируется отдельно: `winws.exe` сам создаёт/стартует службу `WinDivert` (kernel driver)
при первом старте и оставляет её после выхода. Отсюда пункт «Remove Services» удаляет **и** `zapret`, **и**
`WinDivert`/`WinDivert14`. `--wf-tcp/--wf-udp` — это параметры WinDivert-фильтра, они попадают в binPath
службы как обычные аргументы; отдельного «service-режима» у winws нет.

`sc create` с `start= auto` ⇒ обход стартует до логина, без GUI и без консольного окна.
Ограничение: при установленной службе `general*.bat` не запускаются (см. `:test_service ... soft`).

### 4.6 Прочее в service.bat

- `:tcp_enable` — `netsh interface tcp show global` → если `timestamps` не `enabled`, то
  `netsh interface tcp set global timestamps=enabled`. Вызывается и при `status_zapret`, и перед `sc create`.
  **Без TCP timestamps техники с `--dpi-desync-fooling=ts` / `:tcp_ts` не работают.**
- `:replace_active_fakes` — SHA256-сравнение `ACTIVE_DISCORD_UDP.bin`/`ACTIVE_GAME_UDP.bin` со всеми
  `*.bin` в `bin\`, затем `del`+`copy` выбранного файла поверх активного (подмена фейка без правки bat).
- `:ipset_update` — `curl --ssl-no-revoke -L -f -o lists\ipset-all.txt <url>`; если curl старый —
  `--ssl-revoke-best-effort`; если curl нет — PowerShell `Invoke-WebRequest` + `Out-File -Encoding UTF8`.
- `:hosts_update` — скачивание `.service/hosts` в `%TEMP%\zapret_hosts.txt`, сверка первой и последней
  строки с `%SystemRoot%\System32\drivers\etc\hosts`, при расхождении `start notepad` + `explorer /select`.
  **Автоматической записи в hosts нет** — только руками.
- `:service_check_updates` — `Invoke-WebRequest` к `.service/version.txt`, сравнение с `LOCAL_VERSION`,
  при отличии `start "" <releases/latest>`.

---

## 5. Различия v1 vs zapret2 (самое важное)

### 5.1 Бинарники и структура

| | v1 `zapret-discord-youtube` | zapret2 `snanvl-zapret2` |
|---|---|---|
| exe | `bin\winws.exe` (203 776 Б) | `bin\winws2.exe` (656 384 Б) |
| Lua | нет | `lua\blobs.lua` (3 226 Б), `lua\zapret-lib.lua` (96 453 Б), `lua\zapret-antidpi.lua` (53 116 Б) |
| Движок desync | встроенный C | Lua-функции, регистрируемые из `zapret-antidpi.lua` |
| Документация флагов | `bol-van/zapret/docs/readme.md#nfqws` | `bol-van/zapret2/docs/manual.md` (полный список опций) |
| `WinDivert.dll` / `WinDivert64.sys` | те же | те же (идентичные размеры 47 616 / 94 144) |
| `cygwin1.dll` | 2 954 293 Б | тот же |
| `bin/*.bin` | 22 файла | те же 22 файла, байт-в-байт по размерам |

### 5.2 Новые флаги (только v2)

`--debug=0`, `--wf-tcp-out=`, `--wf-udp-out=` (вместо `--wf-tcp/--wf-udp`), `--lua-init=@file`,
`--lua-init="<inline lua>"`, `--lua-desync=func:arg:arg`, `--name="…"`, `--payload=…`,
`--blob=name:0xHEX`, `--out-range=-nN`, `--chdir` (добавляется service.bat).

Исчезли из bat: `--dpi-desync=*`, `--dpi-desync-fooling=*`, `--dpi-desync-fake-*=*`,
`--dpi-desync-repeats`, `--dpi-desync-split-*`, `--dpi-desync-any-protocol`,
`--dpi-desync-cutoff`, `--dpi-desync-hostfakesplit-mod`, `--dpi-desync-fakedsplit-pattern`, `--ip-id`.
(Судя по комментариям в `zapret-antidpi.lua` — «nfqws1 : "--dpi-desync=fake"» — старый синтаксис
эмулируется Lua-функциями, но в этих bat он не используется.)

### 5.3 `lua/` — зачем

- **`blobs.lua`** — загрузчик фейковых payload'ов. Таблица `blobs` мапит *алиас → файл*:
  `ACTIVE_DISCORD→ACTIVE_DISCORD_UDP.bin`, `ACTIVE_GAME→ACTIVE_GAME_UDP.bin`,
  `quic_google→quic_initial_www_google_com.bin`, `quic_4pda`, `quic_5ka`, `quic_lemana`, `quic_rutube`,
  `quic_steam_chat`, `quic_steamcomm`, `quic_steamstatic`, `quic_tencent`,
  `stun→stun.bin`, `stun2→stun2.bin`,
  `tls_4pda`, `tls_5ka`, `tls_awswaf`, `tls_onetrust→tls_clienthello_max_ru.bin`,
  `tls_sochi_park`, `tls_steam_chat`, `tls_steamstatic`, `tls_teamspeak`,
  `tls_google→tls_clienthello_www_google_com.bin`, `tls_sferum→tls_clienthello_www_sferum_ru.bin`.
  Ищет файлы в `""`, `bin/`, `$EXEDIR/`, `$EXEDIR/bin/`; при отсутствии любого — `error()`
  (т.е. **winws2 не стартует, если хотя бы один .bin отсутствует**). Пишет в лог `blobs.lua: N blobs loaded`.
- **`zapret-lib.lua`** — библиотека примитивов: разбор/сборка пакетов, `apply_fooling`
  (`tcp_seq`, `tcp_ack`, `tcp_flags_set/unset`, `tcp_nop_del`, `tcp_ts`, `tcp_md5`, `tcp_ts_up`,
  `ip6_hopbyhop`, `ip6_destopt`, `ip6_routing`, `ip6_ah`, `ip_ttl`, `ip_autottl`, `badsum`, `fool`),
  `apply_ip_id` (`seq|rnd|zero|none`), rawsend, ipfrag, autottl-кэш.
- **`zapret-antidpi.lua`** — сами техники: `drop`, `send` (`delay=`), `pktmod`, `http_domcase`,
  `http_hostcase`, `http_methodeol`, `http_unixeol`, `synack_split` (`mode=syn|synack|acksyn`),
  `synack`, `wsize`, `wssize`, `tls_client_hello_clone`, `syndata`, `rst` (`rstack`),
  `fake` (`blob`, `optional`, `tls_mod`), `multisplit` (`pos`, `seqovl`, `seqovl_pattern`, `blob`, `nodrop`),
  `multidisorder`, `multidisorder_legacy`, `hostfakesplit` (`host`, `midhost`, `nofake1/2`,
  `disorder_after`), `fakedsplit`, `fakeddisorder`, `oob`, `udplen`.

Практический вывод: **v2 — это «desync как код»**. Добавить новую технику = положить `.lua`, а не ждать
нового бинарника. Для GUI это означает: набор аргументов нельзя валидировать по фиксированному списку
флагов — `--lua-desync=` принимает произвольное имя функции.

### 5.4 Изменённые умолчания и поведение

| Аспект | v1 | v2 |
|---|---|---|
| Направление WinDivert | `--wf-tcp`/`--wf-udp` (in+out) | `--wf-tcp-out`/`--wf-udp-out` (только out) |
| Имена профилей | нет | `--name=` у каждого профиля (важно для логов) |
| Порядок | `--wf-*` затем профили | `--blob` → `--lua-init`×3 → профили |
| `--ip-id=zero` | отдельный флаг правила | параметр техники `:ip_id=zero` |
| `--dpi-desync-cutoff=nN` | отдельный флаг | `--out-range=-nN` (уровень профиля, до `--payload`) |
| «любой протокол» | `--dpi-desync-any-protocol=1` | `--payload=all` |
| fooling `ts` | `--dpi-desync-fooling=ts` | `:tcp_ts=-600000` (сдвиг TS) |
| fooling `badseq N` | `--dpi-desync-fooling=badseq --dpi-desync-badseq-increment=N` | `:tcp_seq=N` (часто с `:tcp_ack=-66000`) |
| `EXP` QUIC-правило | `--filter-l7=quic` | `--filter-l7=quic` + `--payload=quic_initial` |
| `ALT5` доп. правило | имя отсутствует | `--name="TCP Bypass"` |
| service binPath | `winws.exe <args>` | `winws2.exe --chdir <args>` |
| service arg-парсер | сложный (mergeargs, `sni host altorder`) | простой (замена кавычек) |
| Убийство процессов при Remove | `winws.exe` | `winws.exe` **и** `winws2.exe` |
| `Check Status` | ищет `winws.exe` | ищет `winws2.exe` |
| Тесты (`test zapret.ps1`) | `Get-Process winws` | `Get-Process winws, winws2`; снимок/восстановление `winws2.exe` через `Win32_Process` |

Списки, `.service/`, `utils/targets.txt`, `bin/*.bin`, `WinDivert*` — **идентичны** в обоих репо
(README отличается только упоминаниями zapret2/lua/manual.md; `service.bat` — только блоками из §5.4).

---

## 6. Admin/UAC, WinDivert, очистка, удаление

**Права.** Любой `general*.bat` и `service.bat` требуют администратора: winws грузит kernel-драйвер
WinDivert (`sc create/start WinDivert`), `service.bat` делает `sc create`, `netsh`, пишет в
`HKLM\System\CurrentControlSet\Services\zapret`. `service.bat` сам запрашивает UAC (§4.2).
`general*.bat` UAC **не** запрашивает — если запустить без прав, winws молча не стартует
(README: «После запуска скрипта general* ничего не происходит»).

**WinDivert.**
- Файлы: `bin\WinDivert.dll` (47 616 Б) + `bin\WinDivert64.sys` (94 144 Б) — из `zapret-win-bundle`.
- Драйвер регистрируется как служба `WinDivert` при первом старте winws; имя `WinDivert14` —
  артефакт старых версий (удаляется «на всякий случай»).
- Для Windows 7 нужна подпись — README: заменить `WinDivert.dll`/`WinDivert64.sys` на версии из
  `zapret-win-bundle/win7`.
- Античит: `bol-van/zapret-win-bundle/tree/master/windivert-hide`.

**Очистка/удаление (полный сценарий из README):**
1. `service.bat` → `Remove Services` (удаляет `zapret`, `winws.exe`/`winws2.exe`, `WinDivert`, `WinDivert14`);
2. `service.bat` → `Run Diagnostics`, устранить ошибки, в конце `Y` (чистка кэша Discord);
3. удалить папку zapret;
4. если `WinDivert` остался: `driverquery | find "Divert"`, затем `sc stop <имя>` + `sc delete <имя>`.

**«Осиротевший» WinDivert** (`v1:599-652`): если `winws.exe` не запущен, а служба `WinDivert`
в состоянии `RUNNING|STOP_PENDING` — Diagnostics сам делает `net stop`+`sc delete`, при неудаче ищет
конфликтующую службу `GoodbyeDPI`, удаляет её и повторяет попытку.

**Конфликтующие службы, которые удаляются по подтверждению** (`v1:654-698`):
`GoodbyeDPI`, `discordfix_zapret`, `winws1`, `winws2` + затем `WinDivert`, `WinDivert14`.
(Заметьте: `winws2` — это имя *службы* старого zapret2-форка, а не процесса.)

**Сброс сети, если ничего не помогает** (README): `netsh winsock reset`, `netsh int ip reset all`,
`netsh winhttp reset proxy`, `ipconfig /flushdns`, перезагрузка.

---

## 7. Как определяют «работает» + известные ограничения

### 7.1 `Check Status` (пункт 3)

`sc query zapret` + чтение `HKLM\...\Services\zapret\zapret-discord-youtube` (имя стратегии),
`sc query WinDivert`, наличие `bin\*.sys`, `tasklist /FI "IMAGENAME eq winws.exe"` →
«Bypass (winws.exe) is RUNNING / NOT running».

### 7.2 `Run Diagnostics` (пункт 11) — 12 проверок

| Проверка | Как | Реакция |
|---|---|---|
| Base Filtering Engine | `sc query BFE \| findstr RUNNING` | красный: «required for zapret to work» |
| Системный прокси | `HKCU\...\Internet Settings\ProxyEnable/ProxyServer` | жёлтый: «make sure it's valid» |
| TCP timestamps | `netsh interface tcp show global` | жёлтый + **авто-включение** `timestamps=enabled` |
| Adguard | `tasklist AdguardSvc.exe` | красный (issue #417) |
| Killer | `sc query \| findstr Killer` | красный (issue #2512) |
| Intel Connectivity Network Service | `sc query \| Intel+Connectivity+Network` | красный (GoodbyeDPI #541) |
| Check Point | `TracSrvWrapper`, `EPWD` | красный: «try to uninstall» |
| SmartByte | `sc query \| SmartByte` | красный |
| Кириллица в пути | PowerShell regex `[\u0430-\u044F…]` на `%~dp0` | жёлтый: перенести в `C:\zapret` |
| OneDrive | `%OneDrive%` в пути | красный |
| `WinDivert64.sys` | `if not exist bin\*.sys` | красный |
| VPN | `sc query \| VPN\|HappService` | жёлтый |
| Secure DNS (DoH) | PowerShell обход `HKLM:\...\Dnscache\InterfaceSpecificParameters\` с `DohFlags > 0` | жёлтый: настроить Secure DNS |
| hosts с youtube.com/youtu.be | `findstr` по `%SystemRoot%\System32\drivers\etc\hosts` | жёлтый |
| Осиротевший WinDivert | §6 | авто-удаление |
| Конфликтующие bypass-службы | §6 | предложение удалить |
| Кэш Discord | `rd /s /q` для `Cache`, `Code Cache`, `GPUCache` в `%APPDATA%\discord[ptb|canary|development]` (сначала `taskkill`) | по умолчанию `Y` |

### 7.3 `Run Tests` (пункт 12) → `utils\test zapret.ps1`

Требует **Administrator** + `curl.exe` в PATH + PowerShell ≥ 3. Отказывается работать, если установлена
служба `zapret` (`Test-ZapretServiceConflict`). Логика:

1. `Get-IpsetStatus` (по содержимому `ipset-all.txt`); если не `any` — **временно** очищает файл
   (бэкап в `ipset-all.test-backup.txt`, флаг `ipset_switched.flag`) и предупреждает, что при закрытии
   окна крестиком ipset не восстановится сразу (восстановится при следующем запуске).
2. Снимает «снимок» работающих `winws`/`winws2` (`Win32_Process` → CommandLine) и потом восстанавливает.
3. Для **каждого** `general*.bat`: `Stop-Zapret` → `Start-Process cmd /c <bat>` →
   `Wait-WinwsReady` (до 5 с, ждёт процесс `winws`/`winws2`, потом 300 мс) → тесты →
   `Stop-Process` bat-процесса.
4. `Standard tests`: для каждой цели из `utils/targets.txt` — `curl -I -s -m 4 --connect-timeout 2 -o NUL -w "%{http_code}"`
   в трёх вариантах: `--http1.1`, `--tlsv1.2 --tls-max 1.2`, `--tlsv1.3 --tls-max 1.3`;
   классификация `OK / ERROR / SSL / UNSUP`; плюс `Ping` (таймаут 1000 мс) для `PING:`-целей.
   Параллельность — runspace pool, `min(16, max(8, CPU*2))`.
5. `DPI checkers`: `https://hyperion-cs.github.io/dpi-checkers/ru/tcp-16-20/suite.v2.json`,
   POST `--data-binary` 64 КБ случайных байт с `--range 0-65535`, детект «TCP 16-20 freeze»;
   при обнаружении — «Consider changing strategy/SNI/IP».
6. Результаты пишутся в `utils\test results` (в `.gitignore`), в конце — восстановление ipset и
   перезапуск ранее работавших winws.
7. Переопределения через env: `MONITOR_HOST`, `MONITOR_TIMEOUT`, `MONITOR_RANGE`,
   `MONITOR_MAX_PARALLEL`, `TEST_CURL_TIMEOUT`, `TEST_MAX_PARALLEL`.

### 7.4 Документированные ограничения и подводные камни

- **Антивирусы.** WinDivert детектится как `Not-a-virus:RiskTool.Multi.WinDivert` / `WinDivert`.
  Решение: исключение на папку либо отключение детекта PUA (README, цитата из `zapret-win-bundle`).
- **Стратегии «выгорают».** README прямо: стратегия может работать, а потом перестать — из-за
  обнаружения DPI. Отсюда 22 варианта и «пробуйте все».
- **Secure DNS обязателен.** Chrome/Firefox DoH или Windows 11 «Encrypted DNS». Без него YouTube
  может не открываться даже при работающем обходе. Keenetic: включить «Транзит запросов».
- **Порядок установки.** Разблокировать архив (свойства → «Разблокировать»), распаковать в путь
  **без кириллицы/спецсимволов/пробелов** (README: `C:\zapret`), иначе winws не стартует.
- **Конфликты.** Killer, Intel Connectivity Network Service, Check Point (TracSrvWrapper/EPWD),
  SmartByte, Adguard, GoodbyeDPI, любые VPN (в т.ч. HappService), другой экземпляр zapret.
  `STOP_PENDING` у службы = конфликт с другим обходом.
- **`ipset any`** — ломает часть сайтов; не использовать постоянно. Для игр: обновить ipset + включить
  Game Filter, при необходимости `ipset any`, но правильнее добавить IP в `ipset-all.txt`,
  домены — в `list-general-user.txt`; при высоком пинге — наоборот, в `*-exclude-user.txt`.
- **Game Filter / IPSet Filter меняют смысл всех bat** — после переключения нужен перезапуск стратегии.
- **MTU** — в этих репозиториях **не** настраивается и не упоминается (в отличие от `--dpi-desync-*`
  документации bol-van). Косвенно связано с `--dpi-desync-split-seqovl` (перекрытие 480–681 байт).
- **Telegram / Spotify** — не решаются zapret'ом (README: `tg-ws-proxy`, MTProto-прокси; Spotify
  заблокировал РФ сам).
- **Windows 7** — нужен подписанный WinDivert из `zapret-win-bundle/win7`.
- **Один экземпляр.** `status_zapret` блокирует `general*.bat`, если служба уже RUNNING;
  `Remove Services` перед ручными тестами обязателен (то же проверяет `test zapret.ps1`).
