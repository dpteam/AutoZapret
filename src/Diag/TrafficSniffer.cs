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

            DateTime until = DateTime.UtcNow.AddSeconds(result.Seconds);

            try
            {
                while (DateTime.UtcNow < until && !cancel.IsCancellationRequested)
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

                        result.Error = "ошибка приёма: " + error;
                        return result;
                    }

                    result.Total++;

                    Marshal.Copy(addressPtr, address, 0, WinDivertInterop.AddressSize);
                    WinDivertAddress addr = WinDivertAddress.FromBuffer(address);

                    if (addr.Outbound) result.Outbound++; else result.Inbound++;

                    PacketInfo info;
                    if (!PacketParser.TryParse(packet, (int)recvLen, addr.IsIPv6, out info)) continue;

                    // TLS ClientHello начинается с 0x16 0x03
                    if (!info.HasPayload) continue;
                    if (packet[info.PayloadOffset] != 0x16) continue;
                    if (info.PayloadLength < 5 || packet[info.PayloadOffset + 1] != 0x03) continue;

                    result.Tls++;

                    byte[] payload = new byte[info.PayloadLength];
                    Array.Copy(packet, info.PayloadOffset, payload, 0, info.PayloadLength);

                    string sni = PayloadValidator.ExtractSni(payload, PayloadKind.TlsClientHello);

                    if (sni.Length == 0) continue;

                    result.WithSni++;
                    if (!result.Snis.Contains(sni)) result.Snis.Add(sni);
                }

                result.Ok = true;
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(addressPtr);
                WinDivertInterop.WinDivertClose(handle);
            }
        }
    }
}