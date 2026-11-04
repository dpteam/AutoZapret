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

        /// <summary>Пошагово по цели: где именно рвётся — DNS, TCP или TLS.</summary>
        public List<TargetStage> Stages = new List<TargetStage>();

        /// <summary>То же самое ДО запуска движка: с чем сравниваем.</summary>
        public List<TargetStage> StagesBefore = new List<TargetStage>();

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

            List<ProbeTarget> targets = ConnectivityProbe.ZapretTargets();

            List<ProbeTarget> first = new List<ProbeTarget>();
            foreach (ProbeTarget t in targets)
            {
                first.Add(t);
                if (first.Count >= 2) break;
            }

            // Снимок «как без обхода» — иначе не с чем сравнивать результат.
            result.StagesBefore = TargetDiagnosis.Run(first, Math.Max(3, seconds) * 1000, cancel);

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

                // теперь то же самое, но с обходом: разница и есть вклад движка
                result.Stages = TargetDiagnosis.Run(first, Math.Max(3, seconds) * 1000, cancel);

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
                result.Engine = zapret.EngineOutput(1500);

                try
                {
                    WriteReport(result);
                    result.FilePath = OutputPath;
                }
                catch (Exception ex)
                {
                    Log.Exception("DIAG", "сохранение отчёта отладки", ex);
                }

                zapret.Stop();
                Thread.Sleep(1200);
            }

            result.Ok = true;
            return result;
        }

        /// <summary>
        /// Отчёт в один файл: шаги по целям, пробы и вывод winws. Так его можно
        /// прислать целиком, не собирая по кускам из лога.
        /// </summary>
        private static void WriteReport(DebugRunResult result)
        {
            List<string> lines = new List<string>();

            lines.Add("# отладочный прогон AutoZapret");
            lines.Add("# стратегия: " + result.StrategyId + " (" + result.StrategyName + ")");
            lines.Add("# проб прошло: " + result.OkCount + " из " + result.Total);
            lines.Add("");

            lines.Add("## по шагам без обхода");
            AddStages(lines, result.StagesBefore);

            lines.Add("");
            lines.Add("## по шагам с обходом");
            AddStages(lines, result.Stages);

            lines.Add("");
            lines.Add("## пробы (curl)");
            foreach (string p in result.Probes) lines.Add("  " + p);

            lines.Add("");
            lines.Add("## вывод winws (--debug=1)");
            foreach (string e in result.Engine) lines.Add(e);

            AtomicFile.WriteAllText(OutputPath, string.Join(Environment.NewLine, lines.ToArray()));
        }

        private static void AddStages(List<string> lines, List<TargetStage> stages)
        {
            if (stages.Count == 0)
            {
                lines.Add("  (нет данных)");
                return;
            }

            foreach (TargetStage stage in stages)
            {
                lines.Add("  " + stage.Name);
                lines.Add("    DNS: " + stage.Dns);
                lines.Add("    TCP: " + stage.Tcp);
                lines.Add("    TLS: " + stage.Tls);
                lines.Add("    итог: " + stage.Verdict());
            }
        }
    }
}