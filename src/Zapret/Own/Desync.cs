using System;
using System.Collections.Generic;
using AutoZapret.Core;
using AutoZapret.Payloads;

namespace AutoZapret.Zapret.Own
{
    /// <summary>Что именно делает движок с ClientHello.</summary>
    public enum Technique
    {
        /// <summary>Ничего: только считать пакеты (режим наблюдения).</summary>
        Off = 0,

        /// <summary>Разрезать на две части в позиции pos.</summary>
        Split = 1,

        /// <summary>Разрезать в нескольких позициях (1, середина SNI и т.д.).</summary>
        MultiSplit = 2,

        /// <summary>Отправить подставной ClientHello со сдвинутым seq, потом настоящий.</summary>
        Fake = 3,

        /// <summary>Подставной ClientHello, затем настоящий разрезанный.</summary>
        FakeSplit = 4,

        /// <summary>Разрезать и отправить куски В ОБРАТНОМ порядке (сбивает сборку).</summary>
        MultiDisorder = 5,

        /// <summary>Разрезать ровно перед именем в SNI, чтобы DPI не прочитал хост.</summary>
        HostFakeSplit = 6
    }

    /// <summary>
    /// Набор параметров техники. Отдельный класс, чтобы CLI, интерфейс и
    /// будущий импорт стратегий winws задавали одно и то же, а не каждый своё.
    /// </summary>
    public sealed class TechniqueOptions
    {
        public Technique Kind = Technique.Split;

        /// <summary>Позиция разреза для Split.</summary>
        public int Position = 1;

        /// <summary>Позиции для MultiSplit. Пустой список — берём Position.</summary>
        public List<int> Positions = new List<int>();

        /// <summary>Сколько раз повторить фейк (в zapret это --dpi-desync-repeats).</summary>
        public int Repeats = 6;

        /// <summary>
        /// Сдвиг seq у фейка. Отрицательный — «уже принято», большой
        /// положительный — «вне окна». Именно это заставляет настоящего
        /// получателя выбросить фейк, а DPI — обработать.
        /// </summary>
        public int FakeSeqOffset = -66000;

        /// <summary>Испортить сумму у фейка (fooling badsum): DPI его не тронет.</summary>
        public bool BadSum;

        /// <summary>
        /// Насколько перекрыть куски (seqovl). Второй кусок уходит с seq,
        /// сдвинутым назад: получатель считает это повтором, а DPI видит
        /// перемешанный поток. 0 — без перекрытия.
        /// </summary>
        public int Overlap;

        /// <summary>Файл фейка (.bin). Пусто — генерируем сами.</summary>
        public string FakeFile = "";

        /// <summary>SNI для сгенерированного фейка.</summary>
        public string FakeSni = "www.google.com";

        public string Describe()
        {
            switch (Kind)
            {
                case Technique.Split: return "split pos=" + Position;
                case Technique.MultiSplit: return "multisplit pos=" + string.Join(",", Positions.ToArray());
                case Technique.MultiDisorder: return "multidisorder pos=" + string.Join(",", Positions.ToArray());
                case Technique.HostFakeSplit: return "hostfakesplit overlap=" + Overlap;
                case Technique.Fake: return "fake seq=" + FakeSeqOffset + (BadSum ? " badsum" : "") + " x" + Repeats;
                case Technique.FakeSplit: return "fakedsplit pos=" + Position + " seq=" + FakeSeqOffset;
                default: return "выключено";
            }
        }
    }

    /// <summary>
    /// Техники desync, применяемые к одному ClientHello.
    ///
    /// Отдельный класс от OwnEngine намеренно: движок отвечает за перехват и
    /// отправку, техники — только за то, «какие пакеты отправить». Так каждую
    /// технику можно проверить на синтетическом пакете, без драйвера и прав,
    /// и добавление новой не трогает конвейер.
    ///
    /// Все техники возвращают СПИСОК пакетов: движок отправляет их по порядку
    /// и отбрасывает оригинал. Пустой список означает «не смог — отправь как есть».
    /// </summary>
    public static class Desync
    {
        /// <summary>Один готовый пакет к отправке.</summary>
        public sealed class Outgoing
        {
            public byte[] Data;
            public int Length;
            public string Note = "";
        }

        /// <summary>Подставной ClientHello: из файла или сгенерированный.</summary>
        public static byte[] FakePayload(TechniqueOptions options, out string source)
        {
            source = "";

            if (options != null && !string.IsNullOrEmpty(options.FakeFile))
            {
                try
                {
                    if (System.IO.File.Exists(options.FakeFile))
                    {
                        byte[] fromFile = System.IO.File.ReadAllBytes(options.FakeFile);

                        if (fromFile.Length >= 40)
                        {
                            source = "файл " + System.IO.Path.GetFileName(options.FakeFile);
                            return fromFile;
                        }

                        Log.Warn("OWN", "фейк из файла слишком короткий: " + fromFile.Length + " байт");
                    }
                    else
                    {
                        Log.Warn("OWN", "файл фейка не найден: " + options.FakeFile);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("OWN", "фейк из файла: " + ex.Message);
                }
            }

            // Генератор уже есть и проверен: настоящий Chrome-hello с валидным
            // key_share. Без него фейк выглядел бы как мусор и DPI его отбросил
            // бы как «не TLS», что нам не нужно.
            GeneratedFake generated = FakeGenerator.TlsClientHello(
                options == null ? "www.google.com" : options.FakeSni);

            if (generated != null && generated.Valid && generated.Data != null && generated.Data.Length >= 40)
            {
                source = "сгенерирован (" + generated.Sni + ", " + generated.Size + " Б)";
                return generated.Data;
            }

            source = "не удалось";
            return null;
        }

        /// <summary>
        /// Построить пакеты для одной техники. Пустой список — «ничего не делай».
        /// </summary>
        public static List<Outgoing> Build(TcpPacket tcp, TechniqueOptions options, out string error)
        {
            List<Outgoing> result = new List<Outgoing>();
            error = "";

            if (tcp == null || options == null) { error = "нет пакета"; return result; }
            if (options.Kind == Technique.Off) return result;
            if (!tcp.HasPayload) { error = "нет данных"; return result; }

            switch (options.Kind)
            {
                case Technique.Split:
                    SplitInto(tcp, result, new List<int> { Clamp(options.Position, tcp.PayloadLength) });
                    break;

                case Technique.MultiSplit:
                {
                    List<int> positions = NormalizePositions(tcp, options);
                    positions.Sort();
                    SplitInto(tcp, result, positions);
                    break;
                }

                case Technique.MultiDisorder:
                {
                    // Те же куски, что у multisplit, но отправляем с конца.
                    // Получатель соберёт поток по seq, а DPI, который смотрит
                    // по порядку прихода, увидит середину раньше начала.
                    List<int> positions = NormalizePositions(tcp, options);
                    positions.Sort();

                    List<Outgoing> ordered = new List<Outgoing>();
                    SplitInto(tcp, ordered, positions);

                    for (int i = ordered.Count - 1; i >= 0; i--) result.Add(ordered[i]);
                    break;
                }

                case Technique.HostFakeSplit:
                {
                    // Режем ровно перед именем в SNI: DPI, который ищет хост
                    // в одном сегменте, его не найдёт. Если имя найти не удалось
                    // (не ClientHello с SNI) — режем как обычный split, иначе
                    // техника молча ничего бы не делала.
                    int sniOffset = FindSniOffset(tcp);

                    if (sniOffset <= 0 || sniOffset >= tcp.PayloadLength)
                    {
                        sniOffset = Clamp(options.Position, tcp.PayloadLength);
                    }

                    int overlap = Math.Max(0, Math.Min(1400, options.Overlap));

                    result.Add(Segment(tcp, 0, sniOffset, 0, "до SNI"));

                    // Второй кусок сдвинут назад на overlap: получатель считает
                    // это повтором уже принятого, DPI видит перекрытие.
                    result.Add(Segment(tcp, sniOffset, tcp.PayloadLength, sniOffset - overlap,
                                      "SNI и далее" + (overlap > 0 ? " (seqovl " + overlap + ")" : "")));
                    break;
                }

                case Technique.Fake:
                    if (!AddFake(tcp, options, result, error)) return new List<Outgoing>();

                    // после фейка настоящий уходит целиком
                    result.Add(Segment(tcp, 0, tcp.PayloadLength, 0, "настоящий"));
                    break;

                case Technique.FakeSplit:
                {
                    if (!AddFake(tcp, options, result, error)) return new List<Outgoing>();

                    int pos = Clamp(options.Position, tcp.PayloadLength);

                    result.Add(Segment(tcp, 0, pos, 0, "часть 1"));
                    result.Add(Segment(tcp, pos, tcp.PayloadLength, pos, "часть 2"));
                    break;
                }
            }

            if (result.Count == 0) error = "техника ничего не дала";

            return result;
        }

        private static bool AddFake(TcpPacket tcp, TechniqueOptions options,
                                    List<Outgoing> result, string errorOut)
        {
            string source;
            byte[] payload = FakePayload(options, out source);

            if (payload == null)
            {
                return false;
            }

            int repeats = Math.Max(1, Math.Min(16, options.Repeats));
            int length;

            // Фейк собираем один раз: заголовки и данные одинаковые, отличается
            // только момент отправки. Пересобирать на каждый повтор — лишняя работа
            // в горячем пути.
            byte[] fake = tcp.BuildWithPayload(payload, options.FakeSeqOffset, out length);

            if (options.BadSum) TcpPacket.CorruptTcpChecksum(fake, tcp.TcpOffset);

            for (int i = 0; i < repeats; i++)
            {
                result.Add(new Outgoing
                {
                    Data = fake,
                    Length = length,
                    Note = "фейк " + (i + 1) + "/" + repeats + " (" + source + ")"
                });
            }

            return true;
        }

        /// <summary>Разрезать payload в заданных позициях: N+1 сегмент.</summary>
        private static void SplitInto(TcpPacket tcp, List<Outgoing> result, List<int> positions)
        {
            int from = 0;

            for (int i = 0; i < positions.Count; i++)
            {
                int to = positions[i];
                if (to <= from) continue;

                result.Add(Segment(tcp, from, to, from, "часть " + (i + 1)));
                from = to;
            }

            if (from < tcp.PayloadLength)
                result.Add(Segment(tcp, from, tcp.PayloadLength, from,
                                   "часть " + (result.Count + 1)));
        }

        private static Outgoing Segment(TcpPacket tcp, int from, int to, int seqAdjust, string note)
        {
            int length;
            byte[] data = tcp.BuildSegment(from, to, seqAdjust, out length);

            return new Outgoing { Data = data, Length = length, Note = note };
        }

        /// <summary>Позиции для multisplit/multidisorder: свои или одна по умолчанию.</summary>
        private static List<int> NormalizePositions(TcpPacket tcp, TechniqueOptions options)
        {
            List<int> positions = new List<int>();

            if (options.Positions != null && options.Positions.Count > 0)
            {
                foreach (int p in options.Positions)
                {
                    int clamped = Clamp(p, tcp.PayloadLength);
                    if (!positions.Contains(clamped)) positions.Add(clamped);
                }
            }
            else
            {
                positions.Add(Clamp(options.Position, tcp.PayloadLength));
            }

            return positions;
        }

        /// <summary>
        /// Смещение имени хоста внутри payload, если это ClientHello с SNI.
        ///
        /// Нужно для hostfakesplit: резать надо именно перед именем, а не «где-то
        /// в начале». Разбор повторяет тот же путь, что PayloadValidator.ExtractSni,
        /// но возвращает не строку, а позицию — иначе некуда резать.
        /// </summary>
        public static int FindSniOffset(TcpPacket tcp)
        {
            if (tcp == null || !tcp.HasPayload) return -1;

            byte[] data = tcp.Buffer;
            int start = tcp.PayloadOffset;
            int length = tcp.PayloadLength;

            if (data == null || length < 43) return -1;

            try
            {
                int pos = start + 5 + 4;              // record header + handshake header
                pos += 2 + 32;                        // version + random

                int sessionLen = data[pos];
                pos += 1 + sessionLen;

                int cipherLen = (data[pos] << 8) | data[pos + 1];
                pos += 2 + cipherLen;

                int compLen = data[pos];
                pos += 1 + compLen;

                int extTotal = (data[pos] << 8) | data[pos + 1];
                pos += 2;

                int end = Math.Min(pos + extTotal, start + length);

                while (pos + 4 <= end)
                {
                    int extType = (data[pos] << 8) | data[pos + 1];
                    int extLen = (data[pos + 2] << 8) | data[pos + 3];
                    pos += 4;

                    if (extType == 0x0000 && pos + extLen <= start + length)
                    {
                        int p = pos + 2;
                        if (p + 3 > start + length) return -1;

                        int nameLen = (data[p + 1] << 8) | data[p + 2];
                        p += 3;

                        if (p + nameLen > start + length) return -1;

                        // позиция относительно начала payload — именно её
                        // понимает BuildSegment
                        return p - start;
                    }

                    pos += extLen;
                }
            }
            catch { }

            return -1;
        }

        /// <summary>Позиция разреза внутри payload. 0 и весь payload бессмысленны:
        /// первый даёт пустой сегмент, второй — «ничего не разрезали».
        /// </summary>
        private static int Clamp(int position, int payloadLength)
        {
            if (payloadLength <= 1) return 0;

            if (position <= 0) return 1;
            if (position >= payloadLength) return payloadLength / 2;

            return position;
        }
    }
}