using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace AutoZapret.Core
{
    /// <summary>
    /// Счётчики сетевого обмена: сколько байт прошло с прошлого замера.
    ///
    /// Что это НЕ измеряет: скорость самого обхода. Windows не отдаёт
    /// «байты процесса» без ETW, а WinDivert работает в ядре, и его трафик
    /// к процессу winws не относится. Поэтому здесь честно системный обмен —
    /// и в UI так и подписано, иначе цифра читается как «скорость обхода».
    ///
    /// Loopback и неработающие интерфейсы не считаются: иначе локальные
    /// соединения (в том числе наш прокси) давали бы ложный «трафик».
    /// </summary>
    public sealed class NetStats
    {
        public long BytesReceived;
        public long BytesSent;
        public DateTime SampledAt = DateTime.MinValue;

        /// <summary>Суммарные счётчики по всем активным интерфейсам, кроме loopback.</summary>
        public static NetStats Sample()
        {
            NetStats stats = new NetStats { SampledAt = DateTime.UtcNow };

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                        IPv4InterfaceStatistics v4 = ni.GetIPv4Statistics();
                        stats.BytesReceived += Math.Max(0, v4.BytesReceived);
                        stats.BytesSent += Math.Max(0, v4.BytesSent);
                    }
                    catch
                    {
                        // один интерфейс не отдал статистику — не повод терять остальные
                    }
                }
            }
            catch
            {
                // нет прав/сети — вернём нули, UI покажет прочерк
            }

            return stats;
        }

        /// <summary>Разница между двумя замерами в байтах в секунду.</summary>
        public static void Rates(NetStats previous, NetStats current, out long downBytesPerSec, out long upBytesPerSec)
        {
            downBytesPerSec = 0;
            upBytesPerSec = 0;

            if (previous == null || current == null) return;

            double seconds = (current.SampledAt - previous.SampledAt).TotalSeconds;
            if (seconds <= 0.05) return;

            // счётчики могут обнулиться (сброс адаптера) — тогда разница отрицательная
            long received = current.BytesReceived - previous.BytesReceived;
            long sent = current.BytesSent - previous.BytesSent;

            if (received < 0 || sent < 0) return;

            downBytesPerSec = (long)(received / seconds);
            upBytesPerSec = (long)(sent / seconds);
        }

        /// <summary>Человекочитаемая скорость: «1.2 МБ/с», «340 КБ/с», «12 Б/с».</summary>
        public static string Format(long bytesPerSec)
        {
            if (bytesPerSec < 0) bytesPerSec = 0;

            if (bytesPerSec < 1024) return bytesPerSec + " Б/с";

            double kb = bytesPerSec / 1024.0;
            if (kb < 1024) return kb.ToString("0") + " КБ/с";

            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0") + " МБ/с";

            return (mb / 1024.0).ToString("0.00") + " ГБ/с";
        }
    }
}