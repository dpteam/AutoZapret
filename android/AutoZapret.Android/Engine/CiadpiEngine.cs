using System;
using System.IO;
using System.Threading;
using Android.Content;
using Android.Content.PM;

namespace AutoZapret.Android.Engine
{
    /// <summary>
    /// Движок обхода на основе byedpi (ciadpi).
    ///
    /// Почему не свой C#-фейк. Он был написан и проверен, но на живой сети
    /// (МТС, Android 16) вёл себя нестабильно: соединение доходило до TLS и
    /// рвалось, а иногда не устанавливалось вовсе. Тот же телефон, та же сеть,
    /// тот же TTL 8 — ciadpi отвечает 200 за 0,5 с. Разница в том, что ciadpi
    /// собран из исходников, отлаженных на тысячах сетей, а наш фейк — первая
    /// версия.
    ///
    /// Лицензия: byedpi — MIT (github.com/hufrea/byedpi), копирование кода
    /// разрешено, но мы его не копируем: используем как отдельный процесс.
    /// Это важно и по другой причине: падение движка не уронит приложение.
    ///
    /// Схема:
    ///   hev → наш SOCKS5 (резолв через DoH, перехват DNS)
    ///       → ciadpi (desync) → интернет
    ///
    /// Наш прокси остаётся спереди, потому что только он умеет честный DNS:
    /// оператор подменяет ответы, и без DoH мы уходим в чёрную дыру 198.18.x.
    /// </summary>
    public sealed class CiadpiEngine
    {
        private Java.Lang.Process _process;
        private int _port;

        public bool IsRunning
        {
            get
            {
                Java.Lang.Process process = _process;

                return process != null && IsAlive(process);
            }
        }

        /// <summary>Порт, на котором слушает ciadpi (127.0.0.1).</summary>
        public int Port { get { return _port; } }

        /// <summary>
        /// Распаковать бинарник под текущую ABI и запустить.
        ///
        /// Возвращает null при успехе, иначе текст ошибки. Бинарник лежит в
        /// assets (в APK), а запускать можно только файл в filesDir — поэтому
        /// копируем при первом запуске.
        /// </summary>
        public string Start(Context context, int port, string extraArgs, Action<string> log)
        {
            // Всегда останавливаем предыдущий экземпляр: второй процесс на том
            // же порту падает с "accept: Invalid argument", и снаружи это
            // выглядит как «движок не работает».
            Stop();

            Thread.Sleep(200);

            try
            {
                string path = EnginePath(context);

                if (path == null) return "движок libciadpi.so не найден в каталоге библиотек";

                // Аргументы — только латиница: cmd-строка процесса не должна
                // зависеть от локали. Техника задаётся флагами byedpi.
                string args = "-p " + port + " -i 127.0.0.1 -x 0 " + (extraArgs ?? "");

                Java.Lang.ProcessBuilder builder = new Java.Lang.ProcessBuilder(
                    new[] { path, "-p", port.ToString(), "-i", "127.0.0.1", "-x", "0" });

                // Разбиваем extraArgs по пробелам: ProcessBuilder ждёт массив,
                // а не строку.
                if (!string.IsNullOrEmpty(extraArgs))
                {
                    foreach (string part in extraArgs.Split(' '))
                    {
                        if (part.Trim().Length > 0) builder.Command().Add(part.Trim());
                    }
                }

                builder.RedirectErrorStream(true);

                _process = builder.Start();
                _port = port;

                if (log != null) log("движок byedpi запущен на 127.0.0.1:" + port + " " + (extraArgs ?? ""));

                // Читаем stdout в фоне, чтобы процесс не встал на заполненном
                // буфере. Логи движка полезны при разборе.
                StartReader(log);

                return null;
            }
            catch (Exception ex)
            {
                return "ciadpi: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private void StartReader(Action<string> log)
        {
            Java.Lang.Process process = _process;

            if (process == null || log == null) return;

            Thread thread = new Thread(delegate ()
            {
                try
                {
                    using (Stream stream = process.InputStream)
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string line;

                        while ((line = reader.ReadLine()) != null) log("byedpi: " + line);
                    }
                }
                catch
                {
                }
            })
            {
                IsBackground = true,
                Name = "ciadpi-log"
            };

            thread.Start();
        }

        /// <summary>
        /// Остановить движок. Гарантированно: SIGTERM, ожидание, SIGKILL.
        ///
        /// ПОЧЕМУ НЕ ОДИН Destroy()
        ///   `Process.Destroy()` — это SIGTERM, и он НЕ гарантирует завершения.
        ///   Проверено на телефоне: после «выключить обход» процесс
        ///   `libciadpi.so` оставался жив (`ps -A`), держал порт и продолжал
        ///   перехватывать трафик, а кнопка показывала «выключено». Снаружи это
        ///   «обход не выключается».
        ///
        ///   Хуже: прежняя версия обнуляла `_process` ДО подтверждения смерти.
        ///   Если SIGTERM не сработал, дескриптор терялся, и убить процесс
        ///   больше было нечем — только перезапуском приложения.
        ///
        /// ПОРЯДОК
        ///   1. SIGTERM (Destroy) — движок успевает закрыть сокеты;
        ///   2. ждём до 1.5 с;
        ///   3. если жив — SIGKILL (DestroyForcibly) и ждём ещё;
        ///   4. дескриптор обнуляем ТОЛЬКО когда процесс действительно мёртв.
        /// </summary>
        public void Stop()
        {
            Java.Lang.Process process = _process;

            if (process == null)
            {
                // Процесса в поле нет, но он мог остаться от прошлой версии,
                // аварии или второго экземпляра сервиса. Ищем своих по /proc.
                KillStale();
                return;
            }

            try { process.Destroy(); } catch { }

            if (!WaitExit(process, 1500))
            {
                try { process.DestroyForcibly(); } catch { }
                WaitExit(process, 1500);
            }

            if (IsAlive(process))
            {
                // Не смогли даже SIGKILL — оставляем дескриптор, чтобы
                // следующий Stop() попробовал снова, а не потерял процесс.
                return;
            }

            _process = null;
        }

        /// <summary>Подождать завершения процесса. true — процесс мёртв.</summary>
        private static bool WaitExit(Java.Lang.Process process, int timeoutMs)
        {
            try
            {
                for (int i = 0; i < timeoutMs / 50; i++)
                {
                    if (!IsAlive(process)) return true;

                    Thread.Sleep(50);
                }
            }
            catch
            {
            }

            return !IsAlive(process);
        }

        /// <summary>
        /// Добить свои процессы `libciadpi.so`, оставшиеся без дескриптора.
        ///
        /// Свои — значит запущенные нашим процессом из нашего каталога библиотек:
        /// `cmdline` содержит полный путь `.../lib/libciadpi.so`, а `ppid`
        /// совпадает с нашим PID. Чужой движок (другое приложение) не трогаем:
        /// это не наша зона и убийство чужого процесса — не наше дело.
        ///
        /// Возвращает число убитых.
        /// </summary>
        public static int KillStale()
        {
            int killed = 0;

            try
            {
                int self = global::Android.OS.Process.MyPid();

                foreach (string dir in Directory.GetDirectories("/proc"))
                {
                    string name = Path.GetFileName(dir);

                    int pid;
                    if (!int.TryParse(name, out pid)) continue;
                    if (pid == self) continue;

                    string cmdline = ReadText(Path.Combine(dir, "cmdline"));

                    if (cmdline.IndexOf("libciadpi.so", StringComparison.Ordinal) < 0) continue;

                    if (ParentPid(Path.Combine(dir, "stat")) != self) continue;

                    try
                    {
                        // Signal.Kill — это SIGKILL (9). Число не принимается:
                        // в .NET for Android второй аргумент — перечисление.
                        global::Android.OS.Process.SendSignal(pid, global::Android.OS.Signal.Kill);
                        killed++;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return killed;
        }

        /// <summary>
        /// PID родителя из /proc/&lt;pid&gt;/stat.
        ///
        /// Формат: `pid (comm) state ppid ...`, где `comm` может содержать
        /// пробелы и скобки, поэтому разбираем по ПОСЛЕДНЕЙ скобке, а не
        /// регуляркой по всему файлу.
        /// </summary>
        private static int ParentPid(string statPath)
        {
            try
            {
                string stat = ReadText(statPath);

                int close = stat.LastIndexOf(')');
                if (close < 0) return -1;

                string rest = stat.Substring(close + 1).Trim();
                string[] parts = rest.Split(' ');

                // parts[0] — состояние, parts[1] — ppid
                if (parts.Length < 2) return -1;

                int ppid;
                return int.TryParse(parts[1], out ppid) ? ppid : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static string ReadText(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return "";
            }
        }

        private static bool IsAlive(Java.Lang.Process process)
        {
            try
            {
                // exitValue бросает, пока процесс жив — это и есть проверка.
                process.ExitValue();

                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Путь к бинарнику движка.
        ///
        /// Только из каталога нативных библиотек: с Android 10 приложению
        /// запрещено запускать файлы из своего каталога данных (W^X), попытка
        /// даёт "error=13, Permission denied". Каталог lib — read-only и
        /// исполняемый, поэтому движок лежит там как libciadpi.so.
        /// </summary>
        private static string EnginePath(Context context)
        {
            try
            {
                string dir = context.ApplicationInfo.NativeLibraryDir;

                if (string.IsNullOrEmpty(dir)) return null;

                string path = Path.Combine(dir, "libciadpi.so");

                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
