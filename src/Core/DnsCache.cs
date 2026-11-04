using System;
using System.Diagnostics;
using System.Text;

namespace AutoZapret.Core
{
    /// <summary>
    /// Сброс кэша DNS.
    ///
    /// Зачем это в программе про hosts и DNS: Windows держит ответы в кэше, и
    /// после правки hosts старый адрес продолжает использоваться, пока запись
    /// не истечёт. Человек поменял строку, нажал «Сохранить», открыл сайт — и
    /// видит старое поведение. Выглядит как «hosts не работает», хотя файл уже
    /// правильный. `ipconfig /flushdns` решает это за миллисекунды.
    ///
    /// То же после смены DNS-серверов: система продолжает отдавать ответы,
    /// полученные от прежнего (подменённого) DNS.
    ///
    /// Права не нужны — flushdns работает и без администратора, но вызываем
    /// его и в elevated-сценариях, чтобы не думать об этом на каждом вызове.
    /// </summary>
    public static class DnsCache
    {
        /// <summary>
        /// Сбросить кэш. Возвращает null при успехе, иначе причину — но вызывающий
        /// обычно просто пишет её в лог: сама по себе неудача не должна ломать
        /// основную операцию (hosts уже записан, и это главное).
        /// </summary>
        public static string Flush()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    // chcp 437: без него на русской Windows вывод приходит в OEM,
                    // и мы не сможем отличить успех от ошибки по тексту
                    Arguments = "/c chcp 437 >nul & ipconfig /flushdns",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();

                    p.WaitForExit(5000);

                    if (p.ExitCode == 0)
                    {
                        Log.Info("DNS", "кэш DNS сброшен");
                        return null;
                    }

                    string text = (error + " " + output).Trim();

                    Log.Warn("DNS", "не удалось сбросить кэш DNS (код " + p.ExitCode + "): " +
                                    (text.Length > 0 ? FirstLine(text) : "без вывода"));

                    return "не удалось сбросить кэш DNS";
                }
            }
            catch (Exception ex)
            {
                Log.Warn("DNS", "flushdns: " + ex.Message);
                return ex.Message;
            }
        }

        /// <summary>
        /// Сбросить и не мешать: основная операция важнее. Возвращает true,
        /// если кэш действительно сброшен (для строки статуса в интерфейсе).
        /// </summary>
        public static bool TryFlush()
        {
            return Flush() == null;
        }

        private static string FirstLine(string text)
        {
            foreach (string line in text.Split('\n'))
            {
                string t = line.Trim();
                if (t.Length > 0) return t.Length > 120 ? t.Substring(0, 120) : t;
            }

            return "";
        }
    }
}