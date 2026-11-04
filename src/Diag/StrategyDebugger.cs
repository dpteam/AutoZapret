using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutoZapret.Core;
using AutoZapret.Zapret;

namespace AutoZapret.Diag
{
    /// <summary>Итог отладочного прогона одной стратегии.</summary>
    public sealed class DebugRunResult
    {
        public bool Ok;
        public string Error = "";

        public string StrategyId = "";
        public string StrategyName = "";

        public int OkCount;
        public int Total;

        /// <summary>Что winws написал про обработку пакетов.</summary>
        public List<string> Engine = new List<string>();

        /// <summary>Результаты проб: «youtube: FAIL TLS».</summary>
        public List<string> Probes = new List<string>();

        /// <summary>Куда сохранён полный вывод (попадает в архив поддержки).</summary>
        public string FilePath = "";

        public string FileName
        {
            get { return string.IsNullOrEmpty(FilePath) ? "" : Path.GetFileName(FilePath); }
        }
    }

    /// <summary>
    /// Отладочный прогон: одна стратегия, `--debug=1`, пара проб, весь вывод winws.
    ///
    /// Зачем отдельный сценарий. Обычный перебор отвечает «0/4», и на этом всё:
    /// непонятно, winws не совпал с трафиком или совпал, но DPI не обманулся.
    /// С `--debug=1` winws сам пишет, что сделал с каждым соединением. Это и
    /// есть ответ, которого не хватало.
    /// </summary>
    public static class StrategyDebugger
    {
        /// <summary>Файл, который кладём в data\state и показываем в архиве.</summary>
        public static string OutputPath
        {
            get { return Path.Combine(Paths.StateDir, "winws-debug.txt"); }
        }

        public static DebugRunResult Run(ZapretService zapret, string strategyId,
                                         int seconds, CancellationToken cancel)
        {
            DebugRunResult result = new DebugRunResult { StrategyId = strategyId };
            result.Total = 2;

            Strategy strategy = zapret.Strategies.Find(strategyId);
            result.StrategyName = strategy != null ? strategy.Name : strategyId;

            // Проба должна видеть настоящую сеть: сначала гасим всё, что осталось
            zapret.Stop();
            Thread.Sleep(1200);

            string error = zapret.StartWithDebug(strategyId, 1);
            if (error != null)
            {
                result.Error = error;
                return result;
            }

            try
            {
                // даём winws загрузить списки и встать на драйвер
                Thread.Sleep(1500);

                ConnectivityProbe probe = new ConnectivityProbe();
                List<ProbeTarget> targets = ConnectivityProbe.ZapretTargets();

                int taken = 0;
                foreach (ProbeTarget t in targets)
                {
                    if (cancel.IsCancellationRequested) break;

                    // по одной: одна цель — это одно соединение, debug-вывод читается
                    ProbeResult r = probe.CheckOne(t, Math.Max(4, seconds));
                    result.Probes.Add(r.ToLine());

                    if (r.Ok) result.OkCount++;
                    result.Total = ++taken;

                    if (taken >= 2) break;
                }

                Thread.Sleep(1000);
            }
            finally
            {
                // Вывод забираем ДО остановки: после Stop() процесс уже мёртв,
                // но строки продолжают приходить — даём им осесть.
                result.Engine = zapret.EngineOutput(400);

                try { zapret.SaveEngineOutput(OutputPath); result.FilePath = OutputPath; }
                catch (Exception ex) { Log.Exception("DIAG", "сохранение вывода winws", ex); }

                zapret.Stop();
                Thread.Sleep(1200);
            }

            result.Ok = true;
            return result;
        }
    }
}