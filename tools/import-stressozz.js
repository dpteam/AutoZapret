#!/usr/bin/env node
'use strict';

/*
 * Конвертер стратегий StressOzz/Zapret-Manager в наш каталог стратегий.
 *
 * ЗАЧЕМ
 *   У StressOzz 73 стратегии (Yv01–Yv35, v1–v10, Dv1–Dv17, gVideo1–gVideo11),
 *   проверенные на живых провайдерах. У нас было 22 (Flowseal). Это набор
 *   другого автора, а не замена: у разных DPI выигрывают разные комбинации,
 *   и чем шире каталог, тем выше шанс, что автоподбор найдёт рабочую.
 *
 * ПОЧЕМУ НЕ ПРОСТО СКОПИРОВАТЬ
 *   Файлы StressOzz написаны для Linux: пути /opt/zapret/... и запуск через
 *   iptables/NFQUEUE. Нам нужны плейсхолдеры ${LISTS}/${BIN} и глобальные
 *   --wf-tcp/--wf-udp, без которых winws не увидит ни одного пакета.
 *
 * ЧЕСТНОСТЬ ЗАМЕН
 *   Два фейка из набора отсутствуют во всех известных бандлах. Подменять их
 *   молча нельзя: стратегия «работала бы» с чужим содержимым, и виноватым
 *   выглядел бы движок. Поэтому каждая подмена печатается в отчёт, а в
 *   описании стратегии стоит пометка.
 *
 * Запуск:
 *   node tools/import-stressozz.js [--write]
 *   без --write только показывает, что было бы сделано.
 */

const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const srcDir = path.join(root, 'refs', 'Zapret-Manager', 'files');
const target = path.join(root, 'assets', 'strategies', 'strategies.v1.json');

const write = process.argv.includes('--write');

/* ------------------------------------------------------------------ карты путей */

// Списки. Оба имени — из набора StressOzz; у нас они называются иначе,
// но смысл тот же, поэтому соответствие однозначное.
const LIST_MAP = {
    'zapret-hosts-google.txt': '${LISTS}/list-google.txt',
    'zapret-hosts-user-exclude.txt': '${LISTS}/list-exclude-user.txt',
    'zapret-hosts-user.txt': '${LISTS}/list-general-user.txt',
    'zapret-hosts.txt': '${LISTS}/list-general.txt',
    'zapret-ip.txt': '${LISTS}/ipset-all.txt',
    'zapret-ip-exclude.txt': '${LISTS}/ipset-exclude.txt',
    'zapret-ip-user.txt': '${LISTS}/ipset-all.txt',
    'zapret-ip-user-exclude.txt': '${LISTS}/ipset-exclude-user.txt'
};

// Фейки, которых нет ни в одном бандле. Подмена — с пометкой.
const FAKE_SUBSTITUTE = {
    // В наборе StressOzz файла 4pda.bin нет вовсе (ссылка «на будущее»).
    // У нас есть ClientHello того же домена, снятый с Flowseal.
    '4pda.bin': 'tls_clienthello_4pda_to.bin',
    // Вариант того же ClientHello www.google.com, просто на 36 байт длиннее.
    // SNI совпадает, поведение для DPI то же.
    'tls_clienthello_www_google_com_2.bin': 'tls_clienthello_www_google_com.bin'
};

const substitutions = [];

function mapPath(p) {
    const dir = p.replace(/\\/g, '/');

    const listMatch = dir.match(/^\/opt\/zapret\/ipset\/(.+)$/);
    if (listMatch) {
        const mapped = LIST_MAP[listMatch[1]];
        return mapped === undefined ? null : mapped;
    }

    const fakeMatch = dir.match(/^\/opt\/zapret\/files\/fake\/(.+)$/);
    if (fakeMatch) {
        const name = fakeMatch[1];
        const sub = FAKE_SUBSTITUTE[name];

        if (sub !== undefined) {
            substitutions.push(name + ' -> ' + sub);
            return '${BIN}/' + sub;
        }

        return '${BIN}/' + name;
    }

    return null;
}

/* ------------------------------------------------------------------ разбор файла */

function parseFile(file) {
    const text = fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n');
    const lines = text.split('\n');

    const sections = [];
    let current = null;

    for (const raw of lines) {
        const line = raw.trim();

        if (line.length === 0) continue;

        if (line.startsWith('#')) {
            current = { name: line.slice(1).trim(), args: [] };
            sections.push(current);
            continue;
        }

        if (!current) continue;

        // Комментарии внутри секции тоже начинаются с '#' — сюда не попадут.
        current.args.push(line);
    }

    return sections;
}

/* ------------------------------------------------------------------ порты */

function portsOf(args, option) {
    const out = new Set();

    for (const a of args) {
        if (!a.startsWith(option + '=')) continue;

        const value = a.slice(option.length + 1);

        for (const part of value.split(',')) {
            const t = part.trim();
            if (!t) continue;

            // Диапазон «19294-19344» winws принимает как есть.
            out.add(t);
        }
    }

    return [...out];
}

/* ------------------------------------------------------------------ стратегия */

function buildStrategy(section, meta) {
    const args = [];
    let dropped = 0;

    for (const a of section.args) {
        // Опция, склеенная со значением через ПРОБЕЛ, — это не одна опция.
        //
        // В исходниках StressOzz встречается `--dpi-desync-autottl 2:2-12`
        // (вместо `=`). Мы передаём аргументы массивом, поэтому такая строка
        // уходит в winws одним куском, он видит имя опции
        // `--dpi-desync-autottl 2:2-12`, не находит его и отказывается
        // стартовать. Стратегия выглядит «нерабочей», а причина — пробел.
        const fixed = a.startsWith('--') && a.indexOf(' ') > 0 && a.indexOf('=') < 0
            ? a.replace(/^(\S+)\s+(\S.*)$/, '$1=$2')
            : a;

        // Путь встречается как значение опции: --hostlist=/opt/...
        const eq = fixed.indexOf('=');
        if (eq > 0) {
            const value = fixed.slice(eq + 1);

            if (value.startsWith('/opt/zapret/')) {
                const mapped = mapPath(value);

                if (mapped === null) {
                    dropped++;
                    continue;
                }

                args.push(fixed.slice(0, eq + 1) + mapped);
                continue;
            }
        }

        args.push(fixed);
    }

    const tcp = portsOf(args, '--filter-tcp');
    const udp = portsOf(args, '--filter-udp');

    // Глобальные аргументы — это не «оптимизация», а условие работы: winws
    // перехватывает только те порты, что перечислены в фильтре WinDivert.
    // Без них стратегия стартует и не видит ни одного пакета.
    const globalArgs = [];

    if (tcp.length > 0) globalArgs.push('--wf-tcp=' + tcp.join(','));
    if (udp.length > 0) globalArgs.push('--wf-udp=' + udp.join(','));

    const label = (tcp.length > 0 ? 'tcp:' + tcp.join(',') : '') +
                  (udp.length > 0 ? (tcp.length > 0 ? ' ' : '') + 'udp:' + udp.join(',') : '');

    return {
        id: 'stressozz.' + section.name,
        name: section.name,
        engine: 'v1',
        note: 'StressOzz · ' + meta.file + (dropped > 0 ? ' · выброшено опций: ' + dropped : ''),
        recommended: false,
        source: meta.file + '#' + section.name,
        targets: meta.targets,
        globalArgs: globalArgs,
        profiles: [
            {
                name: section.name,
                label: label,
                args: args
            }
        ]
    };
}

/* ------------------------------------------------------------------ main */

const FILES = [
    { file: 'StrYoutube', targets: ['youtube'] },
    { file: 'StrV', targets: ['general'] },
    { file: 'StrDiscord', targets: ['discord'] },
    { file: 'StrGVideo', targets: ['youtube', 'video'] }
];

const generated = [];

for (const f of FILES) {
    const full = path.join(srcDir, f.file);

    if (!fs.existsSync(full)) {
        console.error('нет файла: ' + full);
        process.exitCode = 1;
        continue;
    }

    for (const section of parseFile(full)) {
        generated.push(buildStrategy(section, f));
    }
}

if (generated.length === 0) {
    console.error('нечего импортировать');
    process.exit(1);
}

console.log('стратегий StressOzz: ' + generated.length);

const byFile = {};
for (const g of generated) {
    const k = g.source.split('#')[0];
    byFile[k] = (byFile[k] || 0) + 1;
}

for (const k of Object.keys(byFile)) console.log('  ' + k + ': ' + byFile[k]);

if (substitutions.length > 0) {
    console.log('подмены (файла нет ни в одном бандле):');
    for (const s of [...new Set(substitutions)]) console.log('  ' + s);
}

const catalog = JSON.parse(fs.readFileSync(target, 'utf8'));

const before = catalog.strategies.length;

// Убираем предыдущий импорт StressOzz, чтобы повторный запуск не плодил дубли.
catalog.strategies = catalog.strategies.filter(s => !s.id.startsWith('stressozz.'));

const seen = new Set(catalog.strategies.map(s => s.id));
let added = 0;

for (const g of generated) {
    if (seen.has(g.id)) {
        console.error('дубль id: ' + g.id);
        continue;
    }

    seen.add(g.id);
    catalog.strategies.push(g);
    added++;
}

catalog.strategies.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));

console.log('каталог: было ' + before + ', добавлено ' + added + ', стало ' + catalog.strategies.length);

if (!write) {
    console.log('(пробный прогон; добавьте --write, чтобы записать)');
    process.exit(0);
}

fs.writeFileSync(target, JSON.stringify(catalog, null, 4) + '\n', 'utf8');
console.log('записано: ' + target);
