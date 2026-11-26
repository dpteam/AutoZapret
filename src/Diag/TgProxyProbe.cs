using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AutoZapret.Core;
using AutoZapret.Proxy;

namespace AutoZapret.Diag
{
    /// <summary>
    /// Сквозная проверка Telegram-прокси НАСТОЯЩИМ запросом.
    ///
    /// ПОЧЕМУ НЕ «HANDSHAKE OK»
    ///   Прокси отвечает клиенту 64 байтами своего init сразу после разбора
    ///   клиентского. Это значит только «мы умеем читать заголовок»: байты
    ///   взяты из нашего же буфера, до Telegram они не доходили. Проверка,
    ///   которая на этом останавливается, зелёная и при полностью нерабочем
    ///   маршруте — ровно так и было: «handshake принят, ответ 64 байта»,
    ///   а Telegram в это время висел на «Соединение…».
    ///
    /// ЧТО ДЕЛАЕМ ЗДЕСЬ
    ///   1. поднимаем настоящий `TgProxyServer` на свободном порту;
    ///   2. подключаемся к нему как клиент Telegram: obfuscated2 init (dd-тег);
    ///   3. шлём `req_pq_multi` — первый запрос любого клиента, ключ не нужен;
    ///   4. ждём `resPQ#05162463`.
    ///
    /// `resPQ` доказывает всю цепочку: разбор handshake, криптографию обоих
    /// направлений, нарезку на кадры, маршрут (свой релей или пул CfProxy) и
    /// сам Telegram. Всё, что короче, не доказывает ничего.
    /// </summary>
    public static class TgProxyProbe
    {
        private const uint ReqPqMulti = 0xbe7e8ef1;
        private const uint ResPq = 0x05162463;

        public static int Run(string[] args)
        {
            int dc = 2;
            bool cfProxy = true;
            string socks = "";

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--tg-roundtrip", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length)
                {
                    int.TryParse(args[i + 1], out dc);
                }

                if (string.Equals(args[i], "--no-cfproxy", StringComparison.OrdinalIgnoreCase))
                    cfProxy = false;

                // Апстрим через внешний SOCKS5.
                //
                // Нужен, чтобы прогнать НАШ код по тому же пути, что у телефона
                // (там обход — это локальный SOCKS5), не пересобирая APK. Без
                // этого «на ПК работает, на телефоне нет» остаётся загадкой:
                // различий два — сеть и платформа, и разделить их нечем.
                if (string.Equals(args[i], "--socks", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length)
                {
                    socks = args[i + 1];
                }
            }

            Console.WriteLine("== сквозная проверка Telegram-прокси (DC " + dc + ") ==");
            Console.WriteLine("   путь: клиент -> наш прокси -> " +
                              (cfProxy ? "пул CfProxy" : "прямой") + " -> Telegram" +
                              (socks.Length > 0 ? ", апстрим через SOCKS5 " + socks : ""));
            Console.WriteLine();

            int port = FindFreePort();
            string secretHex = Settings.NewSecret();

            TgProxyServer server = new TgProxyServer();

            string startError = server.Start("127.0.0.1", port, secretHex, null, cfProxy, socks);

            if (startError != null)
            {
                Console.WriteLine("  [FAIL] прокси не запустился: " + startError);
                return 1;
            }

            try
            {
                return Probe(port, secretHex, dc);
            }
            finally
            {
                try { server.Stop(); } catch { }
                try { server.Dispose(); } catch { }
            }
        }

        private static int Probe(int port, string secretHex, int dc)
        {
            byte[] secret = TgProxyServer.ParseSecret(secretHex);

            byte[] init = BuildClientInit(secret, dc);

            TcpClient client = new TcpClient();

            try
            {
                client.NoDelay = true;
                client.Connect(IPAddress.Loopback, port);
                client.ReceiveTimeout = 45000;
                client.SendTimeout = 20000;

                NetworkStream stream = client.GetStream();

                stream.Write(init, 0, init.Length);
                stream.Flush();

                // ---- 1. init от прокси: тег и DC
                byte[] serverInit = ReadExact(stream, 64);

                if (serverInit == null)
                {
                    Console.WriteLine("  [FAIL] прокси не ответил init");
                    return 1;
                }

                string tag;
                int gotDc;

                if (!ReadServerInit(serverInit, out tag, out gotDc))
                {
                    Console.WriteLine("  [FAIL] init прокси не разобран");
                    return 1;
                }

                Console.WriteLine("  [ok]   init от прокси: тег " + tag + ", DC " + gotDc);

                if (tag != "dddddddd")
                {
                    Console.WriteLine("  [FAIL] прокси объявил тег " + tag +
                                      " — Telegram разберёт поток другим форматом длины");
                    return 1;
                }

                // ---- 2. настоящий запрос
                byte[] key = MtProtoHandshake.DeriveKey(
                    Slice(init, 8, 32), secret);

                byte[] iv = Slice(init, 40, 16);

                byte[] packet = BuildReqPq();

                using (AesCtr toProxy = new AesCtr(key, iv))
                {
                    toProxy.Skip(MtProtoHandshake.HandshakeLen);
                    toProxy.Transform(packet, 0, packet.Length);
                }

                stream.Write(packet, 0, packet.Length);
                stream.Flush();

                Console.WriteLine("  [ok]   req_pq_multi отправлен (" + packet.Length + " Б), жду resPQ");

                // ---- 3. ответ
                //
                // ВАЖНО: поток от прокси зашифрован ключом клиента, поэтому
                // длину кадра нельзя читать из сырых байтов — сначала
                // расшифровка. Именно на этом спотыкался и сам тест: он брал
                // четыре зашифрованных байта за длину и объявлял «не тот
                // формат» там, где всё было правильно.
                byte[] rev = new byte[48];

                for (int i = 0; i < 48; i++)
                    rev[i] = init[8 + 47 - i];

                byte[] downKey = MtProtoHandshake.DeriveKey(Slice(rev, 0, 32), secret);
                byte[] downIv = Slice(rev, 32, 16);

                using (AesCtr fromProxy = new AesCtr(downKey, downIv))
                {
                    byte[] header = ReadExact(stream, 4);

                    if (header == null)
                    {
                        Console.WriteLine("  [FAIL] прокси закрыл соединение без ответа");
                        Console.WriteLine("         значит данные не дошли до Telegram или ответ не вернулся");
                        return 1;
                    }

                    fromProxy.Transform(header, 0, 4);

                    int frameLen = (header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
                    frameLen &= 0x7FFFFFFF;

                    if (frameLen <= 0 || frameLen > 4 * 1024 * 1024)
                    {
                        Console.WriteLine("  [FAIL] неправдоподобная длина ответа: " + frameLen);
                        Console.WriteLine("         первые байты после расшифровки: " +
                                          MtProtoHandshake.Hex(header, 0, 4));
                        return 1;
                    }

                    byte[] body = ReadExact(stream, frameLen);

                    if (body == null)
                    {
                        Console.WriteLine("  [FAIL] ответ оборвался на " + frameLen + " байтах");
                        return 1;
                    }

                    fromProxy.Transform(body, 0, body.Length);

                    if (body.Length < 4)
                    {
                        Console.WriteLine("  [FAIL] ответ короче заголовка MTProto: " + body.Length + " Б");
                        return 1;
                    }

                    // Тело ответа — незашифрованное MTProto-сообщение:
                    //   auth_key_id(8) + msg_id(8) + length(4) + body
                    // Конструктор лежит в теле сообщения, то есть на 20.
                    // (4 байта длины транспорта уже прочитаны выше.)
                    if (body.Length < 24)
                    {
                        Console.WriteLine("  [FAIL] ответ короче заголовка MTProto: " + body.Length + " Б");
                        return 1;
                    }

                    uint ctor = (uint)(body[20] | (body[21] << 8) | (body[22] << 16) | (body[23] << 24));

                    if (ctor == ResPq)
                    {
                        Console.WriteLine("  [ok]   resPQ от Telegram");
                        Console.WriteLine();
                        Console.WriteLine("Telegram работает через прокси: путь целиком проверен");
                        return 0;
                    }

                    Console.WriteLine("  [FAIL] ответ разобран, но конструктор 0x" + ctor.ToString("x8") +
                                      " вместо resPQ (0x05162463)");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        /// <summary>
        /// Понять, ЧЕМ на самом деле зашифрован ответ.
        ///
        /// Два подозрения, и оба проверяемы на четырёх байтах:
        ///   * прокси не перешифровал поток — тогда данные расшифруются
        ///     ключом самого туннеля (перевёрнутый НАШ init прокси);
        ///   * прокси перешифровал, но не тем секретом — тогда не подходит ни
        ///     один из двух.
        ///
        /// Без этой строки отладка идёт наощупь: «мусор» одинаково выглядит
        /// при трёх разных причинах.
        /// </summary>
        private static string DiagnoseKeys(byte[] sample, byte[] init, byte[] serverInit, byte[] secret)
        {
            // 1) расшифровка «как есть» ключом туннеля (без перешифровки)
            byte[] relayKey, relayIv;
            MtProtoHandshake.DeriveRelayReverse(serverInit, out relayKey, out relayIv);

            byte[] probe = (byte[])sample.Clone();

            using (AesCtr ctr = new AesCtr(relayKey, relayIv))
                ctr.Transform(probe, 0, probe.Length);

            string asRelay = MtProtoHandshake.Hex(probe, 0, 4);

            if (LooksLikeLength(probe))
                return "ответ НЕ перешифрован (похоже на прямой поток туннеля)";

            // 2) расшифровка ключом клиента (как должно быть)
            byte[] rev = new byte[48];

            for (int i = 0; i < 48; i++)
                rev[i] = init[8 + 47 - i];

            byte[] downKey = MtProtoHandshake.DeriveKey(Slice(rev, 0, 32), secret);
            byte[] downIv = Slice(rev, 32, 16);

            probe = (byte[])sample.Clone();

            using (AesCtr ctr = new AesCtr(downKey, downIv))
                ctr.Transform(probe, 0, probe.Length);

            if (LooksLikeLength(probe))
                return "ответ перешифрован правильно";

            return "не подошёл ни один ключ (как туннельный " + asRelay + ", как клиентский " +
                   MtProtoHandshake.Hex(probe, 0, 4) + ")";
        }

        /// <summary>Похоже ли на 4-байтную длину MTProto-кадра.</summary>
        private static bool LooksLikeLength(byte[] data)
        {
            if (data == null || data.Length < 4) return false;

            int value = (data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24)) & 0x7FFFFFFF;

            return value >= 20 && value <= 4 * 1024 * 1024;
        }

        /// <summary>
        /// Проверить ТОЛЬКО транспорт: Socks5Client -> TLS -> WsClient -> init -> req_pq.
        ///
        /// Зачем отдельно от `--tg-roundtrip`. Тот проверяет всё сразу, и когда
        /// он красный, непонятно, где искать: в нашем прокси, в WS-клиенте или
        /// в SOCKS5. Здесь ровно один слой, и его ответ однозначен.
        /// </summary>
        public static int RunWsDirect(string[] args)
        {
            string socks = "";
            string domain = "pclead.co.uk";
            int dc = 2;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--socks", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    socks = args[i + 1];

                if (string.Equals(args[i], "--domain", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    domain = args[i + 1];

                if (string.Equals(args[i], "--ws-direct", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out dc);
            }

            string host = "kws" + dc + "." + domain;

            Console.WriteLine("== только транспорт: " + host + "/apiws ==");
            Console.WriteLine("   SOCKS5: " + (socks.Length > 0 ? socks : "напрямую"));
            Console.WriteLine();

            TcpClient client = null;
            System.Net.Security.SslStream ssl = null;

            try
            {
                string error;

                if (socks.Length > 0)
                {
                    string ph;
                    int pp;

                    if (!Socks5Client.TryParse(socks, out ph, out pp))
                    {
                        Console.WriteLine("  [FAIL] адрес SOCKS5 не разобран");
                        return 1;
                    }

                    client = Socks5Client.Connect(ph, pp, host, 443, 15000, out error);

                    if (client == null)
                    {
                        Console.WriteLine("  [FAIL] SOCKS5: " + error);
                        return 1;
                    }

                    // Nagle обратно включён: наши записи становятся мелкими
                    // сегментами (запрос, кадр init, кадр данных), а обход
                    // обрабатывает первые сегменты соединения. Проверяем, не в
                    // этом ли разница с рабочим клиентом, который пишет
                    // слитно.
                    if (Has(args, "--nagle")) client.NoDelay = false;
                }
                else
                {
                    client = new TcpClient();
                    client.Connect(host, 443);
                }

                Console.WriteLine("  [ok]   TCP");

                ssl = new System.Net.Security.SslStream(client.GetStream(), false,
                                                        GatewayCheck.ValidateRelay);

                // TLS 1.2 — диагностический вариант: у него ClientHello заметно
                // меньше (нет пост-квантового обмена ключами). Если через обход
                // работает только он, значит дело в размере/содержимом
                // ClientHello, а не в нашем коде.
                if (Has(args, "--tls12"))
                {
                    ssl.AuthenticateAsClient(host, null,
                        System.Security.Authentication.SslProtocols.Tls12, false);
                }
                else
                {
                    ssl.AuthenticateAsClient(host);
                }

                Console.WriteLine("  [ok]   TLS (" + ssl.SslProtocol + ")");

                string wsError;
                WsClient ws = WsClient.Connect(ssl, host, "/apiws", 15000, out wsError);

                if (ws == null)
                {
                    Console.WriteLine("  [FAIL] WS: " + wsError);
                    return 1;
                }

                Console.WriteLine("  [ok]   WS 101");

                // Небольшая пауза после апгрейда.
                //
                // Проверяем гипотезу: воркер Cloudflare заканчивает апгрейд
                // раньше, чем открывает сокет к Telegram, и кадр, отправленный
                // в это окно, теряется — ответа не будет никогда. Снаружи это
                // выглядит как «101 получили, дальше тишина».
                if (Has(args, "--pause"))
                {
                    System.Threading.Thread.Sleep(150);
                    Console.WriteLine("  пауза после 101: 150 мс");
                }

                // init как у настоящего клиента
                byte[] init = BuildClientInit(new byte[16], dc);
                Buffer.BlockCopy(Slice(init, 8, 32), 0, init, 8, 32);

                byte[] key = Slice(init, 8, 32);
                byte[] iv = Slice(init, 40, 16);

                // хвост пересобираем «как к Telegram»: без secret
                byte[] tail = new byte[8];
                Buffer.BlockCopy(MtProtoHandshake.TagPadded, 0, tail, 0, 4);
                tail[4] = (byte)(dc & 0xFF);

                using (AesCtr ctr = new AesCtr(key, iv))
                {
                    ctr.Skip(56);
                    ctr.Transform(tail, 0, 8);
                }

                Buffer.BlockCopy(tail, 0, init, 56, 8);

                // Проверяем собственный init так, как его проверит Telegram:
                // ключ = prekey, iv из init, расшифровываем хвост и смотрим тег.
                // Неверный init Telegram молча выбрасывает — и снаружи это
                // «101 получили, дальше тишина».
                {
                    byte[] check = Slice(init, 56, 8);

                    using (AesCtr ctr = new AesCtr(key, iv))
                    {
                        ctr.Skip(56);
                        ctr.Transform(check, 0, 8);
                    }

                    Console.WriteLine("  наш init: первые байты " + MtProtoHandshake.Hex(init, 0, 4) +
                                      ", тег " + MtProtoHandshake.Hex(check, 0, 4) +
                                      ", DC " + (check[4] | (check[5] << 8)));
                }

                byte[] packet = BuildReqPq();

                using (AesCtr toRelay = new AesCtr(key, iv))
                {
                    toRelay.Skip(64);
                    toRelay.Transform(packet, 0, packet.Length);
                }

                byte[] packetEncrypted = packet;

                bool manual = Has(args, "--manual");

                Console.WriteLine("  режим отправки: " + (manual ? "ручные кадры" : "WsClient.SendBinary"));

                if (manual)
                {
                    byte[] f1 = BuildRawFrame(init);
                    byte[] f2 = BuildRawFrame(packetEncrypted);

                    if (Has(args, "--coalesce"))
                    {
                        // Одна запись на два кадра: так делает рабочий клиент
                        // (JS пишет подряд, и TCP склеивает их в один сегмент).
                        // Проверяем, не в этом ли разница: обход обрабатывает
                        // первые сегменты соединения, и лишний сегмент может
                        // стоить нам всего потока.
                        byte[] both = new byte[f1.Length + f2.Length];
                        Buffer.BlockCopy(f1, 0, both, 0, f1.Length);
                        Buffer.BlockCopy(f2, 0, both, f1.Length, f2.Length);

                        ssl.Write(both, 0, both.Length);
                        ssl.Flush();

                        Console.WriteLine("  оба кадра отправлены одной записью (" + both.Length + " Б)");
                    }
                    else
                    {
                        ssl.Write(f1, 0, f1.Length);
                        ssl.Flush();
                        ssl.Write(f2, 0, f2.Length);
                        ssl.Flush();
                    }
                }
                else
                {
                    if (!ws.SendBinary(init, 15000))
                    {
                        Console.WriteLine("  [FAIL] init не отправлен");
                        return 1;
                    }

                    if (!ws.SendBinary(packetEncrypted, 15000))
                    {
                        Console.WriteLine("  [FAIL] req_pq не отправлен");
                        return 1;
                    }
                }
                Console.WriteLine("  [ok]   init и req_pq отправлены, жду ответ");

                if (Has(args, "--raw-read"))
                {
                    // Сырое чтение: есть ли ВООБЩЕ байты от сервера. Нужно,
                    // чтобы отделить «сервер молчит» от «наш разбор кадров не
                    // понимает ответ».
                    byte[] raw = new byte[512];
                    ssl.ReadTimeout = 10000;

                    try
                    {
                        int n = ssl.Read(raw, 0, raw.Length);
                        Console.WriteLine("  сырое чтение: " + n + " Б" +
                                          (n > 0 ? ", начало " + MtProtoHandshake.Hex(raw, 0, Math.Min(16, n)) : ""));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  сырое чтение: " + ex.GetType().Name + ": " + ex.Message);
                    }

                    return 1;
                }

                byte[] reply = ws.ReceiveMessage(20000);

                if (reply == null)
                {
                    Console.WriteLine("  [FAIL] ответа нет: " + (ws.LastError ?? "?") +
                                      (string.IsNullOrEmpty(ws.CloseReason) ? "" : ", " + ws.CloseReason));
                    return 1;
                }

                Console.WriteLine("  [ok]   ответ " + reply.Length + " Б");

                byte[] rev = new byte[48];

                for (int i = 0; i < 48; i++)
                    rev[i] = init[8 + 47 - i];

                byte[] downKey = Slice(rev, 0, 32);
                byte[] downIv = Slice(rev, 32, 16);

                using (AesCtr fromRelay = new AesCtr(downKey, downIv))
                    fromRelay.Transform(reply, 0, reply.Length);

                uint ctor = (uint)(reply[24] | (reply[25] << 8) | (reply[26] << 16) | (reply[27] << 24));

                if (ctor == ResPq)
                {
                    Console.WriteLine();
                    Console.WriteLine("транспорт рабочий: resPQ получен");
                    return 0;
                }

                Console.WriteLine("  [FAIL] конструктор 0x" + ctor.ToString("x8"));
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
            finally
            {
                if (ssl != null) { try { ssl.Dispose(); } catch { } }
                if (client != null) { try { client.Close(); } catch { } }
            }
        }

        /// <summary>Есть ли флаг в аргументах.</summary>
        private static bool Has(string[] args, string flag)
        {
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// Собрать WS-кадр вручную: так же, как это делает рабочий node-тест.
        /// </summary>
        private static byte[] BuildRawFrame(byte[] payload)
        {
            byte[] mask = new byte[4];

            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(mask);

            List<byte> frame = new List<byte>(payload.Length + 14);

            frame.Add(0x82);   // FIN + binary

            if (payload.Length < 126)
            {
                frame.Add((byte)(0x80 | payload.Length));
            }
            else
            {
                frame.Add(0x80 | 126);
                frame.Add((byte)((payload.Length >> 8) & 0xFF));
                frame.Add((byte)(payload.Length & 0xFF));
            }

            frame.AddRange(mask);

            for (int i = 0; i < payload.Length; i++)
                frame.Add((byte)(payload[i] ^ mask[i & 3]));

            return frame.ToArray();
        }

        /// <summary>
        /// Отправить WS-кадр вручную: так же, как это делает рабочий node-тест.
        ///
        /// Нужно, чтобы отделить «наш WsClient собирает кадр неверно» от «сервер
        /// не отвечает». Без этого обе причины выглядят одинаково: тишина.
        /// </summary>
        private static void WriteRawFrame(Stream stream, byte[] payload)
        {
            byte[] bytes = BuildRawFrame(payload);

            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>Расшифровать хвост init прокси: там тег и DC.</summary>
        private static bool ReadServerInit(byte[] serverInit, out string tag, out int dc)
        {
            tag = "";
            dc = 0;

            try
            {
                byte[] key = Slice(serverInit, 8, 32);
                byte[] iv = Slice(serverInit, 40, 16);

                byte[] tail = Slice(serverInit, 56, 8);

                using (AesCtr ctr = new AesCtr(key, iv))
                {
                    ctr.Skip(56);
                    ctr.Transform(tail, 0, 8);
                }

                tag = MtProtoHandshake.Hex(tail, 0, 4);
                dc = tail[4] | (tail[5] << 8);

                if (dc > 32767) dc -= 65536;

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>64-байтовый init как у клиента Telegram: dd-тег, DC.</summary>
        private static byte[] BuildClientInit(byte[] secret, int dc)
        {
            byte[] init = new byte[64];

            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(init);

            byte[] key = MtProtoHandshake.DeriveKey(Slice(init, 8, 32), secret);
            byte[] iv = Slice(init, 40, 16);

            byte[] tail = new byte[8];
            Buffer.BlockCopy(MtProtoHandshake.TagPadded, 0, tail, 0, 4);
            tail[4] = (byte)(dc & 0xFF);
            tail[5] = (byte)((dc >> 8) & 0xFF);

            using (AesCtr ctr = new AesCtr(key, iv))
            {
                ctr.Skip(56);
                ctr.Transform(tail, 0, 8);
            }

            Buffer.BlockCopy(tail, 0, init, 56, 8);
            return init;
        }

        /// <summary>
        /// req_pq_multi в intermediate-формате.
        ///
        /// Незашифрованное MTProto-сообщение: auth_key_id = 0, message_id,
        /// длина, тело. Ключ не нужен — это первый запрос рукопожатия, его
        /// шлёт любой клиент до авторизации.
        /// </summary>
        private static byte[] BuildReqPq()
        {
            byte[] nonce = new byte[16];

            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(nonce);

            byte[] body = new byte[20];
            WriteUInt32(body, 0, ReqPqMulti);
            Buffer.BlockCopy(nonce, 0, body, 4, 16);

            byte[] msg = new byte[20 + body.Length];

            // auth_key_id = 0 — 8 байт нулей уже на месте
            ulong msgId = ((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()) << 32;

            WriteUInt64(msg, 8, msgId);
            WriteUInt32(msg, 16, (uint)body.Length);
            Buffer.BlockCopy(body, 0, msg, 20, body.Length);

            byte[] framed = new byte[4 + msg.Length];
            WriteUInt32(framed, 0, (uint)msg.Length);
            Buffer.BlockCopy(msg, 0, framed, 4, msg.Length);

            return framed;
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
                buffer[offset + i] = (byte)((value >> (i * 8)) & 0xFF);
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            byte[] result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int read = 0;

            try
            {
                while (read < count)
                {
                    int n = stream.Read(buffer, read, count - read);
                    if (n <= 0) return null;
                    read += n;
                }

                return buffer;
            }
            catch
            {
                return null;
            }
        }

        private static int FindFreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            return port;
        }
    }
}
