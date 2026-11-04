using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using AutoZapret.Core;
namespace AutoZapret.App
{
    /// <summary>
    /// Точка входа.
    ///
    /// Режимы:
    ///   (без аргументов)  - обычный запуск, окно + трей
    ///   --minimized       - стартовать свёрнутым в трей (для автозапуска)
    ///   --selftest        - прогон проверок, пишет selftest.txt, код возврата 0/1
    ///   --version         - напечатать версию и выйти
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Paths.EnsureDirectories();
            Log.MinLevel = LogLevel.Info;
            Log.Info("APP", AppInfo.Summary());

            bool minimized = HasFlag(args, "--minimized");
            if (HasFlag(args, "--version"))
            {
                Console.WriteLine(AppInfo.Name + " " + AppInfo.Version);
                return 0;
            }
            if (HasFlag(args, "--probe")) return Diag.DiagCommand.RunProbe(args);
            if (HasFlag(args, "--strategies")) return Diag.DiagCommand.RunStrategies(args);
            if (HasFlag(args, "--update-lists")) return Diag.DiagCommand.RunUpdateLists(args);
            if (HasFlag(args, "--fakes")) return Diag.DiagCommand.RunFakes(args);
            if (HasFlag(args, "--test-proxy")) return Diag.DiagCommand.RunTestProxy(args);
            if (HasFlag(args, "--check-updates")) return Diag.DiagCommand.RunCheckUpdates(args);
            if (HasFlag(args, "--hosts")) return Diag.DiagCommand.RunHosts(args);
            if (HasFlag(args, "--scores")) return Diag.DiagCommand.RunScores(args);
            if (HasFlag(args, "--auto-once")) return Diag.DiagCommand.RunAutoOnce(args);
            if (HasFlag(args, "--import-fakes")) return Diag.DiagCommand.RunImportFakes(args);
            if (HasFlag(args, "--diagnose")) return Diag.DiagCommand.RunDiagnose(args);
            if (HasFlag(args, "--tcp16")) return Diag.DiagCommand.RunTcp16(args);
        if (HasFlag(args, "--diag-zip")) return Diag.DiagCommand.RunDiagZip(args);
        if (HasFlag(args, "--presets")) return Diag.DiagCommand.RunPresets(args);
            if (HasFlag(args, "--autostart")) return AutoStartManager.RunCli(args);
            if (HasFlag(args, "--sha256")) return Diag.DiagCommand.RunSha256(args);
            if (HasFlag(args, "--gen-fake")) return Diag.DiagCommand.RunGenFake(args);
            if (HasFlag(args, "--install-engine")) return Diag.DiagCommand.RunInstallEngine(args);
            if (HasFlag(args, "--scores-reset")) return Diag.DiagCommand.RunScoresReset(args);
            if (HasFlag(args, "--hosts-restore")) return Diag.DiagCommand.RunHostsRestore(args);
            if (HasFlag(args, "--update-engine")) return Diag.DiagCommand.RunUpdateEngine(args);
            if (HasFlag(args, "--deep")) return Diag.DiagCommand.RunDeep(args);
            if (HasFlag(args, "--selftest")) return SelfTest.Run(HasFlag(args, "--require-engine"));
            if (HasFlag(args, "--test-all")) return Diag.DiagCommand.RunTestAll(args);
            if (HasFlag(args, "--test-winws"))
            {
                if (!Elevation.IsElevated)
                {
                    Console.WriteLine("ОТКАЗ: нужны права администратора (winws.exe имеет манифест requireAdministrator).");
                    Console.WriteLine("       Запустите из консоли администратора или добавьте --elevate.");
                    return 4;
                }
                return SelfTest.RunWinws(args);
            }
            if (HasFlag(args, "--elevate"))
            {
                // запрошенная явно элевация: перезапускаемся и выходим
                if (!Elevation.IsElevated)
                {
                    if (Elevation.RestartElevated(StripFlag(args, "--elevate"))) return 0;
                    return 4;
                }
            }

            string platformProblem = AppInfo.PlatformProblem();
            if (platformProblem != null)
            {
                Log.Error("APP", platformProblem);
                MessageBox.Show(platformProblem, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (SingleInstance single = new SingleInstance())
            {
                // --restarted: нас запустил предыдущий экземпляр, чтобы мы получили
                // права администратора. Он ещё несколько сотен миллисекунд держит
                // мьютекс, поэтому ждём его выхода, а не выходим сами.
                bool restarted = HasFlag(args, "--restarted");
                int waitMs = restarted ? 8000 : 0;

                if (!single.TryAcquire(waitMs))
                {
                    Log.Info("APP", "уже запущен другой экземпляр - показал его и выхожу");
                    return 0;
                }

                if (restarted) Log.Info("APP", "экземпляр перезапущен с повышением прав");

                using (AppHost host = new AppHost())
                {
                    single.ActivationRequested += host.ShowWindow;
                    host.Run(minimized);
                }
            }

            Log.Info("APP", "выход");
            return 0;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Убирает флаг из списка аргументов (для передачи остальных в перезапуск).</summary>
        private static string StripFlag(string[] args, string flag)
        {
            if (args == null) return "";
            List<string> kept = new List<string>();
            foreach (string a in args)
                if (!string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) kept.Add(a);
            return string.Join(" ", kept.ToArray());
        }
    }
}
