using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AutoZapret.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutoZapret.App
{
    /// <summary>
    /// Окно-хост. Содержит ровно один контрол - WebView2 на всё поле.
    /// Вся вёрстка живёт в web\ui.html и вшита в exe.
    ///
    /// Почему NavigateToString, а не виртуальный хост: страница целиком в памяти,
    /// нет ни временных файлов, ни доступа к файловой системе из JS.
    ///
    /// Закрытие окна = сворачивание в трей (если так настроено). Выход - только
    /// через меню трея или пункт «Выход» в интерфейсе.
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly WebView2 _web;
        private readonly WebBridge _bridge;
        private bool _webReady;
        private bool _reallyClosing;

        /// <summary>Пользователь просит выйти совсем (а не свернуть).</summary>
        public event Action ExitRequested;

        /// <summary>Окно спрятано в трей / показано.</summary>
        public event Action<bool> VisibilityChanged;

        public MainForm()
        {
            Text = AppInfo.ProductName;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 720);
            MinimumSize = new Size(820, 620);
            BackColor = Color.FromArgb(0x0D, 0x0F, 0x1A);
            Font = new Font("Segoe UI", 9f);
            ShowInTaskbar = true;
            KeyPreview = true;

            try
            {
                string ico = Path.Combine(Paths.BaseDir, "AutoZapret.ico");
                Icon = File.Exists(ico) ? new Icon(ico) : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            _bridge = new WebBridge(PostToPage);

            _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(_web);

            Shown += delegate { BeginInit(); };
            FormClosing += OnFormClosing;
            FormClosed += delegate { try { _web.Dispose(); } catch { } };
        }

        public WebBridge Bridge { get { return _bridge; } }

        public bool IsWebReady { get { return _webReady; } }

        // ------------------------------------------------------------- webview init

        private async void BeginInit()
        {
            try
            {
                Directory.CreateDirectory(Paths.WebViewDataDir);

                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewDataDir);
                await _web.EnsureCoreWebView2Async(env);

                CoreWebView2 core = _web.CoreWebView2;

                // Интерфейс - не браузер: отключаем всё лишнее
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;

                // внешние ссылки - в системный браузер, а не внутри окна
                core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    e.Handled = true;
                    OpenExternal(e.Uri);
                };
                core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs e)
                {
                    // разрешаем только наш собственный about: / data: контент
                    if (e.Uri != null &&
                        !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) &&
                        !e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Cancel = true;
                        OpenExternal(e.Uri);
                    }
                };

                core.WebMessageReceived += OnWebMessage;

                core.NavigateToString(BuildHtml());
                _webReady = true;

                Log.Info("UI", "webview2 " + core.Environment.BrowserVersionString + " готов");
            }
            catch (Exception ex)
            {
                Log.Exception("UI", "webview2 не запустился", ex);
                MessageBox.Show(
                    "Не удалось запустить WebView2:\r\n" + ex.Message +
                    "\r\n\r\nУстановите Microsoft Edge WebView2 Runtime:\r\n" +
                    "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitRequested?.Invoke();
            }
        }

        private string BuildHtml()
        {
            string html = Resources.Text(Resources.UiHtml);
            if (string.IsNullOrEmpty(html))
            {
                html = "<!doctype html><html><body style='font:14px Segoe UI;background:#0D0F1A;color:#DDE4F5;padding:32px'>" +
                       "<h1>AutoZapret</h1><p>Ресурсы интерфейса не вшиты. Пересоберите через build.cmd.</p></body></html>";
            }

            html = html.Replace("/*{{THEME}}*/", Resources.Text(Resources.ThemeCss));
            html = html.Replace("//{{APP}}", Resources.Text(Resources.AppJs));
            html = html.Replace("{{VERSION}}", AppInfo.Version);
            return html;
        }

        // --------------------------------------------------------------- messaging

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string json;
            try { json = e.WebMessageAsJson; }
            catch (Exception ex) { Log.Warn("UI", "webmessage: " + ex.Message); return; }

            _bridge.Handle(json);
        }

        private void PostToPage(Dictionary<string, object> payload)
        {
            string json = Json.Stringify(payload);
            Action deliver = delegate
            {
                try
                {
                    CoreWebView2 core = _web == null ? null : _web.CoreWebView2;
                    if (core != null) core.PostWebMessageAsJson(json);
                }
                catch (Exception ex) { Log.Warn("UI", "post: " + ex.Message); }
            };

            if (IsHandleCreated && InvokeRequired) BeginInvoke(deliver);
            else deliver();
        }

        private static void OpenExternal(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("UI", "open external: " + ex.Message); }
        }

        // ------------------------------------------------------------- visibility

        /// <summary>Показать и поднять окно (из трея или по второму запуску).</summary>
        public void ShowWindow()
        {
            if (InvokeRequired) { BeginInvoke(new Action(ShowWindow)); return; }

            ShowInTaskbar = true;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            VisibilityChanged?.Invoke(true);
        }

        public void HideToTray()
        {
            if (InvokeRequired) { BeginInvoke(new Action(HideToTray)); return; }

            Hide();
            ShowInTaskbar = false;
            VisibilityChanged?.Invoke(false);
        }

        public void NavigateTo(string screen)
        {
            if (string.IsNullOrEmpty(screen)) return;
            _bridge.Send(WebBridge.Msg("navigate", "screen", screen));
        }

        /// <summary>Выход по-настоящему (не сворачивание).</summary>
        public void CloseForReal()
        {
            if (InvokeRequired) { BeginInvoke(new Action(CloseForReal)); return; }
            _reallyClosing = true;
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_reallyClosing) return;

            if (Settings.Current.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            // закрытие не по воле пользователя (shutdown, task manager) - выходим
            _reallyClosing = true;
        }
    }
}
