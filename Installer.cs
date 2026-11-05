using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: AssemblyTitle("T2 Hardware Control Center Setup")]
[assembly: AssemblyProduct("T2 Hardware Control Center")]
[assembly: AssemblyCompany("Peter Karlsson")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace MacAirControlSetup
{
    public class SetupWindow : Window
    {
        private TextBox _txtInstallDir;
        private CheckBox _chkDriver;
        private CheckBox _chkShortcuts;
        private CheckBox _chkAutoStart;
        private CheckBox _chkLaunchNow;
        private ProgressBar _progressBar;
        private TextBox _txtLog;
        private Button _btnInstall;
        private Button _btnUninstall;

        private readonly string _version;

        // Dark Gold Palette
        private static readonly Color ColGold    = Color.FromRgb(229, 184, 66);  // #E5B842 Sovereign Gold
        private static readonly Color ColCopper  = Color.FromRgb(217, 138, 43);  // #D98A2B Burnished Gold
        private static readonly Color ColChamp   = Color.FromRgb(238, 210, 125); // #EED27D Champagne Gold
        private static readonly Color ColCrimson = Color.FromRgb(214, 54, 56);   // #D63638 Crimson

        private static readonly Brush BgDeep      = new SolidColorBrush(Color.FromArgb(252, 7, 8, 11));
        private static readonly Brush CardBg      = new SolidColorBrush(Color.FromArgb(238, 15, 16, 21));
        private static readonly Brush SubCardBg   = new SolidColorBrush(Color.FromArgb(230, 21, 23, 29));
        private static readonly Brush GoldBorder  = new SolidColorBrush(Color.FromArgb(100, 212, 175, 55));
        private static readonly Brush MutedBorder = new SolidColorBrush(Color.FromArgb(80, 78, 70, 54));
        private static readonly Brush TextPrimary = new SolidColorBrush(Color.FromRgb(242, 236, 222));
        private static readonly Brush TextMuted   = new SolidColorBrush(Color.FromRgb(156, 146, 126));

        public SetupWindow(string version, bool autoUninstall)
        {
            _version = version;
            Title = "T2 Hardware Control Center Setup v" + _version;
            Width = 680;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;

            BuildUi();

            if (autoUninstall)
            {
                Loaded += (s, e) => RunUninstallAsync();
            }
        }

        private void BuildUi()
        {
            LinearGradientBrush borderGrad = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            borderGrad.GradientStops.Add(new GradientStop(ColGold, 0.0));
            borderGrad.GradientStops.Add(new GradientStop(Color.FromRgb(115, 88, 34), 0.5));
            borderGrad.GradientStops.Add(new GradientStop(ColCopper, 1.0));

            Border outer = new Border
            {
                Background = BgDeep,
                BorderBrush = borderGrad,
                BorderThickness = new Thickness(1.6),
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(10),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 22, ShadowDepth = 4, Opacity = 0.85 }
            };

            DockPanel root = new DockPanel();

            // 1. Title Bar
            Border titleBar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(250, 10, 11, 14)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 212, 175, 55)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(13, 13, 0, 0),
                Padding = new Thickness(18, 12, 18, 12)
            };
            titleBar.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            DockPanel titleDock = new DockPanel();
            StackPanel spTraffic = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Border dotClose = new Border
            {
                Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5),
                Background = new SolidColorBrush(Color.FromRgb(255, 95, 86)),
                Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, ToolTip = "Close Setup"
            };
            dotClose.MouseLeftButtonDown += (s, e) => { e.Handled = true; Close(); };
            Border dotMin = new Border
            {
                Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5),
                Background = new SolidColorBrush(Color.FromRgb(255, 189, 46)),
                Margin = new Thickness(0, 0, 14, 0), Cursor = Cursors.Hand, ToolTip = "Minimize"
            };
            dotMin.MouseLeftButtonDown += (s, e) => { e.Handled = true; WindowState = WindowState.Minimized; };
            spTraffic.Children.Add(dotClose);
            spTraffic.Children.Add(dotMin);

            StackPanel spTitle = new StackPanel();
            StackPanel spTitleRow = new StackPanel { Orientation = Orientation.Horizontal };
            spTitleRow.Children.Add(new TextBlock
            {
                Text = "T2 HARDWARE CONTROL CENTER ",
                FontSize = 15, FontWeight = FontWeights.Bold, Foreground = TextPrimary
            });
            spTitleRow.Children.Add(new TextBlock
            {
                Text = "// INSTALLER v" + _version,
                FontSize = 15, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(ColGold)
            });
            spTitle.Children.Add(spTitleRow);
            spTitle.Children.Add(new TextBlock
            {
                Text = "THERMAL FAN CURVE, PROCESSOR TURBO & 80% BATTERY LIMITER FOR WINDOWS",
                FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 10.5, Foreground = TextMuted,
                Margin = new Thickness(0, 2, 0, 0)
            });
            spTraffic.Children.Add(spTitle);
            titleDock.Children.Add(spTraffic);
            titleBar.Child = titleDock;

            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);

            // 2. Main Body
            StackPanel body = new StackPanel { Margin = new Thickness(22, 16, 22, 16) };

            // Target Path Card
            Border pathCard = new Border
            {
                Background = CardBg, BorderBrush = GoldBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            StackPanel spPath = new StackPanel();
            spPath.Children.Add(new TextBlock
            {
                Text = "INSTALLATION DIRECTORY:",
                FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColGold), Margin = new Thickness(0, 0, 0, 6)
            });
            _txtInstallDir = new TextBox
            {
                Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MacBookAirControlCenter"),
                FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 12.5,
                Background = SubCardBg, Foreground = TextPrimary, BorderBrush = MutedBorder,
                Padding = new Thickness(8, 6, 8, 6)
            };
            spPath.Children.Add(_txtInstallDir);
            pathCard.Child = spPath;
            body.Children.Add(pathCard);

            // Options Card
            Border optCard = new Border
            {
                Background = CardBg, BorderBrush = GoldBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 12)
            };
            StackPanel spOpt = new StackPanel();
            spOpt.Children.Add(new TextBlock
            {
                Text = "COMPONENTS & SYSTEM INTEGRATION:",
                FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColChamp), Margin = new Thickness(0, 0, 0, 8)
            });

            _chkDriver    = CreateStyledCheck("Install & Start T2 SMC Kernel Driver (applesmc.sys — Required for Sensors, Fan & Battery)", true);
            _chkShortcuts = CreateStyledCheck("Create Desktop & Start Menu Shortcuts (T2 Hardware Control Center)", true);
            _chkAutoStart = CreateStyledCheck("Start Automatically with Windows on AC & Battery (Via Scheduled Task)", true);
            _chkLaunchNow = CreateStyledCheck("Launch T2 Hardware Control Center immediately after installation", true);

            spOpt.Children.Add(_chkDriver);
            spOpt.Children.Add(_chkShortcuts);
            spOpt.Children.Add(_chkAutoStart);
            spOpt.Children.Add(_chkLaunchNow);
            optCard.Child = spOpt;
            body.Children.Add(optCard);

            // Progress & Log Box
            _progressBar = new ProgressBar
            {
                Minimum = 0, Maximum = 100, Value = 0, Height = 8,
                Background = SubCardBg, Foreground = new SolidColorBrush(ColGold),
                BorderBrush = MutedBorder, Margin = new Thickness(0, 0, 0, 8)
            };
            body.Children.Add(_progressBar);

            _txtLog = new TextBox
            {
                Height = 115, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"), FontSize = 11,
                Background = new SolidColorBrush(Color.FromRgb(6, 7, 10)),
                Foreground = new SolidColorBrush(ColChamp),
                BorderBrush = MutedBorder, Padding = new Thickness(8),
                Text = "Ready to install T2 Hardware Control Center v" + _version + ".\r\n" +
                       "Designed for 2018-2020 T2-equipped laptops running Windows 10 or Windows 11.\r\n"
            };
            body.Children.Add(_txtLog);

            // Action Buttons
            Grid btnGrid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _btnInstall = new Button
            {
                Content = "Install & Launch Control Center (v" + _version + ")",
                FontSize = 13.5, FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromArgb(75, ColGold.R, ColGold.G, ColGold.B)),
                Foreground = TextPrimary, BorderBrush = new SolidColorBrush(ColGold),
                BorderThickness = new Thickness(1.5), Padding = new Thickness(18, 11, 18, 11),
                Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand
            };
            _btnInstall.Click += (s, e) => RunInstallAsync();
            Grid.SetColumn(_btnInstall, 0);
            btnGrid.Children.Add(_btnInstall);

            _btnUninstall = new Button
            {
                Content = "Uninstall",
                FontSize = 12, FontWeight = FontWeights.Bold,
                Background = SubCardBg, Foreground = new SolidColorBrush(ColCrimson),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, ColCrimson.R, ColCrimson.G, ColCrimson.B)),
                BorderThickness = new Thickness(1.2), Padding = new Thickness(16, 11, 16, 11),
                Cursor = Cursors.Hand
            };
            _btnUninstall.Click += (s, e) => RunUninstallAsync();
            Grid.SetColumn(_btnUninstall, 1);
            btnGrid.Children.Add(_btnUninstall);

            body.Children.Add(btnGrid);
            root.Children.Add(body);
            outer.Child = root;
            Content = outer;
        }

        private CheckBox CreateStyledCheck(string text, bool isChecked)
        {
            return new CheckBox
            {
                Content = new TextBlock { Text = text, Foreground = TextPrimary, FontSize = 12 },
                IsChecked = isChecked,
                Margin = new Thickness(0, 3, 0, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private void Log(string msg, int progress)
        {
            Dispatcher.Invoke(() =>
            {
                _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
                _txtLog.ScrollToEnd();
                if (progress >= 0) _progressBar.Value = progress;
            });
        }

        private static bool ExtractEmbeddedResource(string resourceName, string outPath)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null) return false;
                using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                {
                    s.CopyTo(fs);
                }
                return true;
            }
        }

        private static void RunCmd(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args)
                {
                    CreateNoWindow = true, UseShellExecute = false
                };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(5000);
                }
            }
            catch { }
        }

        private void RunInstallAsync()
        {
            string targetDir = _txtInstallDir.Text.Trim();
            bool doDriver = _chkDriver.IsChecked == true;
            bool doShortcuts = _chkShortcuts.IsChecked == true;
            bool doAutoStart = _chkAutoStart.IsChecked == true;
            bool doLaunch = _chkLaunchNow.IsChecked == true;

            _btnInstall.IsEnabled = false;
            _btnUninstall.IsEnabled = false;

            Task.Factory.StartNew(() =>
            {
                try
                {
                    Log("Stopping any running MacAirControl instances...", 10);
                    RunCmd("schtasks.exe", "/End /TN \"MacBookAirControlCenter\"");
                    foreach (Process p in Process.GetProcessesByName("MacAirControl"))
                    {
                        try { p.Kill(); p.WaitForExit(2000); } catch { }
                    }

                    Log("Creating directory: " + targetDir, 25);
                    Directory.CreateDirectory(targetDir);

                    string exePath = Path.Combine(targetDir, "MacAirControl.exe");
                    string sysPath = Path.Combine(targetDir, "applesmc.sys");
                    string icoPath = Path.Combine(targetDir, "app.ico");
                    string docPath = Path.Combine(targetDir, "README.md");

                    Log("Extracting MacAirControl.exe (Gotham Gold Edition)...", 40);
                    ExtractEmbeddedResource("MacAirControl.exe", exePath);
                    ExtractEmbeddedResource("applesmc.sys", sysPath);
                    ExtractEmbeddedResource("app.ico", icoPath);
                    ExtractEmbeddedResource("README.md", docPath);

                    // Copy self as uninstaller
                    string uninstPath = Path.Combine(targetDir, "Uninstall_MacAirControl.exe");
                    try { File.Copy(Assembly.GetExecutingAssembly().Location, uninstPath, true); } catch { }

                    if (doDriver && File.Exists(sysPath))
                    {
                        Log("Configuring & starting AppleSMC kernel driver...", 60);
                        RunCmd("sc.exe", "create AppleSMC binPath= \"" + sysPath + "\" type= kernel start= auto");
                        RunCmd("sc.exe", "config AppleSMC binPath= \"" + sysPath + "\" type= kernel start= auto");
                        RunCmd("sc.exe", "start AppleSMC");
                    }

                    if (doAutoStart)
                    {
                        Log("Registering Windows Scheduled Task (\\MacBookAirControlCenter)...", 75);
                        RunCmd("schtasks.exe", "/Create /F /RL HIGHEST /SC ONLOGON /TN \"MacBookAirControlCenter\" /TR \"\\\"" + exePath + "\\\" --minimized\"");
                        string psCmd = "-NoProfile -WindowStyle Hidden -Command \"Set-ScheduledTask -TaskName 'MacBookAirControlCenter' -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero))\"";
                        RunCmd("powershell.exe", psCmd);
                    }

                    if (doShortcuts)
                    {
                        Log("Creating Desktop & Start Menu shortcuts...", 85);
                        string deskLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "MacBook Air Control Center.lnk");
                        string startLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MacBook Air Control Center.lnk");
                        CreateShortcut(deskLink, exePath, targetDir, icoPath);
                        CreateShortcut(startLink, exePath, targetDir, icoPath);
                    }

                    // Register in Windows Add/Remove Programs
                    try
                    {
                        using (RegistryKey k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MacBookAirControlCenter"))
                        {
                            if (k != null)
                            {
                                k.SetValue("DisplayName", "MacBook Air & T2 Control Center (Gotham Gold Edition)");
                                k.SetValue("DisplayVersion", _version);
                                k.SetValue("Publisher", "Peter Karlsson");
                                k.SetValue("DisplayIcon", icoPath);
                                k.SetValue("InstallLocation", targetDir);
                                k.SetValue("UninstallString", "\"" + uninstPath + "\" --uninstall");
                                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                            }
                        }
                    }
                    catch { }

                    Log("SUCCESS! MacBook Air Control Center v" + _version + " is installed.", 100);

                    if (doLaunch && File.Exists(exePath))
                    {
                        File.WriteAllText(Path.Combine(targetDir, "show_window.flag"), "1");
                        Process.Start(new ProcessStartInfo(exePath) { WorkingDirectory = targetDir, UseShellExecute = true });
                    }
                }
                catch (Exception ex)
                {
                    Log("ERROR: " + ex.Message, 0);
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        _btnInstall.IsEnabled = true;
                        _btnUninstall.IsEnabled = true;
                    });
                }
            });
        }

        private void RunUninstallAsync()
        {
            string targetDir = _txtInstallDir.Text.Trim();
            _btnInstall.IsEnabled = false;
            _btnUninstall.IsEnabled = false;

            Task.Factory.StartNew(() =>
            {
                try
                {
                    Log("Stopping MacBook Air Control Center...", 25);
                    RunCmd("schtasks.exe", "/End /TN \"MacBookAirControlCenter\"");
                    RunCmd("schtasks.exe", "/Delete /F /TN \"MacBookAirControlCenter\"");
                    foreach (Process p in Process.GetProcessesByName("MacAirControl"))
                    {
                        try { p.Kill(); p.WaitForExit(2000); } catch { }
                    }

                    Log("Removing Desktop & Start Menu shortcuts...", 60);
                    string deskLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "MacBook Air Control Center.lnk");
                    string startLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MacBook Air Control Center.lnk");
                    if (File.Exists(deskLink)) File.Delete(deskLink);
                    if (File.Exists(startLink)) File.Delete(startLink);

                    try
                    {
                        Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MacBookAirControlCenter", false);
                    }
                    catch { }

                    Log("Uninstall complete.", 100);
                }
                catch (Exception ex)
                {
                    Log("Uninstall error: " + ex.Message, 0);
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        _btnInstall.IsEnabled = true;
                        _btnUninstall.IsEnabled = true;
                    });
                }
            });
        }

        private static void CreateShortcut(string lnkPath, string targetExe, string workDir, string iconPath)
        {
            string ps = string.Format(
                "-NoProfile -WindowStyle Hidden -Command \"$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('{0}'); $s.TargetPath = '{1}'; $s.WorkingDirectory = '{2}'; $s.IconLocation = '{3}'; $s.Save()\"",
                lnkPath.Replace("'", "''"), targetExe.Replace("'", "''"), workDir.Replace("'", "''"), iconPath.Replace("'", "''"));
            RunCmd("powershell.exe", ps);
        }

        [STAThread]
        public static void Main(string[] args)
        {
            string ver = "1.0.0";
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("VERSION.txt"))
                {
                    if (s != null)
                    {
                        using (StreamReader sr = new StreamReader(s))
                        {
                            ver = sr.ReadToEnd().Trim();
                        }
                    }
                }
            }
            catch { }

            bool autoUninstall = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase))
                        autoUninstall = true;
                }
            }

            Application app = new Application();
            app.Run(new SetupWindow(ver, autoUninstall));
        }
    }
}
