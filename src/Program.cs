using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KennyPCOptimizer
{
    static class Program
    {
        private static WebServer _server;
        private static Process _uiProc;
        private static string _appUrl;
        private static NotifyIcon _trayIcon;
        private static System.Threading.Timer _autoRamTimer;
        private static volatile bool _shuttingDown;

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool selfCheck = false;
            foreach (string a in args)
            {
                if (string.Equals(a, "--self-check", StringComparison.OrdinalIgnoreCase))
                {
                    selfCheck = true;
                }
            }

            Config.EnsureAppDirs();
            Directory.CreateDirectory(Config.RuntimeProfileDir);

            int port = GetFreeTcpPort();
            string token = GenerateHexToken(16);
            _appUrl = string.Format("http://127.0.0.1:{0}/?token={1}", port, token);

            _server = new WebServer(port, token, Application.ExecutablePath, delegate
            {
                ShutdownAll();
                Environment.Exit(0);
            });

            try
            {
                _server.Start();
            }
            catch (Exception ex)
            {
                if (!selfCheck)
                {
                    MessageBox.Show("Failed to start embedded HTTP server: " + ex.Message, Config.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return 1;
            }

            if (selfCheck)
            {
                // Verify /api/health responds 200 OK purely from in-process C# server
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(string.Format("http://127.0.0.1:{0}/api/health", port));
                    req.Timeout = 3000;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        if ((int)resp.StatusCode != 200)
                        {
                            ShutdownAll();
                            return 2;
                        }
                    }
                    // Also verify root embedded HTML serves cleanly
                    HttpWebRequest reqRoot = (HttpWebRequest)WebRequest.Create(string.Format("http://127.0.0.1:{0}/", port));
                    reqRoot.Timeout = 3000;
                    using (HttpWebResponse respRoot = (HttpWebResponse)reqRoot.GetResponse())
                    {
                        if ((int)respRoot.StatusCode != 200)
                        {
                            ShutdownAll();
                            return 3;
                        }
                    }
                }
                catch
                {
                    ShutdownAll();
                    return 4;
                }
                ShutdownAll();
                return 0;
            }

            SetupTrayIcon();
            _autoRamTimer = new System.Threading.Timer(delegate
            {
                if (_shuttingDown) return;
                MemoryOptimizer.CheckAutoOptimizerTick(delegate(string msg)
                {
                    try
                    {
                        Dictionary<string, object> s = SystemInfo.LoadSettings();
                        bool notify = !s.ContainsKey("ram_tray_notifications") || Convert.ToBoolean(s["ram_tray_notifications"]);
                        if (notify && _trayIcon != null)
                        {
                            _trayIcon.ShowBalloonTip(3500, Config.AppName, msg, ToolTipIcon.Info);
                        }
                    }
                    catch { }
                });
            }, null, 15000, 15000);
            DateTime launchTime = DateTime.UtcNow;
            LaunchAppWindow();

            if (_uiProc != null)
            {
                Thread watcher = new Thread(delegate()
                {
                    try
                    {
                        _uiProc.WaitForExit();
                        TimeSpan lived = DateTime.UtcNow - launchTime;
                        if (lived.TotalSeconds > 2.5 && !_shuttingDown)
                        {
                            ShutdownAll();
                            Environment.Exit(0);
                        }
                    }
                    catch { }
                });
                watcher.IsBackground = true;
                watcher.Start();
            }

            Application.ApplicationExit += delegate { ShutdownAll(); };
            Application.Run();
            return 0;
        }

        private static int GetFreeTcpPort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static string GenerateHexToken(int numBytes)
        {
            byte[] buf = new byte[numBytes];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(buf);
            }
            StringBuilder sb = new StringBuilder(numBytes * 2);
            foreach (byte b in buf) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static void LaunchAppWindow()
        {
            string[] appBrowsers = new string[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"
            };

            foreach (string browserExe in appBrowsers)
            {
                if (!File.Exists(browserExe)) continue;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = browserExe;
                    psi.Arguments = string.Format(
                        "--app=\"{0}\" --user-data-dir=\"{1}\" --window-size=1360,880 --no-first-run --no-default-browser-check --disable-features=msEdgeSidebarV2",
                        _appUrl,
                        Config.RuntimeProfileDir
                    );
                    psi.UseShellExecute = false;
                    _uiProc = Process.Start(psi);
                    return;
                }
                catch { }
            }

            try { Process.Start(_appUrl); } catch { }
        }

        private static void SetupTrayIcon()
        {
            Icon appIcon = null;
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("assets.app_icon.ico"))
                {
                    if (s != null) appIcon = new Icon(s);
                }
            }
            catch { }
            if (appIcon == null) appIcon = SystemIcons.Shield;

            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("Open Kenny PC Optimizer", delegate { LaunchAppWindow(); });
            menu.MenuItems.Add("Boost RAM Now (Safe)", delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    System.Collections.Generic.Dictionary<string, object> res = MemoryOptimizer.CleanMemory(
                        new System.Collections.Generic.List<string> { "working_set", "low_standby", "system_file_cache", "combine_memory" },
                        false, null);
                    try
                    {
                        if (_trayIcon != null)
                        {
                            _trayIcon.ShowBalloonTip(3500, "RAM Boost Complete",
                                string.Format("Freed {0} RAM + {1} Standby Cache", res["ram_freed_human"], res["standby_freed_human"]),
                                ToolTipIcon.Info);
                        }
                    }
                    catch { }
                });
            });
            menu.MenuItems.Add("Open Backup Vault", delegate
            {
                BackupManager.OpenBackupFolder(null, null);
            });
            if (!SystemInfo.IsAdmin())
            {
                menu.MenuItems.Add("Restart as Administrator", delegate
                {
                    if (SystemInfo.TriggerUacElevation(Application.ExecutablePath))
                    {
                        ShutdownAll();
                        Application.Exit();
                    }
                });
            }
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Exit", delegate
            {
                ShutdownAll();
                Application.Exit();
            });

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = appIcon;
            _trayIcon.Text = Config.AppName;
            _trayIcon.ContextMenu = menu;
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += delegate { LaunchAppWindow(); };
        }

        private static void ShutdownAll()
        {
            _shuttingDown = true;
            try
            {
                if (_autoRamTimer != null)
                {
                    _autoRamTimer.Dispose();
                    _autoRamTimer = null;
                }
            }
            catch { }
            try
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
            }
            catch { }
            try
            {
                if (_uiProc != null && !_uiProc.HasExited) _uiProc.CloseMainWindow();
            }
            catch { }
            try
            {
                if (_server != null)
                {
                    _server.Dispose();
                    _server = null;
                }
            }
            catch { }
        }
    }
}
