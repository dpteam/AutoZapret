using System.Windows.Forms;

namespace AutoZapret.App
{
    /// <summary>
    /// Масштабирование интерфейса.
    ///
    /// Одна причина, по которой это отдельный класс: режим DPI в WinForms
    /// начиная с .NET 6 задаётся только вызовом <see cref="Application.SetHighDpiMode"/>
    /// (свойство проекта <c>ApplicationHighDpiMode</c> лишь генерирует
    /// <c>ApplicationConfiguration.Initialize()</c>, которую мы не зовём).
    ///
    /// Если этого не сделать, процесс остаётся SystemAware: при масштабе 125 %
    /// Windows рисует окно в 100 % и растягивает картинку — всё мыло. Именно
    /// это и было жалобой, а «в манифесте же прописано» не помогает: DPI там
    /// сознательно не указан, иначе WinForms ругается WFO0003 и режим может
    /// не примениться.
    ///
    /// PerMonitorV2, а не SystemAware: окно, перенесённое на второй монитор
    /// с другим масштабом, остаётся резким.
    /// </summary>
    internal static class DpiSetup
    {
        public const HighDpiMode Mode = HighDpiMode.PerMonitorV2;

        /// <summary>
        /// Включает режим. Возвращает false, если Windows/фреймворк его не приняли
        /// (например, режим уже был задан манифестом или окно успело создаться).
        /// </summary>
        public static bool Apply()
        {
            try { return Application.SetHighDpiMode(Mode); }
            catch { return false; }
        }
    }
}