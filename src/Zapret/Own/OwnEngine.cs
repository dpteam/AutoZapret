using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AutoZapret.Core;

namespace AutoZapret.Zapret.Own
{
    /// <summary>Настройки своего движка.</summary>
    public sealed class OwnEngineOptions
    {
        /// <summary>Где резать ClientHello. 1 — сразу после первого байта (как split-pos=1).</summary>
        public int SplitPosition = 1;

        /// <summary>Сколько секунд работать (0 — пока не отменят).</summary>
        public int Seconds = 0;

        /// <summary>Ничего не отправлять и не отбрасывать: только показать, что бы сделали.</summary>
        public bool DryRun;

        /// <summary>Дополнительные порты не нужны: первый шаг — только TCP/443.</summary>
        public int Port = 443;
    }

    /// <summary>Что движок сделал — для лога и отчёта.</summary>
    public sealed class OwnEngineStats
    {
        public int Packets;
        public int ClientHellos;
        public int Split;
        public int Errors;

        public string Summary()
        {
            return "пакетов " + Packets + ", ClientHello " + ClientHellos +
                   ", разрезано " + Split + ", ошибок " + Errors;
        }
    }

    /// <summary>
    /// Первый шаг своего движка (ROADMAP P10.3): перехват TCP/443 и техника
    /// `split` — ClientHello режется на две части с правильными seq.
    ///
    /// Почему именно split: это самая простая техника, которую DPI не умеет
    /// собирать, и на ней видно, работает ли вообще наш конвейер — перехват,
    /// правка байтов, пересчёт сумм, отправка, отбрасывание оригинала.
    /// Если здесь всё сходится, остальные техники (fake, multisplit, hostfakesplit)
    /// добавляются как отдельные правила, а не как новый движок.
    ///
    /// Безопасность важнее возможностей: любая ошибка разбора или отправки
    /// заканчивается тем, что пакет уходит НЕИЗМЕНЁННЫМ. Движок не должен
    /// уметь «сломать интернет» — этим он и отличается от самодельных фильтров.
    /// </summary>
    public sealed class OwnEngine
    {
        private readonly OwnEngineOptions _options;
        private readonly OwnEngineStats _stats = new OwnEngineStats();

        public OwnEngine(OwnEngineOptions options)
        {
            _options = options ?? new OwnEngineOptions();
        }

        public OwnEngineStats Stats { get { return _stats; } }

        /// <summary>
        /// Запустить. Возвращает null при успехе, иначе текст ошибки.
        /// </summary>
        public string Run(CancellationToken cancel = default(CancellationToken))
        {
            if (!WinDivertInterop.IsAvailable())
                return "WinDivert.dll не найден в " + Paths.EngineBinDir;

            string filter = "outbound and tcp.DstPort == " + _options.Port +
                            " and tcp.PayloadLength > 40";

            IntPtr handle;

            try
            {
                handle = WinDivertInterop.WinDivertOpen(
                    filter, WinDivertInterop.LayerNetwork, 0, 0);
            }
            catch (Exception ex)
            {
                return "WinDivert не открылся: " + ex.GetType().Name + ": " + ex.Message;
            }

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                int error = Marshal.GetLastWin32Error();

                return error == 5
                    ? "нужны права администратора (WinDivert код 5)"
                    : "WinDivert не открылся (код " + error + "): драйвер занят другим перехватчиком";
            }

            Log.Info("OWN", "движок запущен: split pos=" + _options.SplitPosition +
                            (_options.DryRun ? " (только показ)" : "") + ", фильтр: " + filter);

            ManualResetEvent stop = new ManualResetEvent(false);

            try
            {
                // приём в отдельном потоке: WinDivertRecv блокируется до пакета,
                // и без этого остановка по времени не сработала бы
                Thread reader = new Thread(delegate () { Loop(handle, stop, cancel); })
                {
                    IsBackground = true,
                    Name = "own-engine"
                };

                reader.Start();

                if (_options.Seconds > 0)
                {
                    WaitHandle.WaitAny(new[] { cancel.WaitHandle }, _options.Seconds * 1000);
                }
                else
                {
                    WaitHandle.WaitAny(new[] { cancel.WaitHandle });
                }

                stop.Set();
                WinDivertInterop.WinDivertClose(handle);
                reader.Join(1500);

                return null;
            }
            finally
            {
                try { WinDivertInterop.WinDivertClose(handle); } catch { }

                Log.Info("OWN", "движок остановлен: " + _stats.Summary());
            }
        }

        private void Loop(IntPtr handle, ManualResetEvent stop, CancellationToken cancel)
        {
            byte[] packet = new byte[WinDivertInterop.MaxPacketSize];
            byte[] address = new byte[WinDivertInterop.AddressSize];

            IntPtr addressPtr = Marshal.AllocHGlobal(WinDivertInterop.AddressSize);

            try
            {
                while (!stop.WaitOne(0) && !cancel.IsCancellationRequested)
                {
                    uint recvLen;

                    if (!WinDivertInterop.WinDivertRecv(handle, packet, (uint)packet.Length, out recvLen, addressPtr))
                    {
                        int error = Marshal.GetLastWin32Error();

                        // 6 = хэндл закрыт нами по времени, 995 = прервано — штатно
                        if (error == 6 || error == 995) break;
                        if (error == WinDivertInterop.ErrorTimeout || error == WinDivertInterop.ErrorNoData)
                        {
                            Thread.Sleep(20);
                            continue;
                        }

                        _stats.Errors++;
                        Log.Warn("OWN", "ошибка приёма: " + error);
                        break;
                    }

                    _stats.Packets++;

                    if (!Handle(handle, addressPtr, packet, (int)recvLen, address))
                    {
                        // не смогли обработать — отдаём пакет как есть, иначе рвём трафик
                        if (!Reinject(handle, addressPtr, packet, (int)recvLen)) _stats.Errors++;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(addressPtr);
            }
        }

        /// <summary>
        /// Разобрать и, если это ClientHello, разрезать. false — «ничего не делал,
        /// отправьте пакет как есть».
        /// </summary>
        private bool Handle(IntPtr handle, IntPtr addressPtr, byte[] packet, int length, byte[] address)
        {
            TcpPacket tcp = TcpPacket.Parse(packet, length);

            if (tcp == null || !tcp.HasPayload) return false;

            // Нас интересует только начало TLS-рукопожатия: 0x16 0x03
            if (packet[tcp.PayloadOffset] != 0x16) return false;
            if (tcp.PayloadLength < 5) return false;
            if (packet[tcp.PayloadOffset + 1] != 0x03) return false;

            _stats.ClientHellos++;

            int pos = _options.SplitPosition;

            if (pos <= 0) pos = 1;
            if (pos >= tcp.PayloadLength) pos = tcp.PayloadLength / 2;

            if (pos <= 0) return false;

            if (_options.DryRun)
            {
                Log.Info("OWN", "разрезал бы " + tcp.SrcPort + "->" + tcp.DstPort +
                                ": seq=" + tcp.Seq + ", payload=" + tcp.PayloadLength + ", pos=" + pos);
                return false;   // в режиме показа пакет уходит как есть
            }

            int firstLen, secondLen;

            byte[] first = tcp.BuildSegment(0, pos, 0, out firstLen);
            byte[] second = tcp.BuildSegment(pos, tcp.PayloadLength, pos, out secondLen);

            if (!Send(handle, addressPtr, first, firstLen))
            {
                _stats.Errors++;
                return false;
            }

            if (!Send(handle, addressPtr, second, secondLen))
            {
                _stats.Errors++;
                return false;
            }

            _stats.Split++;

            if (_stats.Split <= 20 || _stats.Split % 100 == 0)
            {
                Log.Info("OWN", "split " + tcp.SrcPort + "->" + tcp.DstPort +
                                ": " + tcp.PayloadLength + " → " + pos + "+" + (tcp.PayloadLength - pos) +
                                ", seq " + tcp.Seq + " / " + unchecked(tcp.Seq + (uint)pos));
            }

            // Оригинал НЕ отправляем: он уже отправлен двумя частями.
            // Если бы первая часть не ушла, мы сюда не попали бы — пакет уйдёт целиком.
            return true;
        }

        private bool Send(IntPtr handle, IntPtr addressPtr, byte[] data, int length)
        {
            uint sendLen;
            return WinDivertInterop.WinDivertSend(handle, data, (uint)length, out sendLen, addressPtr);
        }

        private bool Reinject(IntPtr handle, IntPtr addressPtr, byte[] packet, int length)
        {
            return Send(handle, addressPtr, packet, length);
        }
    }
}