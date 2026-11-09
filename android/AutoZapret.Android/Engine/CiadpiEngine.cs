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

        public bool IsRunning { get { return _process != null; } }

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

        public void Stop()
        {
            Java.Lang.Process process = _process;
            _process = null;

            if (process == null) return;

            try { process.Destroy(); } catch { }

            // Ждём завершения недолго: порт должен освободиться до следующего
            // запуска, иначе новый экземпляр не сможет занять его.
            try
            {
                for (int i = 0; i < 20; i++)
                {
                    if (!IsAlive(process)) break;

                    Thread.Sleep(50);
                }
            }
            catch
            {
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
