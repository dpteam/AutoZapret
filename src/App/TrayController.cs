using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AutoZapret.Core;

namespace AutoZapret.App
{
    public enum TrayState
    {
        Off,        // обход выключен
        On,         // обход работает
        Busy,       // запускается / останавливается / обновляется
        Error       // ошибка, требует внимания
    }

    /// <summary>
    /// Иконка в трее.
    ///
    /// Ключевое решение: НЕ три набора иконок, а одна базовая + генерируемый оверлей
    /// (цветная точка в правом нижнем углу). Меньше ассетов, невозможно рассинхронизировать.
    ///
    /// Все обращения к NotifyIcon идут через UI-поток: handle умирает при завершении
    /// приложения, и вызов из фонового потока даёт InvalidOperationException.
    /// </summary>
    public sealed class TrayController : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly Icon _baseIcon;
        private readonly Icon _offIcon;
        private readonly Icon _onIcon;
        private readonly Icon _busyIcon;
        private readonly Icon _errorIcon;
        private readonly Control _sync;

        private TrayState _state = TrayState.Off;
        private bool _disposed;

        // ---- события для хоста
        public event Action ShowRequested;
        public event Action ToggleRequested;
        public event Action SettingsRequested;
        public event Action LogsRequested;
        public event Action ExitRequested;

        public TrayController(Control syncControl)
        {
            _sync = syncControl;

            _baseIcon = LoadBaseIcon();

            _offIcon = _baseIcon;
            _onIcon = WithDot(_baseIcon, Color.FromArgb(0x00, 0xD6, 0x8F));   // зелёный
            _busyIcon = WithDot(_baseIcon, Color.FromArgb(0xF0, 0xB4, 0x29)); // жёлтый
            _errorIcon = WithDot(_baseIcon, Color.FromArgb(0xE7, 0x4C, 0x3C)); // красный

            _icon = new NotifyIcon
            {
                Icon = _offIcon,
                Text = BuildTooltip("Обход выключен"),
                Visible = true,
                ContextMenuStrip = BuildMenu()
            };
            _icon.DoubleClick += delegate { Raise(ShowRequested); };
            _icon.MouseClick += OnMouseClick;
        }

        public TrayState State
        {
            get { return _state; }
        }

        /// <summary>Поставить состояние и обновить иконку/подсказку. Можно из любого потока.</summary>
        public void SetState(TrayState state, string detail = null)
        {
            _state = state;
            Invoke(() =>
            {
                if (_disposed) return;
                _icon.Icon = IconFor(state);
                _icon.Text = BuildTooltip(DefaultText(state, detail));
            });
        }

        /// <summary>Всплывающее уведомление. Не чаще, чем нужно: Windows их копит.</summary>
        public void Notify(string title, string text, ToolTipIcon kind = ToolTipIcon.Info)
        {
            Invoke(() =>
            {
                if (_disposed) return;
                try { _icon.ShowBalloonTip(4000, title, text, kind); }
                catch { }
            });
        }

        public void SetToggleText(string text)
        {
            Invoke(() =>
            {
                if (_disposed) return;
                ToolStripItem item = FindItem("toggle");
                if (item != null) item.Text = text;
            });
        }

        public void SetTooltip(string text)
        {
            Invoke(() =>
            {
                if (_disposed) return;
                _icon.Text = BuildTooltip(text);
            });
        }

        // ------------------------------------------------------------------ menu

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip { ShowImageMargin = false };

            ToolStripMenuItem toggle = new ToolStripMenuItem("▶ Включить обход") { Name = "toggle" };
            toggle.Click += delegate { Raise(ToggleRequested); };
            menu.Items.Add(toggle);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem show = new ToolStripMenuItem("Открыть окно");
            show.Click += delegate { Raise(ShowRequested); };
            menu.Items.Add(show);

            ToolStripMenuItem settings = new ToolStripMenuItem("Настройки");
            settings.Click += delegate { Raise(SettingsRequested); };
            menu.Items.Add(settings);

            ToolStripMenuItem logs = new ToolStripMenuItem("Логи");
            logs.Click += delegate { Raise(LogsRequested); };
            menu.Items.Add(logs);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("Выход");
            exit.Click += delegate { Raise(ExitRequested); };
            menu.Items.Add(exit);

            return menu;
        }

        private ToolStripItem FindItem(string name)
        {
            if (_icon.ContextMenuStrip == null) return null;
            foreach (ToolStripItem item in _icon.ContextMenuStrip.Items)
                if (string.Equals(item.Name, name, StringComparison.Ordinal)) return item;
            return null;
        }

        private void OnMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Raise(ShowRequested);
        }

        private void Raise(Action handler)
        {
            if (handler == null) return;
            try { handler(); } catch (Exception ex) { Log.Exception("TRAY", "обработчик", ex); }
        }

        // ------------------------------------------------------------- rendering

        private static Icon LoadBaseIcon()
        {
            try
            {
                string path = System.IO.Path.Combine(Paths.BaseDir, "AutoZapret.ico");
                if (System.IO.File.Exists(path)) return new Icon(path);

                Icon fromExe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (fromExe != null) return fromExe;
            }
            catch { }

            // последний шанс: рисуем квадрат, лишь бы не упасть
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (Brush brush = new SolidBrush(Color.FromArgb(0x55, 0xAA, 0xFF)))
                        g.FillRectangle(brush, 1, 1, 14, 14);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        /// <summary>Рисует цветную точку поверх базовой иконки.</summary>
        private static Icon WithDot(Icon baseIcon, Color color)
        {
            try
            {
                int size = Math.Max(16, baseIcon.Width);
                using (Bitmap canvas = new Bitmap(size, size))
                {
                    using (Graphics g = Graphics.FromImage(canvas))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        g.DrawIcon(baseIcon, new Rectangle(0, 0, size, size));

                        int dot = Math.Max(6, size / 3);
                        int margin = Math.Max(1, size / 16);
                        Rectangle rect = new Rectangle(size - dot - margin, size - dot - margin, dot, dot);

                        using (Brush fill = new SolidBrush(color))
                            g.FillEllipse(fill, rect);
                        using (Pen outline = new Pen(Color.FromArgb(210, 0, 0, 0), 1f))
                            g.DrawEllipse(outline, rect);
                    }

                    IntPtr handle = canvas.GetHicon();
                    try { return (Icon)Icon.FromHandle(handle).Clone(); }
                    finally { DestroyIcon(handle); }
                }
            }
            catch
            {
                return baseIcon;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private Icon IconFor(TrayState state)
        {
            switch (state)
            {
                case TrayState.On: return _onIcon;
                case TrayState.Busy: return _busyIcon;
                case TrayState.Error: return _errorIcon;
                default: return _offIcon;
            }
        }

        private static string DefaultText(TrayState state, string detail)
        {
            if (!string.IsNullOrEmpty(detail)) return detail;
            switch (state)
            {
                case TrayState.On: return "Обход включён";
                case TrayState.Busy: return "Подождите…";
                case TrayState.Error: return "Ошибка — откройте окно";
                default: return "Обход выключен";
            }
        }

        /// <summary>Win32 обрезает подсказку трея до 127 символов.</summary>
        private static string BuildTooltip(string text)
        {
            string s = string.IsNullOrEmpty(text) ? AppInfo.ProductName : text;
            if (s.Length > 120) s = s.Substring(0, 117) + "…";
            return s;
        }

        private void Invoke(Action action)
        {
            if (action == null) return;
            try
            {
                if (_sync != null && _sync.IsHandleCreated && _sync.InvokeRequired)
                    _sync.BeginInvoke(action);
                else
                    action();
            }
            catch (InvalidOperationException) { /* handle умер при выходе (ObjectDisposedException - тоже сюда) */ }
            catch (Exception ex) { Log.Warn("TRAY", "invoke: " + ex.Message); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_icon != null)
                {
                    _icon.Visible = false;
                    if (_icon.ContextMenuStrip != null) _icon.ContextMenuStrip.Dispose();
                    _icon.Dispose();
                }
            }
            catch { }

            foreach (Icon icon in new[] { _onIcon, _busyIcon, _errorIcon })
            {
                try { if (icon != null && !ReferenceEquals(icon, _baseIcon)) icon.Dispose(); } catch { }
            }
            try { if (_baseIcon != null) _baseIcon.Dispose(); } catch { }
        }
    }
}
