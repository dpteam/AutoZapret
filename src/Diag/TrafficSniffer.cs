using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Payloads;
using AutoZapret.Zapret;

namespace AutoZapret.Diag
{
    /// <summary>
    /// Что сниффер увидел за N секунд.
    ///
    /// Это ответ на вопрос «включён, аргументы есть, а не работает — почему?».
    /// Если пакеты видны, а стратегия не влияет, дело в стратегии/аргументах.
    /// Если пакетов не видно вообще — WinDivert не перехватывает, и любая
    /// стратегия будет выглядеть сломанной.
    /// </summary>
    public sealed class SniffResult
    {
        public bool Ok;
        public string Error = "";

        public int Total;
        public int Outbound;
        public int Inbound;
        public int Tls;
        public int WithSni;

        public int Seconds;
        public List<string> Snis = new List<string>();

        public string Summary()
        {
            if (!Ok) return Error;

            return "пакетов TCP/443: " + Total +
                   " (наружу " + Outbound + ", внутрь " + Inbound + "), " +
                   "TLS ClientHello: " + Tls + ", с SNI: " + WithSni;
        }

        /// <summary>
        /// Человеческий вывод: работы драйвера мало, надо объяснить, что это значит.
        /// </summary>
        public string Verdict()
        {
            if (!Ok) return Error;

            if (Total > 0 && WithSni > 0)
                return "драйвер видит трафик и разбирает TLS — значит обход должен работать, " +
                       "и если сайты не открываются, дело в стратегии";

            if (Total > 0)
                return "драйвер видит пакеты, но TLS ClientHello не разобрано. " +
                       "Откройте youtube в браузере во время проверки и повторите";

            return "пакетов на 443 порту не было совсем: либо в это время никто не ходил в сеть, " +
                   "либо WinDivert не перехватывает (VPN/TUN-адаптер, другой перехватчик)";
        }
    }

    /// <summary>
    /// Сниффер на WinDivert в режиме SNIFF: отдаёт КОПИИ пакетов, трафик не трогает.
    /// Открыть его — уже проверка: если WinDivert не открывается, никакая стратегия
    /// работать не будет, и это видно до перебора двадцати двух штук.
    /// </summary>
    public static class TrafficSniffer
    {
        public static SniffResult Run(int seconds, CancellationToken cancel = default(CancellationToken))
        {
            SniffResult result = new SniffResult { Seconds = Math.Max(1, seconds) };

            if (!WinDivertInterop.IsAvailable())
            {
                result.Error = "WinDivert.dll не найден в " + Paths.EngineBinDir +
                               " — установите движок";
                return result;
            }

            IntPtr handle;

            try
            {
                handle = WinDivertInterop.WinDivertOpen(
                    "tcp.DstPort == 443 or tcp.SrcPort == 443",
                    WinDivertInterop.LayerNetwork, 0, WinDivertInterop.FlagSniff);
            }
            catch (DllNotFoundException ex)
            {
                result.Error = "WinDivert.dll не загрузилась: " + ex.Message;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "не удалось открыть WinDivert: " + ex.GetType().Name + ": " + ex.Message;
                return result;
            }

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                int error = Marshal.GetLastWin32Error();

                result.Error = error == 5
                    ? "WinDivert не открылся (код 5): нужны права администратора"
                    : "WinDivert не открылся (код " + error + "): драйвер занят другим перехватчиком";

                return result;
            }

            byte[] packet = new byte[WinDivertInterop.MaxPacketSize];
            byte[] address = new byte[WinDivertInterop.AddressSize];
            IntPtr addressPtr = Marshal.AllocHGlobal(WinDivertInterop.AddressSize);

            // WinDivertRecv блокируется до прихода пакета, поэтому «спать 8 секунд»
            // в этом же потоке нельзя: без трафика проверка висела бы вечно
            // (вживую видели 36 секунд вместо 8). Читаем в отдельном потоке,
            // а по времени закрываем хэндл — recv сразу вернёт ошибку.
            ManualResetEvent stop = new ManualResetEvent(false);
            Thread reader = new Thread(delegate ()
            {
                while (!stop.WaitOne(0) && !cancel.IsCancellationRequested)
                {
                    uint recvLen;

                    bool ok = WinDivertInterop.WinDivertRecv(
                        handle, packet, (uint)packet.Length, out recvLen, addressPtr);

                    if (!ok)
                    {
                        int error = Marshal.GetLastWin32Error();

                        // таймаут — норма: просто ждём дальше, пока не вышло время
                        if (error == WinDivertInterop.ErrorTimeout || error == WinDivertInterop.ErrorNoData)
                        {
                            Thread.Sleep(50);
                            continue;
                        }

                        // 6 = хэндл закрыт нами по времени — это штатное завершение
                        if (error == 6) break;

                        result.Error = "ошибка приёма: " + error;
                        break;
                    }

                    try { Count(result, packet, (int)recvLen, addressPtr, address); }
                    catch (Exception ex) { Log.Debug("DIAG", "сниффер: " + ex.Message); }
                }
            })
            { IsBackground = true, Name = "sniffer-recv" };

            try
            {
                reader.Start();

                int wait = (int)Math.Min(int.MaxValue, result.Seconds * 1000L);

                WaitHandle.WaitAny(new WaitHandle[] { cancel.WaitHandle }, wait);

                stop.Set();
                WinDivertInterop.WinDivertClose(handle);
                reader.Join(1500);

                result.Ok = result.Error.Length == 0;
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(addressPtr);
                try { WinDivertInterop.WinDivertClose(handle); } catch { }
            }
        }

        /// <summary>Разбор одного пакета: считаем и, если это ClientHello, вытаскиваем SNI.</summary>
        private static void Count(SniffResult result, byte[] packet, int length,
                                  IntPtr addressPtr, byte[] address)
        {
            result.Total++;

            Marshal.Copy(addressPtr, address, 0, WinDivertInterop.AddressSize);
            WinDivertAddress addr = WinDivertAddress.FromBuffer(address);

            if (addr.Outbound) result.Outbound++; else result.Inbound++;

            PacketInfo info;
            if (!PacketParser.TryParse(packet, length, addr.IsIPv6, out info)) return;

            // TLS ClientHello начинается с 0x16 0x03
            if (!info.HasPayload) return;
            if (packet[info.PayloadOffset] != 0x16) return;
            if (info.PayloadLength < 5 || packet[info.PayloadOffset + 1] != 0x03) return;

            result.Tls++;

            byte[] payload = new byte[info.PayloadLength];
            Array.Copy(packet, info.PayloadOffset, payload, 0, info.PayloadLength);

            string sni = PayloadValidator.ExtractSni(payload, PayloadKind.TlsClientHello);

            if (sni.Length == 0) return;

            result.WithSni++;

            lock (result.Snis)
            {
                if (!result.Snis.Contains(sni)) result.Snis.Add(sni);
            }
        }
    }
}