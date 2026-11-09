using System;
using System.Collections.Generic;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Готовая техника обхода — то, что человек выбирает одним пунктом.
    ///
    /// Почему пресеты данными, а не `if (name == "youtube")`: тот же принцип,
    /// что и на десктопе (`assets/strategies/*.json`). Добавить новую
    /// комбинацию — значит добавить запись в таблицу, а не ветку в коде.
    ///
    /// Что здесь можно, а что нет. Мы работаем обычными сокетами без root,
    /// поэтому доступны только техники, которые выражаются через `send()`:
    ///
    ///   * разрезать ClientHello на куски — DPI видит обрывок;
    ///   * отправить кусок с TTL=1 — DPI видит его, сервер нет;
    ///   * пометить байт срочным (URG) — простые парсеры DPI теряют поток.
    ///
    /// Недоступно без нативного кода (нужен raw-сокет или подмена данных в
    /// очереди ядра): `fake` с подставным SNI, `badsum`, `md5sig`, `syndata`,
    /// произвольный `seq`. Это честная граница C# — см. `android/README.md`.
    /// </summary>
    public sealed class DesyncPreset
    {
        public string Id;
        public string Name;
        public string Hint;

        /// <summary>Позиции разреза внутри ClientHello. Пусто — не режем.</summary>
        public int[] Positions = new int[0];

        /// <summary>Сначала отправить последний кусок с TTL=1.</summary>
        public bool Disorder;

        /// <summary>Один байт отправить как срочный (URG).</summary>
        public bool Oob;

        /// <summary>Отправить подставной ClientHello с низким TTL.</summary>
        public bool Fake;

        /// <summary>TTL фейка. 8 проверено на живой сети.</summary>
        public int FakeTtl = 8;

        public static readonly DesyncPreset Off = new DesyncPreset
        {
            Id = "off",
            Name = "Без обхода",
            Hint = "только прокси, ничего не трогаем — для сравнения",
            Positions = new int[0]
        };

        public static readonly DesyncPreset Split1 = new DesyncPreset
        {
            Id = "split1",
            Name = "Разрез 1",
            Hint = "ClientHello уходит двумя сегментами: 1 байт и остальное",
            Positions = new[] { 1 }
        };

        public static readonly DesyncPreset Split2 = new DesyncPreset
        {
            Id = "split2",
            Name = "Разрез 1+5",
            Hint = "три сегмента: 1 / 4 / остальное. Чаще всего помогает YouTube",
            Positions = new[] { 1, 5 }
        };

        public static readonly DesyncPreset MultiSplit = new DesyncPreset
        {
            Id = "multisplit",
            Name = "Разрез 1+3+5",
            Hint = "четыре сегмента — когда двух мало",
            Positions = new[] { 1, 3, 5 }
        };

        public static readonly DesyncPreset DisorderPreset = new DesyncPreset
        {
            Id = "disorder",
            Name = "Disorder",
            Hint = "хвост ClientHello уходит первым с TTL=1, потом обычный разрез",
            Positions = new[] { 1 },
            Disorder = true
        };

        public static readonly DesyncPreset OobPreset = new DesyncPreset
        {
            Id = "oob",
            Name = "OOB",
            Hint = "разрез 1 плюс срочный байт: простые DPI теряют поток",
            Positions = new[] { 1 },
            Oob = true
        };

        public static readonly DesyncPreset DisorderOobPreset = new DesyncPreset
        {
            Id = "disorder-oob",
            Name = "Disorder + OOB",
            Hint = "самый шумный вариант, когда остальное не берёт",
            Positions = new[] { 1, 5 },
            Disorder = true,
            Oob = true
        };

        /// <summary>
        /// Единственная техника, которая прошла на живой сети МТС: 200 OK за
        /// 0,57 с там, где разрез и disorder давали таймаут.
        ///
        /// Механика: подставной ClientHello с безобидным именем уходит с TTL 8
        /// и до сервера не доходит, но DPI его видит и считает соединение
        /// проверенным. Настоящий ClientHello идёт следом и не разбирается.
        /// </summary>
        public static readonly DesyncPreset FakeTtl8 = new DesyncPreset
        {
            Id = "fake8",
            Name = "Фейк + TTL 8",
            Hint = "подставное рукопожатие с TTL 8. Прошло на живой сети там, где разрез не берёт",
            Fake = true,
            FakeTtl = 8
        };

        public static readonly DesyncPreset FakeSplit = new DesyncPreset
        {
            Id = "fake-split",
            Name = "Фейк + разрез",
            Hint = "фейк TTL 8, затем настоящий ClientHello двумя сегментами",
            Positions = new[] { 1 },
            Fake = true,
            FakeTtl = 8
        };

        public static readonly DesyncPreset FakeDisorder = new DesyncPreset
        {
            Id = "fake-disorder",
            Name = "Фейк + disorder",
            Hint = "фейк TTL 8, затем разрез в обратном порядке",
            Positions = new[] { 1 },
            Disorder = true,
            Fake = true,
            FakeTtl = 8
        };

        /// <summary>Порядок в интерфейсе — от мягкого к жёсткому.</summary>
        public static readonly DesyncPreset[] All = new[]
        {
            Off, Split1, Split2, MultiSplit, DisorderPreset, OobPreset, DisorderOobPreset,
            FakeTtl8, FakeSplit, FakeDisorder
        };

        public static DesyncPreset ById(string id)
        {
            if (!string.IsNullOrEmpty(id))
            {
                foreach (DesyncPreset preset in All)
                {
                    if (preset.Id == id) return preset;
                }
            }

            return Split1;
        }

        /// <summary>Перенести пресет в рабочую конфигурацию прокси.</summary>
        public void ApplyTo(DesyncConfig config)
        {
            if (config == null) return;

            config.Enabled = Id != Off.Id;
            config.Positions = Positions;
            config.Disorder = Disorder;
            config.Oob = Oob;
            config.Fake = Fake;
            config.FakeTtl = FakeTtl;
        }

        public static List<string[]> ToList()
        {
            List<string[]> rows = new List<string[]>();

            foreach (DesyncPreset preset in All)
            {
                rows.Add(new[] { preset.Id, preset.Name, preset.Hint });
            }

            return rows;
        }
    }
}
