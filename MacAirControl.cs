using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace MacAirControl
{
    // =========================================================================
    // Persistent User Configuration
    // =========================================================================
    public class AppConfig
    {
        public string FanMode = "Curve"; // "Auto", "Curve", "Manual"
        public int CurveMinTemp = 60;
        public int CurveMaxTemp = 85;
        public int ManualRpm = 4200;
        public bool BatteryCapEnabled = true;
        public int BatteryCapPct = 80;
        public bool SafetyResetOnExit = true;

        private static string ConfigPath
        {
            get
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                return System.IO.Path.Combine(dir, "settings.ini");
            }
        }

        public static AppConfig Load()
        {
            AppConfig cfg = new AppConfig();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    foreach (string line in File.ReadAllLines(ConfigPath))
                    {
                        string[] parts = line.Split('=');
                        if (parts.Length != 2) continue;
                        string k = parts[0].Trim();
                        string v = parts[1].Trim();
                        if (k == "FanMode") cfg.FanMode = v;
                        else if (k == "CurveMinTemp") int.TryParse(v, out cfg.CurveMinTemp);
                        else if (k == "CurveMaxTemp") int.TryParse(v, out cfg.CurveMaxTemp);
                        else if (k == "ManualRpm") int.TryParse(v, out cfg.ManualRpm);
                        else if (k == "BatteryCapEnabled") bool.TryParse(v, out cfg.BatteryCapEnabled);
                        else if (k == "BatteryCapPct") int.TryParse(v, out cfg.BatteryCapPct);
                        else if (k == "SafetyResetOnExit") bool.TryParse(v, out cfg.SafetyResetOnExit);
                    }
                }
            }
            catch { }
            return cfg;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("FanMode=" + FanMode);
                sb.AppendLine("CurveMinTemp=" + CurveMinTemp);
                sb.AppendLine("CurveMaxTemp=" + CurveMaxTemp);
                sb.AppendLine("ManualRpm=" + ManualRpm);
                sb.AppendLine("BatteryCapEnabled=" + BatteryCapEnabled);
                sb.AppendLine("BatteryCapPct=" + BatteryCapPct);
                sb.AppendLine("SafetyResetOnExit=" + SafetyResetOnExit);
                File.WriteAllText(ConfigPath, sb.ToString());
            }
            catch { }
        }
    }

    // =========================================================================
    // Low-Level Apple SMC Driver Client (\\.\APPLESMC)
    // =========================================================================
    public class AppleSmcClient : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice, uint dwIoControlCode,
            byte[] lpInBuffer, int nInBufferSize,
            byte[] lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1;
        private const uint FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;

        private const uint IOCTL_SMC_READ_KEY       = 0x00220000;
        private const uint IOCTL_SMC_WRITE_KEY      = 0x00220004;
        private const uint IOCTL_SMC_GET_KEY_BY_IDX = 0x00220008;
        private const uint IOCTL_SMC_GET_KEY_INFO   = 0x0022000C;
        private const uint IOCTL_SMC_IS_MMIO        = 0x00220020;

        private SafeFileHandle _handle;
        public bool IsConnected { get; private set; }
        public bool IsMmio { get; private set; }
        public string StatusMessage { get; private set; }

        private class KeyMeta
        {
            public byte Size;
            public string TypeCode;
            public byte Flags;
        }

        private readonly Dictionary<string, KeyMeta> _keyCache = new Dictionary<string, KeyMeta>();

        public AppleSmcClient()
        {
            Connect();
        }

        public bool Connect()
        {
            try
            {
                if (_handle != null && !_handle.IsInvalid)
                {
                    IsConnected = true;
                    return true;
                }

                EnsureDriverServiceRunning();

                int lastErr = 0;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    _handle = CreateFile(@"\\.\APPLESMC",
                        GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                    if (!_handle.IsInvalid)
                        break;

                    lastErr = Marshal.GetLastWin32Error();
                    Program.StopConflictingProcesses();
                    Thread.Sleep(250);
                }

                if (_handle == null || _handle.IsInvalid)
                {
                    StatusMessage = "SMC OFFLINE (ERR " + lastErr + ")";
                    IsConnected = false;
                    return false;
                }

                int ret;
                byte[] mmioBuf = new byte[1];
                if (DeviceIoControl(_handle, IOCTL_SMC_IS_MMIO, null, 0, mmioBuf, 1, out ret, IntPtr.Zero) && ret == 1)
                {
                    IsMmio = mmioBuf[0] != 0;
                }

                IsConnected = true;
                StatusMessage = IsMmio ? "APPLE T2 SMC // MMIO ONLINE" : "APPLE SMC // PORT I/O ONLINE";
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = "SMC ERROR: " + ex.Message;
                IsConnected = false;
                return false;
            }
        }

        private void EnsureDriverServiceRunning()
        {
            try
            {
                string localSys = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "applesmc.sys");
                using (ServiceController sc = new ServiceController("AppleSMC"))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        try
                        {
                            sc.Start();
                            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(3));
                        }
                        catch
                        {
                            if (File.Exists(localSys))
                            {
                                Program.RunHiddenCmd("sc.exe", "config AppleSMC binPath= \"" + localSys + "\" type= kernel start= auto");
                                Program.RunHiddenCmd("sc.exe", "start AppleSMC");
                                Thread.Sleep(300);
                            }
                        }
                    }
                }
            }
            catch
            {
                string localSys = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "applesmc.sys");
                if (File.Exists(localSys))
                {
                    Program.RunHiddenCmd("sc.exe", "create AppleSMC binPath= \"" + localSys + "\" type= kernel start= auto");
                    Program.RunHiddenCmd("sc.exe", "start AppleSMC");
                    Thread.Sleep(300);
                }
            }
        }

        private byte[] EncodeKey(string key)
        {
            return Encoding.ASCII.GetBytes(key.PadRight(4, ' ').Substring(0, 4));
        }

        private KeyMeta GetKeyInfoInternal(string key)
        {
            if (_handle == null || _handle.IsInvalid) return null;
            KeyMeta cached;
            if (_keyCache.TryGetValue(key, out cached)) return cached;

            byte[] inBuf = EncodeKey(key);
            byte[] outBuf = new byte[6];
            int ret;
            if (DeviceIoControl(_handle, IOCTL_SMC_GET_KEY_INFO, inBuf, 4, outBuf, 6, out ret, IntPtr.Zero) && ret == 6)
            {
                byte size = outBuf[0];
                if (size > 0 && size <= 32)
                {
                    string typeStr = Encoding.ASCII.GetString(outBuf, 1, 4);
                    var meta = new KeyMeta { Size = size, TypeCode = typeStr, Flags = outBuf[5] };
                    _keyCache[key] = meta;
                    return meta;
                }
            }
            return null;
        }

        public bool KeyExists(string key)
        {
            return GetKeyInfoInternal(key) != null;
        }

        public byte[] ReadRaw(string key, out string typeCode)
        {
            typeCode = "";
            if (!IsConnected) return null;

            KeyMeta meta = GetKeyInfoInternal(key);
            if (meta == null) return null;
            typeCode = meta.TypeCode;

            byte[] keyBytes = EncodeKey(key);
            byte[] inBuf = new byte[5];
            Array.Copy(keyBytes, 0, inBuf, 0, 4);
            inBuf[4] = meta.Size;

            byte[] outBuf = new byte[32];
            int ret;
            if (DeviceIoControl(_handle, IOCTL_SMC_READ_KEY, inBuf, 5, outBuf, 32, out ret, IntPtr.Zero) && ret >= meta.Size)
            {
                byte[] res = new byte[meta.Size];
                Array.Copy(outBuf, 0, res, 0, meta.Size);
                return res;
            }
            return null;
        }

        public bool WriteRaw(string key, byte[] data)
        {
            if (!IsConnected || data == null || data.Length == 0 || data.Length > 32) return false;

            byte[] keyBytes = EncodeKey(key);
            byte[] inBuf = new byte[5 + data.Length];
            Array.Copy(keyBytes, 0, inBuf, 0, 4);
            inBuf[4] = (byte)data.Length;
            Array.Copy(data, 0, inBuf, 5, data.Length);

            byte[] outBuf = new byte[32];
            int ret;
            return DeviceIoControl(_handle, IOCTL_SMC_WRITE_KEY, inBuf, inBuf.Length, outBuf, outBuf.Length, out ret, IntPtr.Zero);
        }

        public double? ReadNumber(string key)
        {
            string typeCode;
            byte[] raw = ReadRaw(key, out typeCode);
            if (raw == null || raw.Length == 0) return null;

            if (raw.Length == 4 && (typeCode == "flt " || typeCode == " tlf"))
            {
                float f = BitConverter.ToSingle(raw, 0);
                if (!float.IsNaN(f) && !float.IsInfinity(f) && f > -100 && f < 20000)
                    return f;
                byte[] rev = (byte[])raw.Clone();
                Array.Reverse(rev);
                f = BitConverter.ToSingle(rev, 0);
                return f;
            }

            if (raw.Length == 2)
            {
                if (typeCode == "fpe2" || typeCode == "2epf")
                {
                    ushort be = (ushort)((raw[0] << 8) | raw[1]);
                    return be / 4.0;
                }
                if (typeCode == "sp78" || typeCode == "87ps")
                {
                    short be = (short)((raw[0] << 8) | raw[1]);
                    return be / 256.0;
                }
                if (typeCode == "fp88" || typeCode == "88pf")
                {
                    ushort be = (ushort)((raw[0] << 8) | raw[1]);
                    return be / 256.0;
                }
                if (typeCode == "ui16" || typeCode == "61iu")
                {
                    ushort be = (ushort)((raw[0] << 8) | raw[1]);
                    return be;
                }
                if (typeCode == "si16" || typeCode == "61is")
                {
                    short be = (short)((raw[0] << 8) | raw[1]);
                    return be;
                }
                if (typeCode == "hex_")
                {
                    return (ushort)((raw[0] << 8) | raw[1]);
                }
            }

            if (raw.Length == 1) return raw[0];

            if (raw.Length == 4)
            {
                if (typeCode == "ui32")
                {
                    uint be = (uint)((raw[0] << 24) | (raw[1] << 16) | (raw[2] << 8) | raw[3]);
                    return be;
                }
                float f = BitConverter.ToSingle(raw, 0);
                if (!float.IsNaN(f) && f >= -50 && f <= 20000) return f;
            }

            return null;
        }

        public bool WriteFloatOrFpe2(string key, double val)
        {
            KeyMeta meta = GetKeyInfoInternal(key);
            if (meta == null) return false;

            if (meta.Size == 4)
            {
                byte[] bytes = BitConverter.GetBytes((float)val);
                return WriteRaw(key, bytes);
            }
            if (meta.Size == 2)
            {
                ushort raw = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(val * 4.0)));
                byte[] bytes = new byte[] { (byte)(raw >> 8), (byte)(raw & 0xFF) };
                return WriteRaw(key, bytes);
            }
            return false;
        }

        public bool WriteByte(string key, byte val)
        {
            return WriteRaw(key, new byte[] { val });
        }

        public bool WriteUInt16BE(string key, ushort val)
        {
            return WriteRaw(key, new byte[] { (byte)(val >> 8), (byte)(val & 0xFF) });
        }

        public void Dispose()
        {
            if (_handle != null && !_handle.IsInvalid)
            {
                _handle.Dispose();
            }
        }
    }

    // =========================================================================
    // Custom MSI Afterburner × Cyberpunk Radial Cockpit Dial Control
    // =========================================================================
    public class AfterburnerDialControl : Grid
    {
        private readonly Canvas _dialCanvas;
        private readonly System.Windows.Shapes.Path _bgArc;
        private readonly System.Windows.Shapes.Path _valArc;
        private readonly List<Line> _ticks = new List<Line>();
        private readonly TextBlock _txtTitle;
        private readonly TextBlock _txtMainValue;
        private readonly TextBlock _txtUnit;
        private readonly TextBlock _txtSubLine1;
        private readonly TextBlock _txtSubLine2;
        private readonly Border _badgeBorder;
        private readonly TextBlock _txtBadge;

        private const double DialSize = 210.0;
        private const double CenterXY = 105.0;
        private const double Radius = 84.0;
        private const double StartAngle = 135.0;
        private const double SweepMax = 270.0;

        public AfterburnerDialControl(string title, string unit, Color accentColor)
        {
            Width = DialSize;
            Height = DialSize + 18;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;

            _dialCanvas = new Canvas
            {
                Width = DialSize,
                Height = DialSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top
            };

            // Outer Cyberpunk decorative ring
            Ellipse outerRing = new Ellipse
            {
                Width = DialSize - 6,
                Height = DialSize - 6,
                Stroke = new SolidColorBrush(Color.FromArgb(45, accentColor.R, accentColor.G, accentColor.B)),
                StrokeThickness = 1.0,
                StrokeDashArray = new DoubleCollection(new double[] { 6, 4 })
            };
            Canvas.SetLeft(outerRing, 3);
            Canvas.SetTop(outerRing, 3);
            _dialCanvas.Children.Add(outerRing);

            // Inner dark Gotham obsidian cockpit well
            Ellipse innerWell = new Ellipse
            {
                Width = Radius * 2 - 22,
                Height = Radius * 2 - 22,
                Fill = new RadialGradientBrush(
                    Color.FromArgb(235, 22, 24, 30),
                    Color.FromArgb(250, 7, 8, 11)),
                Stroke = new SolidColorBrush(Color.FromArgb(75, 166, 138, 68)),
                StrokeThickness = 1.0
            };
            Canvas.SetLeft(innerWell, CenterXY - (Radius - 11));
            Canvas.SetTop(innerWell, CenterXY - (Radius - 11));
            _dialCanvas.Children.Add(innerWell);

            // 28 Perimeter Afterburner Ticks
            int tickCount = 28;
            for (int i = 0; i <= tickCount; i++)
            {
                double ratio = (double)i / tickCount;
                double deg = StartAngle + ratio * SweepMax;
                double rad = deg * Math.PI / 180.0;
                bool isMajor = (i % 4 == 0);
                double rIn = isMajor ? Radius + 7 : Radius + 9;
                double rOut = isMajor ? Radius + 15 : Radius + 13;

                Line tick = new Line
                {
                    X1 = CenterXY + rIn * Math.Cos(rad),
                    Y1 = CenterXY + rIn * Math.Sin(rad),
                    X2 = CenterXY + rOut * Math.Cos(rad),
                    Y2 = CenterXY + rOut * Math.Sin(rad),
                    Stroke = new SolidColorBrush(Color.FromArgb(70, 140, 128, 104)),
                    StrokeThickness = isMajor ? 2.0 : 1.2
                };
                _ticks.Add(tick);
                _dialCanvas.Children.Add(tick);
            }

            // Background Arc Track
            _bgArc = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(80, 38, 36, 32)),
                StrokeThickness = 10,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = BuildArcGeometry(StartAngle, SweepMax)
            };
            _dialCanvas.Children.Add(_bgArc);

            // Active Gotham Gold Value Arc
            _valArc = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(accentColor),
                StrokeThickness = 10,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Effect = new DropShadowEffect
                {
                    Color = accentColor,
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Opacity = 0.75
                },
                Data = BuildArcGeometry(StartAngle, 5)
            };
            _dialCanvas.Children.Add(_valArc);

            Children.Add(_dialCanvas);

            // Center Digital HUD Readouts
            StackPanel centerStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -6, 0, 0)
            };

            _txtTitle = new TextBlock
            {
                Text = title,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(168, 155, 128)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            };
            centerStack.Children.Add(_txtTitle);

            StackPanel valRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            _txtMainValue = new TextBlock
            {
                Text = "--",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(245, 239, 224)),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _txtUnit = new TextBlock
            {
                Text = " " + unit,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accentColor),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 4)
            };
            valRow.Children.Add(_txtMainValue);
            valRow.Children.Add(_txtUnit);
            centerStack.Children.Add(valRow);

            _txtSubLine1 = new TextBlock
            {
                Text = "--",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(218, 206, 180)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };
            centerStack.Children.Add(_txtSubLine1);

            _txtSubLine2 = new TextBlock
            {
                Text = "--",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(156, 146, 126)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            };
            centerStack.Children.Add(_txtSubLine2);

            Children.Add(centerStack);

            // Bottom Gotham Tactical Pill Badge
            _badgeBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 14, 15, 19)),
                BorderBrush = new SolidColorBrush(accentColor),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 2)
            };
            _txtBadge = new TextBlock
            {
                Text = "ONLINE",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accentColor)
            };
            _badgeBorder.Child = _txtBadge;
            Children.Add(_badgeBorder);
        }

        private Geometry BuildArcGeometry(double startDeg, double sweepDeg)
        {
            sweepDeg = Math.Max(1.0, Math.Min(SweepMax, sweepDeg));
            double startRad = startDeg * Math.PI / 180.0;
            double endRad = (startDeg + sweepDeg) * Math.PI / 180.0;

            Point pStart = new Point(CenterXY + Radius * Math.Cos(startRad), CenterXY + Radius * Math.Sin(startRad));
            Point pEnd = new Point(CenterXY + Radius * Math.Cos(endRad), CenterXY + Radius * Math.Sin(endRad));

            StreamGeometry geom = new StreamGeometry();
            using (StreamGeometryContext ctx = geom.Open())
            {
                ctx.BeginFigure(pStart, false, false);
                ctx.ArcTo(pEnd, new Size(Radius, Radius), 0, sweepDeg > 180.0, SweepDirection.Clockwise, true, false);
            }
            geom.Freeze();
            return geom;
        }

        public void UpdateDial(double ratio, string mainVal, string sub1, string sub2, string badgeText, Color color)
        {
            ratio = Math.Max(0.02, Math.Min(1.0, ratio));
            _valArc.Data = BuildArcGeometry(StartAngle, ratio * SweepMax);

            SolidColorBrush brush = new SolidColorBrush(color);
            _valArc.Stroke = brush;
            _txtUnit.Foreground = brush;
            _badgeBorder.BorderBrush = brush;
            _txtBadge.Foreground = brush;

            DropShadowEffect glow = _valArc.Effect as DropShadowEffect;
            if (glow != null) glow.Color = color;

            int activeTicks = (int)Math.Round(ratio * (_ticks.Count - 1));
            for (int i = 0; i < _ticks.Count; i++)
            {
                _ticks[i].Stroke = (i <= activeTicks)
                    ? brush
                    : new SolidColorBrush(Color.FromArgb(55, 148, 163, 184));
            }

            _txtMainValue.Text = mainVal;
            _txtSubLine1.Text = sub1;
            _txtSubLine2.Text = sub2;
            _txtBadge.Text = badgeText;
        }
    }

    // =========================================================================
    // Custom Apple × Cyberpunk Animated Pill Toggle Switch
    // =========================================================================
    public class CyberToggleSwitch : Border
    {
        private bool _isChecked;
        private readonly Grid _trackGrid;
        private readonly Border _thumb;
        private readonly TextBlock _txtState;
        private readonly Color _activeColor;

        public event Action<bool> Toggled;

        public bool IsChecked
        {
            get { return _isChecked; }
            set
            {
                _isChecked = value;
                UpdateVisual(false);
            }
        }

        public CyberToggleSwitch(bool initialState, Color activeColor)
        {
            _isChecked = initialState;
            _activeColor = activeColor;
            Width = 68;
            Height = 28;
            CornerRadius = new CornerRadius(14);
            BorderThickness = new Thickness(1.5);
            Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Center;

            _trackGrid = new Grid();
            _txtState = new TextBlock
            {
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };

            _thumb = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Center
            };

            _trackGrid.Children.Add(_txtState);
            _trackGrid.Children.Add(_thumb);
            Child = _trackGrid;

            MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                _isChecked = !_isChecked;
                UpdateVisual(true);
            };

            UpdateVisual(false);
        }

        private void UpdateVisual(bool fireEvent)
        {
            if (_isChecked)
            {
                Background = new SolidColorBrush(Color.FromArgb(65, _activeColor.R, _activeColor.G, _activeColor.B));
                BorderBrush = new SolidColorBrush(_activeColor);
                _thumb.Background = new SolidColorBrush(_activeColor);
                _thumb.HorizontalAlignment = HorizontalAlignment.Right;
                _thumb.Margin = new Thickness(0, 0, 3, 0);
                _thumb.Effect = new DropShadowEffect { Color = _activeColor, BlurRadius = 9, ShadowDepth = 0, Opacity = 0.85 };
                _txtState.Text = "ON";
                _txtState.Foreground = new SolidColorBrush(_activeColor);
                _txtState.HorizontalAlignment = HorizontalAlignment.Left;
                _txtState.Margin = new Thickness(9, 0, 0, 0);
            }
            else
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 14, 15, 19));
                BorderBrush = new SolidColorBrush(Color.FromRgb(68, 62, 50));
                _thumb.Background = new SolidColorBrush(Color.FromRgb(138, 128, 108));
                _thumb.HorizontalAlignment = HorizontalAlignment.Left;
                _thumb.Margin = new Thickness(3, 0, 0, 0);
                _thumb.Effect = null;
                _txtState.Text = "OFF";
                _txtState.Foreground = new SolidColorBrush(Color.FromRgb(118, 110, 94));
                _txtState.HorizontalAlignment = HorizontalAlignment.Right;
                _txtState.Margin = new Thickness(0, 0, 8, 0);
            }

            if (fireEvent && Toggled != null)
            {
                Toggled(_isChecked);
            }
        }
    }

    // =========================================================================
    // Main Window — Dark Gotham × WayneTech Gold × Apple × MSI Afterburner UI
    // =========================================================================
    public class MainWindow : Window
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly AppleSmcClient _smc;
        private readonly AppConfig _cfg;
        private readonly DispatcherTimer _pollTimer;
        private WinForms.NotifyIcon _trayIcon;
        private WinForms.ContextMenuStrip _trayMenu;

        private PerformanceCounter _cpuPerfCounter;
        private PerformanceCounter _cpuLoadCounter;

        // Top HUD & Navigation
        private TextBlock _txtSmcStatus;
        private TextBlock _txtTopPowerBadge;
        private TextBlock _txtTopTurboBadge;
        private TextBlock _txtTopBattBadge;

        private Button[] _tabButtons;
        private UIElement[] _tabPages;
        private int _selectedTabIndex = 0;

        // Tab 1: MSI Afterburner Cockpit Dials & Live Waveform
        private AfterburnerDialControl _dialCpu;
        private AfterburnerDialControl _dialFan;
        private Canvas _waveformCanvas;
        private readonly List<double> _histCpuTemp = new List<double>();
        private readonly List<double> _histFanPct = new List<double>();
        private readonly List<double> _histCpuLoad = new List<double>();
        private const int MaxHistoryPoints = 50;

        private Button _btnQuickSilent;
        private Button _btnQuickSweet;
        private Button _btnQuickOverdrive;

        private CyberToggleSwitch _tglDashSmartFan;
        private CyberToggleSwitch _tglDashTurboBoost;
        private CyberToggleSwitch _tglDashBattCap;

        // 5-Sensor Thermal Matrix Cards (Tab 1)
        private TextBlock _txtCpuTemp, _txtProxTemp, _txtBoardTemp, _txtBattTemp, _txtPalmTemp;
        private Border _barCpuTemp, _barProxTemp, _barBoardTemp, _barBattTemp, _barPalmTemp;
        private TextBlock _txtCpuClock, _txtCpuLoad, _txtFanRpm;

        // Tab 2: Turbine & Fan Curve Controls
        private Canvas _fanCurveCanvas;
        private Button _btnFanCurve, _btnFanAuto, _btnFanManual;
        private Border _pnlFanCurveSettings, _pnlFanManualSettings;
        private Slider _sldCurveMin, _sldCurveMax, _sldManualRpm;
        private TextBlock _txtCurveMinVal, _txtCurveMaxVal, _txtManualRpmVal, _txtFanTargetInfo;

        // Tab 3: Turbo Boost & Per-Core Telemetry
        private Button _btnTurboOff, _btnTurboSweet, _btnTurboFull;
        private CyberToggleSwitch _tglTurboMaster;
        private TextBlock _txtTurboDetail;
        private TextBlock _txtCore1, _txtCore2, _txtCore3, _txtCore4, _txtGpuTemp, _txtSaTemp;
        private Border _barCore1, _barCore2, _barCore3, _barCore4, _barGpuTemp, _barSaTemp;

        // Tab 4: AlDente Battery & System Settings
        private TextBlock _txtBattStatusLine, _txtBattSmcState, _txtBattCapTarget;
        private TextBlock _txtBattTilePct, _txtBattTileBclm, _txtBattTileCycles, _txtBattTileVolt;
        private Canvas _battVisualCanvas;
        private Slider _sldBattCap;
        private CyberToggleSwitch _tglBattCap;
        private CyberToggleSwitch _tglStartWithWindows;
        private CyberToggleSwitch _tglSafetyReset;
        private Button _btnBattTopUp100;

        // State
        private double _fanMinRpm = 2700;
        private double _fanMaxRpm = 8000;
        private double _lastCpuTemp = 50.0;
        private double _lastFanRpm = 2700.0;
        private double _lastTargetRpm = 2700.0;
        private double _lastCpuLoad = 12.0;
        private int _lastEstMhz = 1800;
        private bool _isChargingInhibited = false;
        private string _activeTurboMode = "Sweet";
        private bool _allowClose = false;
        private bool _suppressSliderEvents = false;

        // Dark Gotham × WayneTech Gold × Apple Obsidian Palette
        private static readonly Color ColCyan    = Color.FromRgb(229, 184, 66);  // #E5B842 Sovereign Gotham Gold
        private static readonly Color ColMagenta = Color.FromRgb(217, 138, 43);  // #D98A2B Burnished Copper-Gold
        private static readonly Color ColGreen   = Color.FromRgb(238, 210, 125); // #EED27D Pale Champagne Gold
        private static readonly Color ColAmber   = Color.FromRgb(255, 196, 46);  // #FFC42E Molten Solar Gold
        private static readonly Color ColRed     = Color.FromRgb(214, 54, 56);   // #D63638 Gotham Tactical Crimson
        private static readonly Color ColPurple  = Color.FromRgb(186, 164, 118); // #BAA476 Muted Brass / Titanium Gold

        private static readonly Brush BgDeep      = new SolidColorBrush(Color.FromArgb(252, 7, 8, 11));     // Deep Gotham Obsidian
        private static readonly Brush CardBg      = new SolidColorBrush(Color.FromArgb(238, 15, 16, 21));   // Matte Carbon Gunmetal
        private static readonly Brush SubCardBg   = new SolidColorBrush(Color.FromArgb(230, 21, 23, 29));   // Tactical Dark Slate
        private static readonly Brush CardBorder  = new SolidColorBrush(Color.FromArgb(95, 212, 175, 55));  // Brushed Gold Hairline
        private static readonly Brush MutedBorder = new SolidColorBrush(Color.FromArgb(75, 78, 70, 54));    // Dark Bronze-Gunmetal Hairline
        private static readonly Brush TextPrimary = new SolidColorBrush(Color.FromRgb(242, 236, 222));      // Warm Ivory Gold-White
        private static readonly Brush TextMuted   = new SolidColorBrush(Color.FromRgb(156, 146, 126));      // Muted Pewter-Brass

        private static readonly Brush AccentBlue    = new SolidColorBrush(ColCyan);
        private static readonly Brush AccentMagenta = new SolidColorBrush(ColMagenta);
        private static readonly Brush AccentGreen   = new SolidColorBrush(ColGreen);
        private static readonly Brush AccentAmber   = new SolidColorBrush(ColAmber);
        private static readonly Brush AccentRed     = new SolidColorBrush(ColRed);

        public MainWindow(bool startMinimized)
        {
            _cfg = AppConfig.Load();
            _smc = new AppleSmcClient();

            Title = "MacBook Air Control Center // Gotham Gold Edition";
            Width = 1040;
            Height = 820;
            MinWidth = 900;
            MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Custom Borderless Apple × Cyberpunk Glass Window
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResizeWithGrip;

            string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(icoPath))
            {
                try { Icon = new BitmapImage(new Uri(icoPath, UriKind.Absolute)); } catch { }
            }

            try
            {
                _cpuPerfCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
                _cpuLoadCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuPerfCounter.NextValue();
                _cpuLoadCounter.NextValue();
            }
            catch { }

            ReadInitialSmcBounds();

            // Pre-seed waveform history so the live graph looks rich immediately
            for (int i = 0; i < MaxHistoryPoints; i++)
            {
                _histCpuTemp.Add(52.0);
                _histFanPct.Add(25.0);
                _histCpuLoad.Add(15.0);
            }

            BuildCyberDeckUi();
            InitializeTray();

            string startupShowFlag = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "show_window.flag");
            if (File.Exists(startupShowFlag))
            {
                try { File.Delete(startupShowFlag); } catch { }
                startMinimized = false;
            }

            DetectCurrentTurboState();
            SyncFanUiState();
            ApplyBatteryCap(false);
            UpdateTelemetryAndControl();

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            _pollTimer.Tick += (s, e) => UpdateTelemetryAndControl();
            _pollTimer.Start();

            if (startMinimized)
            {
                WindowState = WindowState.Minimized;
                ShowInTaskbar = false;
                Loaded += (s, e) => Hide();
            }
        }

        private void ReadInitialSmcBounds()
        {
            if (!_smc.IsConnected) return;
            double? mn = _smc.ReadNumber("F0Mn");
            double? mx = _smc.ReadNumber("F0Mx");
            if (mn.HasValue && mn.Value >= 1000 && mn.Value <= 6000) _fanMinRpm = mn.Value;
            if (mx.HasValue && mx.Value >= 4000 && mx.Value <= 10000) _fanMaxRpm = mx.Value;
        }

        // =====================================================================
        // Complete UI Construction (Dark Gotham × WayneTech Gold × Apple × Afterburner)
        // =====================================================================
        private void BuildCyberDeckUi()
        {
            // Outer Window Frame with Brushed Gotham Gold Gradient Border
            LinearGradientBrush frameBorderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            frameBorderBrush.GradientStops.Add(new GradientStop(ColCyan, 0.0));
            frameBorderBrush.GradientStops.Add(new GradientStop(Color.FromRgb(115, 88, 34), 0.5));
            frameBorderBrush.GradientStops.Add(new GradientStop(ColMagenta, 1.0));

            Border outerFrame = new Border
            {
                Background = BgDeep,
                BorderBrush = frameBorderBrush,
                BorderThickness = new Thickness(1.6),
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(8),
                Effect = new DropShadowEffect
                {
                    Color = Color.FromRgb(0, 0, 0),
                    BlurRadius = 22,
                    ShadowDepth = 4,
                    Opacity = 0.85
                }
            };

            DockPanel rootDock = new DockPanel();

            // -----------------------------------------------------------------
            // 1. TOP TITLE BAR (macOS Traffic Lights + Gotham Gold HUD Status)
            // -----------------------------------------------------------------
            Border titleBar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(250, 10, 11, 14)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 212, 175, 55)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(13, 13, 0, 0),
                Padding = new Thickness(18, 12, 18, 12)
            };
            titleBar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                }
                else
                {
                    try { DragMove(); } catch { }
                }
            };

            Grid titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left: macOS Traffic Light Buttons + Brand Title
            StackPanel spLeftTitle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            spLeftTitle.Children.Add(CreateTrafficLightButton(Color.FromRgb(255, 95, 86), "Hide to System Tray", () => HideToTray()));
            spLeftTitle.Children.Add(CreateTrafficLightButton(Color.FromRgb(255, 189, 46), "Minimize Window", () => WindowState = WindowState.Minimized));
            spLeftTitle.Children.Add(CreateTrafficLightButton(Color.FromRgb(39, 201, 63), "Maximize / Restore", () =>
            {
                WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
            }));

            Border sepBar = new Border
            {
                Width = 1.5,
                Height = 24,
                Background = new SolidColorBrush(Color.FromArgb(80, 166, 138, 68)),
                Margin = new Thickness(12, 0, 14, 0)
            };
            spLeftTitle.Children.Add(sepBar);

            StackPanel spTitleText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            StackPanel spTitleRow = new StackPanel { Orientation = Orientation.Horizontal };
            spTitleRow.Children.Add(new TextBlock
            {
                Text = " MACBOOK AIR ",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary
            });
            spTitleRow.Children.Add(new TextBlock
            {
                Text = "// GOTHAM GOLD AFTERBURNER",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = AccentBlue
            });
            spTitleText.Children.Add(spTitleRow);

            _txtSmcStatus = new TextBlock
            {
                Text = _smc.StatusMessage + "  •  MacBookAir9,1 (Core i5-1030NG7)",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 11,
                Foreground = _smc.IsConnected ? AccentGreen : AccentAmber,
                Margin = new Thickness(0, 2, 0, 0)
            };
            spTitleText.Children.Add(_txtSmcStatus);
            spLeftTitle.Children.Add(spTitleText);

            Grid.SetColumn(spLeftTitle, 0);
            titleGrid.Children.Add(spLeftTitle);

            // Right: Live HUD Status Pills + Minimize to Tray Button
            StackPanel spRightHud = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            _txtTopPowerBadge = CreateHudPillBadge("FAN: SMART CURVE", ColMagenta);
            _txtTopTurboBadge = CreateHudPillBadge("TURBO: SWEET SPOT", ColCyan);
            _txtTopBattBadge  = CreateHudPillBadge("ALDENTE: 80% CAP", ColGreen);

            spRightHud.Children.Add((UIElement)_txtTopPowerBadge.Parent);
            spRightHud.Children.Add((UIElement)_txtTopTurboBadge.Parent);
            spRightHud.Children.Add((UIElement)_txtTopBattBadge.Parent);

            Button btnTrayMinimize = CreateCyberButton("▼ TRAY", false, ColCyan);
            btnTrayMinimize.Padding = new Thickness(12, 6, 12, 6);
            btnTrayMinimize.Margin = new Thickness(8, 0, 0, 0);
            btnTrayMinimize.Click += (s, e) => HideToTray();
            spRightHud.Children.Add(btnTrayMinimize);

            Grid.SetColumn(spRightHud, 2);
            titleGrid.Children.Add(spRightHud);

            titleBar.Child = titleGrid;
            DockPanel.SetDock(titleBar, Dock.Top);
            rootDock.Children.Add(titleBar);

            // -----------------------------------------------------------------
            // 2. SEGMENTED NAVIGATION TAB BAR (Gotham Dark Gold Pill Tabs)
            // -----------------------------------------------------------------
            Border navBar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 11, 12, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(55, 166, 138, 68)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(20, 10, 20, 10)
            };

            UniformGrid navGrid = new UniformGrid { Columns = 4 };
            string[] tabTitles = new string[]
            {
                "⚡ 01 // COCKPIT & HUD",
                "🌀 02 // TURBINE & FAN CURVE",
                "🚀 03 // TURBO & CORES",
                "🔋 04 // ALDENTE BATTERY"
            };

            _tabButtons = new Button[4];
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                Button b = new Button
                {
                    Content = tabTitles[i],
                    FontFamily = new FontFamily("Segoe UI Semibold"),
                    FontSize = 12.5,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(10, 9, 10, 9),
                    Margin = new Thickness(4, 0, 4, 0),
                    BorderThickness = new Thickness(1.5),
                    Cursor = Cursors.Hand
                };
                b.Click += (s, e) => SelectTab(idx);
                _tabButtons[i] = b;
                navGrid.Children.Add(b);
            }

            navBar.Child = navGrid;
            DockPanel.SetDock(navBar, Dock.Top);
            rootDock.Children.Add(navBar);

            // -----------------------------------------------------------------
            // 3. BOTTOM STATUS FOOTER BAR
            // -----------------------------------------------------------------
            Border footerBar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 9, 10, 13)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(65, 212, 175, 55)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                CornerRadius = new CornerRadius(0, 0, 13, 13),
                Padding = new Thickness(18, 7, 24, 7)
            };
            DockPanel footerDock = new DockPanel();
            TextBlock txtFooterLeft = new TextBlock
            {
                Text = "APPLE T2 HARDWARE CONTROLLER  •  ZERO TRIAL LIMITS  •  SINGLE-CLICK TRAY ICON TO TOGGLE",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 10.5,
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock txtFooterRight = new TextBlock
            {
                Text = "GOTHAM GOLD EDITION × WINDOWS 10 LTSC // DRAG CORNER TO RESIZE ◢",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 229, 184, 66)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            footerDock.Children.Add(txtFooterLeft);
            footerDock.Children.Add(txtFooterRight);
            footerBar.Child = footerDock;
            DockPanel.SetDock(footerBar, Dock.Bottom);
            rootDock.Children.Add(footerBar);

            // -----------------------------------------------------------------
            // 4. MAIN SCROLLABLE TAB CONTENT AREA
            // -----------------------------------------------------------------
            ScrollViewer mainScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(20, 16, 20, 16)
            };

            Grid pagesHost = new Grid();
            _tabPages = new UIElement[4];
            _tabPages[0] = BuildTab1Cockpit();
            _tabPages[1] = BuildTab2FanCurve();
            _tabPages[2] = BuildTab3TurboAndCores();
            _tabPages[3] = BuildTab4BatteryAndSystem();

            for (int i = 0; i < 4; i++)
            {
                pagesHost.Children.Add(_tabPages[i]);
            }

            mainScroll.Content = pagesHost;
            rootDock.Children.Add(mainScroll);

            outerFrame.Child = rootDock;
            Content = outerFrame;

            SelectTab(0);
        }

        private void SelectTab(int index)
        {
            _selectedTabIndex = index;
            for (int i = 0; i < _tabPages.Length; i++)
            {
                bool active = (i == index);
                _tabPages[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;

                Color c = (i == 0) ? ColCyan
                        : (i == 1) ? ColMagenta
                        : (i == 2) ? ColAmber
                        : ColGreen;

                _tabButtons[i].Background = active
                    ? new SolidColorBrush(Color.FromArgb(55, c.R, c.G, c.B))
                    : new SolidColorBrush(Color.FromArgb(190, 16, 18, 23));
                _tabButtons[i].BorderBrush = active
                    ? new SolidColorBrush(c)
                    : MutedBorder;
                _tabButtons[i].Foreground = active
                    ? TextPrimary
                    : TextMuted;
            }
            DrawWaveformGraph();
            DrawFanCurveGraph();
            DrawBatteryVisualBar(GetCurrentBatteryPercent());
        }

        // =====================================================================
        // TAB 1: MSI AFTERBURNER COCKPIT & LIVE TELEMETRY MATRIX
        // =====================================================================
        private UIElement BuildTab1Cockpit()
        {
            StackPanel stack = new StackPanel();

            // --- A. MSI AFTERBURNER DUAL RADIAL COCKPIT + WAVEFORM CENTER ---
            Border cockpitCard = CreateCyberCard("MSI AFTERBURNER // DUAL HARDWARE COCKPIT & REAL-TIME TELEMETRY", ColCyan);
            StackPanel spCockpit = (StackPanel)cockpitCard.Child;

            Grid dialGrid = new Grid { Margin = new Thickness(0, 6, 0, 4) };
            dialGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            dialGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dialGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });

            // Left Dial: CPU Core & Clock
            _dialCpu = new AfterburnerDialControl("CPU CORE & CLOCK", "°C", ColCyan);
            Grid.SetColumn(_dialCpu, 0);
            dialGrid.Children.Add(_dialCpu);

            // Center Panel: Live Multi-Trace Waveform + Instant Profile Switcher
            Border centerWaveCard = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 11, 12, 16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 212, 175, 55)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(12, 0, 12, 0)
            };

            StackPanel spCenter = new StackPanel();

            // Legend Header
            DockPanel waveHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            waveHeader.Children.Add(new TextBlock
            {
                Text = "LIVE HARDWARE MONITOR (60S TRACE)",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = TextMuted
            });

            StackPanel spLegend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            spLegend.Children.Add(CreateLegendDot("CPU °C", ColCyan));
            spLegend.Children.Add(CreateLegendDot("FAN %", ColMagenta));
            spLegend.Children.Add(CreateLegendDot("LOAD %", ColGreen));
            waveHeader.Children.Add(spLegend);
            spCenter.Children.Add(waveHeader);

            // Oscilloscope Canvas
            Border waveFrame = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(248, 5, 6, 8)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(65, 212, 175, 55)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Height = 112,
                ClipToBounds = true
            };
            _waveformCanvas = new Canvas { Height = 112 };
            _waveformCanvas.SizeChanged += (s, e) => DrawWaveformGraph();
            waveFrame.Child = _waveformCanvas;
            spCenter.Children.Add(waveFrame);

            // Instant Afterburner Profile Preset Bar
            TextBlock lblPresets = new TextBlock
            {
                Text = "INSTANT GOTHAM PROFILES (ONE-CLICK FAN + TURBO PRESETS):",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = TextMuted,
                Margin = new Thickness(0, 10, 0, 5)
            };
            spCenter.Children.Add(lblPresets);

            UniformGrid presetBar = new UniformGrid { Columns = 3 };
            _btnQuickSilent = CreateCyberButton("🌙 SILENT ECO (1.1 GHz)", false, ColCyan);
            _btnQuickSweet  = CreateCyberButton("⚡ SWEET SPOT (2.2 GHz)", true, ColGreen);
            _btnQuickOverdrive = CreateCyberButton("🔥 MAX OVERDRIVE (3.5 GHz)", false, ColMagenta);

            _btnQuickSilent.Margin = new Thickness(0, 0, 4, 0);
            _btnQuickSweet.Margin = new Thickness(2, 0, 2, 0);
            _btnQuickOverdrive.Margin = new Thickness(4, 0, 0, 0);

            _btnQuickSilent.Click += (s, e) => { ApplyTurboMode("Off"); SetFanMode("Auto"); };
            _btnQuickSweet.Click += (s, e) => { ApplyTurboMode("Sweet"); SetFanMode("Curve"); };
            _btnQuickOverdrive.Click += (s, e) => { ApplyTurboMode("Full"); _cfg.CurveMinTemp = 55; _cfg.CurveMaxTemp = 78; SetFanMode("Curve"); };

            presetBar.Children.Add(_btnQuickSilent);
            presetBar.Children.Add(_btnQuickSweet);
            presetBar.Children.Add(_btnQuickOverdrive);
            spCenter.Children.Add(presetBar);

            centerWaveCard.Child = spCenter;
            Grid.SetColumn(centerWaveCard, 1);
            dialGrid.Children.Add(centerWaveCard);

            // Right Dial: Exhaust Fan RPM & Target
            _dialFan = new AfterburnerDialControl("EXHAUST TURBINE", "RPM", ColMagenta);
            Grid.SetColumn(_dialFan, 2);
            dialGrid.Children.Add(_dialFan);

            spCockpit.Children.Add(dialGrid);
            stack.Children.Add(cockpitCard);

            // --- B. QUICK MASTER TOGGLE DECK (Gotham Gold Pill Switches) ---
            Border quickDeckCard = CreateCyberCard("MASTER HARDWARE TOGGLE SWITCHES // INSTANT OVERRIDES", ColPurple);
            StackPanel spQuickDeck = (StackPanel)quickDeckCard.Child;

            UniformGrid toggleGrid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };

            _tglDashSmartFan = new CyberToggleSwitch(_cfg.FanMode == "Curve", ColMagenta);
            _tglDashSmartFan.Toggled += (on) => SetFanMode(on ? "Curve" : "Auto");
            toggleGrid.Children.Add(CreateToggleCard(
                "SMART FAN CURVE",
                "Proactive T2 cooling (" + _cfg.CurveMinTemp + "°C–" + _cfg.CurveMaxTemp + "°C)",
                _tglDashSmartFan, ColMagenta));

            _tglDashTurboBoost = new CyberToggleSwitch(_activeTurboMode != "Off", ColCyan);
            _tglDashTurboBoost.Toggled += (on) => ApplyTurboMode(on ? "Sweet" : "Off");
            toggleGrid.Children.Add(CreateToggleCard(
                "INTEL TURBO BOOST",
                "ON = 2.2 GHz Sweet Spot  •  OFF = 1.1 GHz Silent",
                _tglDashTurboBoost, ColCyan));

            _tglDashBattCap = new CyberToggleSwitch(_cfg.BatteryCapEnabled, ColGreen);
            _tglDashBattCap.Toggled += (on) =>
            {
                _cfg.BatteryCapEnabled = on;
                _cfg.Save();
                ApplyBatteryCap(!on);
            };
            toggleGrid.Children.Add(CreateToggleCard(
                "ALDENTE 80% CHARGE CAP",
                "Hardware SMC BCLM battery health limiter",
                _tglDashBattCap, ColGreen));

            spQuickDeck.Children.Add(toggleGrid);
            stack.Children.Add(quickDeckCard);

            // --- C. 5-SENSOR APPLE T2 THERMAL MATRIX + CLOCK/LOAD STRIP ---
            Border sensorCard = CreateCyberCard("APPLE T2 SMC // 5-ZONE THERMAL & FREQUENCY TELEMETRY MATRIX", ColGreen);
            StackPanel spSensors = (StackPanel)sensorCard.Child;

            UniformGrid sensorGrid = new UniformGrid { Columns = 5, Margin = new Thickness(0, 4, 0, 8) };
            _txtCpuTemp   = AddSensorMatrixTile(sensorGrid, "CPU CORE PECI", "TCXC", "-- °C", ColCyan, out _barCpuTemp);
            _txtProxTemp  = AddSensorMatrixTile(sensorGrid, "CPU HEATSINK", "TC0P", "-- °C", ColGreen, out _barProxTemp);
            _txtBoardTemp = AddSensorMatrixTile(sensorGrid, "LOGIC BOARD", "TW0P", "-- °C", ColGreen, out _barBoardTemp);
            _txtBattTemp  = AddSensorMatrixTile(sensorGrid, "SMART BATTERY", "TB0T", "-- °C", ColAmber, out _barBattTemp);
            _txtPalmTemp  = AddSensorMatrixTile(sensorGrid, "PALM REST", "Ts0P", "-- °C", ColCyan, out _barPalmTemp);
            spSensors.Children.Add(sensorGrid);

            UniformGrid subStats = new UniformGrid { Columns = 3 };
            _txtCpuClock = AddSubHudBar(subStats, "LIVE CPU FREQUENCY", "-- MHz (-- GHz)", ColCyan);
            _txtCpuLoad  = AddSubHudBar(subStats, "TOTAL PROCESSOR LOAD", "--%", ColGreen);
            _txtFanRpm   = AddSubHudBar(subStats, "EXHAUST TURBINE SPEED", "-- RPM", ColMagenta);
            spSensors.Children.Add(subStats);

            stack.Children.Add(sensorCard);
            return stack;
        }

        // =====================================================================
        // TAB 2: TURBINE & INTERACTIVE FAN CURVE EDITOR
        // =====================================================================
        private UIElement BuildTab2FanCurve()
        {
            StackPanel stack = new StackPanel();

            Border card = CreateCyberCard("EXHAUST TURBINE CONTROLLER // MACS FAN CONTROL REPLACEMENT", ColMagenta);
            StackPanel sp = (StackPanel)card.Child;

            _txtFanTargetInfo = new TextBlock
            {
                Text = "Active Fan Mode: Smart Curve (60°C – 85°C)",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = AccentMagenta,
                Margin = new Thickness(4, 2, 0, 12)
            };
            sp.Children.Add(_txtFanTargetInfo);

            // 3 Mode Selector Buttons
            UniformGrid modeGrid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };
            _btnFanCurve  = CreateCyberButton("📈 SMART TEMP CURVE\nDynamic Ramp (60°C – 85°C)", true, ColMagenta);
            _btnFanAuto   = CreateCyberButton(" APPLE SMC AUTO\nStock Firmware Control", false, ColCyan);
            _btnFanManual = CreateCyberButton("🎛️ MANUAL CONSTANT RPM\nFixed Turbine Speed", false, ColAmber);

            _btnFanCurve.Margin = new Thickness(0, 0, 6, 0);
            _btnFanAuto.Margin = new Thickness(3, 0, 3, 0);
            _btnFanManual.Margin = new Thickness(6, 0, 0, 0);

            _btnFanCurve.Click  += (s, e) => SetFanMode("Curve");
            _btnFanAuto.Click   += (s, e) => SetFanMode("Auto");
            _btnFanManual.Click += (s, e) => SetFanMode("Manual");

            modeGrid.Children.Add(_btnFanCurve);
            modeGrid.Children.Add(_btnFanAuto);
            modeGrid.Children.Add(_btnFanManual);
            sp.Children.Add(modeGrid);

            // Interactive Visual Fan Curve Graph
            Border graphBox = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 6, 7, 10)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(85, 217, 138, 43)),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 14)
            };
            StackPanel spGraph = new StackPanel();
            spGraph.Children.Add(new TextBlock
            {
                Text = "LIVE RESPONSE CURVE // CYBERDECK THERMAL MAPPING (X = CPU °C, Y = TURBINE RPM, DOT = LIVE OPERATING POINT)",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = TextMuted,
                Margin = new Thickness(0, 0, 0, 8)
            });
            _fanCurveCanvas = new Canvas { Height = 165, ClipToBounds = true };
            _fanCurveCanvas.SizeChanged += (s, e) => DrawFanCurveGraph();
            spGraph.Children.Add(_fanCurveCanvas);
            graphBox.Child = spGraph;
            sp.Children.Add(graphBox);

            // Smart Curve Sliders + Steppers Panel
            _pnlFanCurveSettings = new Border
            {
                Background = SubCardBg,
                BorderBrush = MutedBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 14, 18, 14)
            };
            StackPanel spCurveSliders = new StackPanel();

            // Row 1: Min Temp Slider
            DockPanel dpMin = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            dpMin.Children.Add(new TextBlock
            {
                Text = "RAMP START TEMP (2700 RPM BASE):",
                Width = 250,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });
            StackPanel spMinBtns = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(spMinBtns, Dock.Right);
            spMinBtns.Children.Add(CreateStepperButton("-5°C", () => AdjustCurveTemp(true, -5)));
            _txtCurveMinVal = new TextBlock
            {
                Text = _cfg.CurveMinTemp + " °C",
                Width = 68,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = AccentBlue,
                VerticalAlignment = VerticalAlignment.Center
            };
            spMinBtns.Children.Add(_txtCurveMinVal);
            spMinBtns.Children.Add(CreateStepperButton("+5°C", () => AdjustCurveTemp(true, +5)));
            dpMin.Children.Add(spMinBtns);

            _sldCurveMin = new Slider
            {
                Minimum = 45,
                Maximum = 80,
                Value = _cfg.CurveMinTemp,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 16, 0)
            };
            _sldCurveMin.ValueChanged += (s, e) =>
            {
                if (_suppressSliderEvents) return;
                int v = (int)Math.Round(_sldCurveMin.Value);
                if (v >= _cfg.CurveMaxTemp - 5) v = _cfg.CurveMaxTemp - 5;
                _cfg.CurveMinTemp = v;
                _txtCurveMinVal.Text = v + " °C";
                _cfg.Save();
                ApplyFanControl();
                DrawFanCurveGraph();
            };
            dpMin.Children.Add(_sldCurveMin);
            spCurveSliders.Children.Add(dpMin);

            // Row 2: Max Temp Slider
            DockPanel dpMax = new DockPanel();
            dpMax.Children.Add(new TextBlock
            {
                Text = "MAX BLAST TEMP (8000 RPM MAX):",
                Width = 250,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });
            StackPanel spMaxBtns = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(spMaxBtns, Dock.Right);
            spMaxBtns.Children.Add(CreateStepperButton("-5°C", () => AdjustCurveTemp(false, -5)));
            _txtCurveMaxVal = new TextBlock
            {
                Text = _cfg.CurveMaxTemp + " °C",
                Width = 68,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = AccentAmber,
                VerticalAlignment = VerticalAlignment.Center
            };
            spMaxBtns.Children.Add(_txtCurveMaxVal);
            spMaxBtns.Children.Add(CreateStepperButton("+5°C", () => AdjustCurveTemp(false, +5)));
            dpMax.Children.Add(spMaxBtns);

            _sldCurveMax = new Slider
            {
                Minimum = 60,
                Maximum = 95,
                Value = _cfg.CurveMaxTemp,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 16, 0)
            };
            _sldCurveMax.ValueChanged += (s, e) =>
            {
                if (_suppressSliderEvents) return;
                int v = (int)Math.Round(_sldCurveMax.Value);
                if (v <= _cfg.CurveMinTemp + 5) v = _cfg.CurveMinTemp + 5;
                _cfg.CurveMaxTemp = v;
                _txtCurveMaxVal.Text = v + " °C";
                _cfg.Save();
                ApplyFanControl();
                DrawFanCurveGraph();
            };
            dpMax.Children.Add(_sldCurveMax);
            spCurveSliders.Children.Add(dpMax);

            _pnlFanCurveSettings.Child = spCurveSliders;
            sp.Children.Add(_pnlFanCurveSettings);

            // Manual Constant RPM Panel
            _pnlFanManualSettings = new Border
            {
                Background = SubCardBg,
                BorderBrush = MutedBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 14, 18, 14),
                Visibility = Visibility.Collapsed
            };
            StackPanel spManual = new StackPanel();
            DockPanel dpManual = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            dpManual.Children.Add(new TextBlock
            {
                Text = "MANUAL TURBINE TARGET SPEED:",
                Width = 230,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });
            _txtManualRpmVal = new TextBlock
            {
                Text = _cfg.ManualRpm + " RPM",
                Width = 110,
                TextAlignment = TextAlignment.Right,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = AccentMagenta,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_txtManualRpmVal, Dock.Right);
            dpManual.Children.Add(_txtManualRpmVal);

            _sldManualRpm = new Slider
            {
                Minimum = _fanMinRpm,
                Maximum = _fanMaxRpm,
                Value = Math.Max(_fanMinRpm, Math.Min(_fanMaxRpm, _cfg.ManualRpm)),
                TickFrequency = 100,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0)
            };
            _sldManualRpm.ValueChanged += (s, e) =>
            {
                if (_suppressSliderEvents) return;
                _cfg.ManualRpm = (int)Math.Round(_sldManualRpm.Value);
                _txtManualRpmVal.Text = _cfg.ManualRpm + " RPM";
                _cfg.Save();
                if (_cfg.FanMode == "Manual") ApplyFanControl();
                DrawFanCurveGraph();
            };
            dpManual.Children.Add(_sldManualRpm);
            spManual.Children.Add(dpManual);

            UniformGrid rpmPresets = new UniformGrid { Columns = 4 };
            rpmPresets.Children.Add(CreateStepperButton("SILENT (2700 RPM)", () => _sldManualRpm.Value = _fanMinRpm));
            rpmPresets.Children.Add(CreateStepperButton("CRUISE (4200 RPM)", () => _sldManualRpm.Value = 4200));
            rpmPresets.Children.Add(CreateStepperButton("COOLING (5800 RPM)", () => _sldManualRpm.Value = 5800));
            rpmPresets.Children.Add(CreateStepperButton("MAX BLAST (" + (int)_fanMaxRpm + " RPM)", () => _sldManualRpm.Value = _fanMaxRpm));
            spManual.Children.Add(rpmPresets);

            _pnlFanManualSettings.Child = spManual;
            sp.Children.Add(_pnlFanManualSettings);

            stack.Children.Add(card);
            return stack;
        }

        // =====================================================================
        // TAB 3: TURBO BOOST & PER-CORE DIE TELEMETRY
        // =====================================================================
        private UIElement BuildTab3TurboAndCores()
        {
            StackPanel stack = new StackPanel();

            Border cardTurbo = CreateCyberCard("INTEL SPEEDSHIFT & TURBO BOOST // OVERCLOCK / THERMAL SWEET SPOT", ColAmber);
            StackPanel spTurbo = (StackPanel)cardTurbo.Child;

            DockPanel dpMasterTurbo = new DockPanel { Margin = new Thickness(4, 0, 4, 12) };
            _txtTurboDetail = new TextBlock
            {
                Text = "Active Profile: Sweet Spot Boost (2.2 GHz AC / 1.8 GHz Battery)",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = AccentGreen,
                VerticalAlignment = VerticalAlignment.Center
            };
            StackPanel spMasterSwitch = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(spMasterSwitch, Dock.Right);
            spMasterSwitch.Children.Add(new TextBlock
            {
                Text = "MASTER TURBO BOOST:",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            _tglTurboMaster = new CyberToggleSwitch(_activeTurboMode != "Off", ColCyan);
            _tglTurboMaster.Toggled += (on) => ApplyTurboMode(on ? "Sweet" : "Off");
            spMasterSwitch.Children.Add(_tglTurboMaster);

            dpMasterTurbo.Children.Add(spMasterSwitch);
            dpMasterTurbo.Children.Add(_txtTurboDetail);
            spTurbo.Children.Add(dpMasterTurbo);

            UniformGrid turboGrid = new UniformGrid { Columns = 3 };
            _btnTurboOff   = CreateCyberButton("🌙 TURBO OFF (1.1 GHz BASE)\nUltra-Cool & Silent  •  Max Battery Life", false, ColCyan);
            _btnTurboSweet = CreateCyberButton("⚡ SWEET SPOT (2.2 GHz AC / 1.8 DC)\nRecommended  •  Zero 400 MHz Throttling", true, ColGreen);
            _btnTurboFull  = CreateCyberButton("🔥 FULL BOOST (3.5 GHz MAX)\nUncapped Clock  •  Peak Burst Speed", false, ColAmber);

            _btnTurboOff.Margin = new Thickness(0, 0, 6, 0);
            _btnTurboSweet.Margin = new Thickness(3, 0, 3, 0);
            _btnTurboFull.Margin = new Thickness(6, 0, 0, 0);

            _btnTurboOff.Click   += (s, e) => ApplyTurboMode("Off");
            _btnTurboSweet.Click += (s, e) => ApplyTurboMode("Sweet");
            _btnTurboFull.Click  += (s, e) => ApplyTurboMode("Full");

            turboGrid.Children.Add(_btnTurboOff);
            turboGrid.Children.Add(_btnTurboSweet);
            turboGrid.Children.Add(_btnTurboFull);
            spTurbo.Children.Add(turboGrid);

            stack.Children.Add(cardTurbo);

            // Per-Core & Die Sensors Card
            Border cardCores = CreateCyberCard("INTEL CORE i5-1030NG7 // INDIVIDUAL CORE & GPU DIE TEMPERATURES", ColCyan);
            StackPanel spCores = (StackPanel)cardCores.Child;

            UniformGrid coreGrid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
            _txtCore1   = AddSensorMatrixTile(coreGrid, "CPU CORE 1 DIE", "TC1C", "-- °C", ColCyan, out _barCore1);
            _txtCore2   = AddSensorMatrixTile(coreGrid, "CPU CORE 2 DIE", "TC2C", "-- °C", ColCyan, out _barCore2);
            _txtCore3   = AddSensorMatrixTile(coreGrid, "CPU CORE 3 DIE", "TC3C", "-- °C", ColCyan, out _barCore3);
            _txtCore4   = AddSensorMatrixTile(coreGrid, "CPU CORE 4 DIE", "TC4C", "-- °C", ColCyan, out _barCore4);
            _txtGpuTemp = AddSensorMatrixTile(coreGrid, "IRIS PLUS GPU DIE", "TCGC", "-- °C", ColMagenta, out _barGpuTemp);
            _txtSaTemp  = AddSensorMatrixTile(coreGrid, "SYSTEM AGENT DIE", "TCSA", "-- °C", ColGreen, out _barSaTemp);

            spCores.Children.Add(coreGrid);
            stack.Children.Add(cardCores);

            return stack;
        }

        // =====================================================================
        // TAB 4: ALDENTE BATTERY CHARGE LIMITER & WINDOWS STARTUP
        // =====================================================================
        private UIElement BuildTab4BatteryAndSystem()
        {
            StackPanel stack = new StackPanel();

            Border cardBatt = CreateCyberCard("ALDENTE HARDWARE BATTERY LIMITER // T2 SMC CHARGE MANAGEMENT", ColGreen);
            StackPanel spBatt = (StackPanel)cardBatt.Child;

            _txtBattStatusLine = new TextBlock
            {
                Text = "Battery Charge: --%   •   Checking status...",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = AccentGreen,
                Margin = new Thickness(2, 2, 0, 4)
            };
            _txtBattSmcState = new TextBlock
            {
                Text = "Hardware SMC Limit (BCLM): 80%",
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 12,
                Foreground = TextMuted,
                Margin = new Thickness(2, 0, 0, 12)
            };
            spBatt.Children.Add(_txtBattStatusLine);
            spBatt.Children.Add(_txtBattSmcState);

            // Visual 20-Cell Gotham Gold Battery Pack Bar
            Border battBarBox = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 6, 7, 10)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(85, 229, 184, 66)),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 14)
            };
            _battVisualCanvas = new Canvas { Height = 36, ClipToBounds = true };
            _battVisualCanvas.SizeChanged += (s, e) => DrawBatteryVisualBar(GetCurrentBatteryPercent());
            battBarBox.Child = _battVisualCanvas;
            spBatt.Children.Add(battBarBox);

            // AlDente Slider + Steppers + Top Up to 100% Row
            Border capCtrlBox = new Border
            {
                Background = SubCardBg,
                BorderBrush = MutedBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 14)
            };
            DockPanel dpCap = new DockPanel();
            dpCap.Children.Add(new TextBlock
            {
                Text = "ALDENTE TARGET CHARGE CEILING:",
                Width = 235,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });

            StackPanel spCapRight = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(spCapRight, Dock.Right);
            spCapRight.Children.Add(CreateStepperButton("-5%", () => AdjustBatteryCap(-5)));
            _txtBattCapTarget = new TextBlock
            {
                Text = _cfg.BatteryCapPct + "%",
                Width = 62,
                TextAlignment = TextAlignment.Center,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = AccentGreen,
                VerticalAlignment = VerticalAlignment.Center
            };
            spCapRight.Children.Add(_txtBattCapTarget);
            spCapRight.Children.Add(CreateStepperButton("+5%", () => AdjustBatteryCap(+5)));

            _btnBattTopUp100 = CreateCyberButton("⚡ TOP UP TO 100%", false, ColAmber);
            _btnBattTopUp100.Margin = new Thickness(12, 0, 0, 0);
            _btnBattTopUp100.Padding = new Thickness(14, 7, 14, 7);
            _btnBattTopUp100.Click += (s, e) =>
            {
                _cfg.BatteryCapEnabled = false;
                _cfg.Save();
                ApplyBatteryCap(true);
            };
            spCapRight.Children.Add(_btnBattTopUp100);
            dpCap.Children.Add(spCapRight);

            _sldBattCap = new Slider
            {
                Minimum = 50,
                Maximum = 100,
                Value = _cfg.BatteryCapPct,
                TickFrequency = 5,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 16, 0)
            };
            _sldBattCap.ValueChanged += (s, e) =>
            {
                if (_suppressSliderEvents) return;
                _cfg.BatteryCapPct = (int)Math.Round(_sldBattCap.Value);
                _cfg.BatteryCapEnabled = _cfg.BatteryCapPct < 100;
                _cfg.Save();
                ApplyBatteryCap(false);
            };
            dpCap.Children.Add(_sldBattCap);
            capCtrlBox.Child = dpCap;
            spBatt.Children.Add(capCtrlBox);

            // 4 T2 Smart Battery Telemetry Tiles
            UniformGrid battTiles = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 4) };
            _txtBattTilePct    = AddSubHudBar(battTiles, "CHARGE (BRSC)", "--%", ColGreen);
            _txtBattTileBclm   = AddSubHudBar(battTiles, "SMC LIMIT (BCLM)", "80%", ColCyan);
            _txtBattTileCycles = AddSubHudBar(battTiles, "CYCLES (B0CT)", "--", ColAmber);
            _txtBattTileVolt   = AddSubHudBar(battTiles, "VOLTAGE (B0AV)", "-- V", ColMagenta);
            spBatt.Children.Add(battTiles);

            stack.Children.Add(cardBatt);

            // System & Startup Toggle Switches Card
            Border cardSys = CreateCyberCard("SYSTEM & AUTOMATION TOGGLES // WINDOWS 10 LTSC INTEGRATION", ColCyan);
            StackPanel spSys = (StackPanel)cardSys.Child;

            UniformGrid sysToggles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };

            _tglBattCap = new CyberToggleSwitch(_cfg.BatteryCapEnabled, ColGreen);
            _tglBattCap.Toggled += (on) =>
            {
                _cfg.BatteryCapEnabled = on;
                _cfg.Save();
                ApplyBatteryCap(!on);
            };
            sysToggles.Children.Add(CreateToggleCard(
                "ENABLE HARDWARE CHARGE CAP",
                "Write target ceiling directly to Apple T2 BCLM & CH0B",
                _tglBattCap, ColGreen));

            SetAutoStart(true);
            _tglStartWithWindows = new CyberToggleSwitch(IsAutoStartRegistered(), ColCyan);
            _tglStartWithWindows.Toggled += (on) => SetAutoStart(on);
            sysToggles.Children.Add(CreateToggleCard(
                "AUTO-START WITH WINDOWS",
                "Starts minimized in Tray on AC & Battery (No UAC nag)",
                _tglStartWithWindows, ColCyan));

            _tglSafetyReset = new CyberToggleSwitch(_cfg.SafetyResetOnExit, ColAmber);
            _tglSafetyReset.Toggled += (on) => { _cfg.SafetyResetOnExit = on; _cfg.Save(); };
            sysToggles.Children.Add(CreateToggleCard(
                "BOOT CAMP SHUTDOWN SAFETY",
                "Release charge inhibit on Shutdown if battery < 25%",
                _tglSafetyReset, ColAmber));

            spSys.Children.Add(sysToggles);
            stack.Children.Add(cardSys);

            return stack;
        }

        // =====================================================================
        // Custom Vector Graph Renderers (Waveform, Fan Curve, Battery Bar)
        // =====================================================================
        private void DrawWaveformGraph()
        {
            if (_waveformCanvas == null || _waveformCanvas.ActualWidth < 20) return;
            _waveformCanvas.Children.Clear();

            double w = _waveformCanvas.ActualWidth;
            double h = _waveformCanvas.ActualHeight;

            // Subtle Cyberpunk Grid Lines
            for (int i = 1; i <= 3; i++)
            {
                double y = h * i / 4.0;
                Line gridLine = new Line
                {
                    X1 = 0, Y1 = y, X2 = w, Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromArgb(30, 148, 163, 184)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection(new double[] { 4, 4 })
                };
                _waveformCanvas.Children.Add(gridLine);
            }

            for (int i = 1; i <= 7; i++)
            {
                double x = w * i / 8.0;
                Line vLine = new Line
                {
                    X1 = x, Y1 = 0, X2 = x, Y2 = h,
                    Stroke = new SolidColorBrush(Color.FromArgb(20, 148, 163, 184)),
                    StrokeThickness = 1
                };
                _waveformCanvas.Children.Add(vLine);
            }

            DrawTracePolyline(_histCpuLoad, w, h, ColGreen, 1.5, false);
            DrawTracePolyline(_histFanPct, w, h, ColMagenta, 1.8, false);
            DrawTracePolyline(_histCpuTemp, w, h, ColCyan, 2.4, true);
        }

        private void DrawTracePolyline(List<double> values, double w, double h, Color color, double thickness, bool fillUnder)
        {
            if (values.Count < 2) return;

            PointCollection pts = new PointCollection();
            int n = values.Count;
            for (int i = 0; i < n; i++)
            {
                double x = (double)i / (MaxHistoryPoints - 1) * w;
                double clamped = Math.Max(0.0, Math.Min(100.0, values[i]));
                double y = h - (clamped / 100.0) * (h - 12) - 6;
                pts.Add(new Point(x, y));
            }

            if (fillUnder)
            {
                Polygon poly = new Polygon
                {
                    Points = new PointCollection(pts)
                    {
                        new Point(w, h),
                        new Point(0, h)
                    },
                    Fill = new LinearGradientBrush(
                        Color.FromArgb(65, color.R, color.G, color.B),
                        Color.FromArgb(4, color.R, color.G, color.B),
                        90.0)
                };
                _waveformCanvas.Children.Add(poly);
            }

            Polyline line = new Polyline
            {
                Points = pts,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round
            };
            _waveformCanvas.Children.Add(line);
        }

        private void DrawFanCurveGraph()
        {
            if (_fanCurveCanvas == null || _fanCurveCanvas.ActualWidth < 40) return;
            _fanCurveCanvas.Children.Clear();

            double w = _fanCurveCanvas.ActualWidth;
            double h = _fanCurveCanvas.ActualHeight;
            double padL = 46, padR = 18, padT = 12, padB = 22;
            double plotW = Math.Max(10, w - padL - padR);
            double plotH = Math.Max(10, h - padT - padB);

            // Grid + Y-axis RPM labels
            for (int i = 0; i <= 3; i++)
            {
                double ratio = (double)i / 3.0;
                double y = padT + (1.0 - ratio) * plotH;
                double rpmLbl = _fanMinRpm + ratio * (_fanMaxRpm - _fanMinRpm);

                Line gl = new Line
                {
                    X1 = padL, Y1 = y, X2 = w - padR, Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromArgb(35, 148, 163, 184)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection(new double[] { 4, 4 })
                };
                _fanCurveCanvas.Children.Add(gl);

                TextBlock tb = new TextBlock
                {
                    Text = ((int)Math.Round(rpmLbl)).ToString(),
                    FontFamily = new FontFamily("Consolas, Segoe UI"),
                    FontSize = 10,
                    Foreground = TextMuted
                };
                Canvas.SetLeft(tb, 2);
                Canvas.SetTop(tb, y - 7);
                _fanCurveCanvas.Children.Add(tb);
            }

            // X-axis Temp labels (40°C to 100°C)
            for (int t = 40; t <= 100; t += 10)
            {
                double x = padL + ((t - 40.0) / 60.0) * plotW;
                TextBlock tb = new TextBlock
                {
                    Text = t + "°C",
                    FontFamily = new FontFamily("Consolas, Segoe UI"),
                    FontSize = 10,
                    Foreground = TextMuted
                };
                Canvas.SetLeft(tb, x - 12);
                Canvas.SetTop(tb, h - 16);
                _fanCurveCanvas.Children.Add(tb);
            }

            // Build Curve Points
            PointCollection curvePts = new PointCollection();
            if (_cfg.FanMode == "Manual")
            {
                double rRatio = (_cfg.ManualRpm - _fanMinRpm) / Math.Max(1.0, _fanMaxRpm - _fanMinRpm);
                double y = padT + (1.0 - Math.Max(0, Math.Min(1, rRatio))) * plotH;
                curvePts.Add(new Point(padL, y));
                curvePts.Add(new Point(padL + plotW, y));
            }
            else
            {
                double minT = _cfg.FanMode == "Auto" ? 68 : _cfg.CurveMinTemp;
                double maxT = _cfg.FanMode == "Auto" ? 92 : _cfg.CurveMaxTemp;
                double x1 = padL + ((minT - 40.0) / 60.0) * plotW;
                double x2 = padL + ((maxT - 40.0) / 60.0) * plotW;
                double yBottom = padT + plotH;
                double yTop = padT;

                curvePts.Add(new Point(padL, yBottom));
                curvePts.Add(new Point(x1, yBottom));
                curvePts.Add(new Point(x2, yTop));
                curvePts.Add(new Point(padL + plotW, yTop));
            }

            Polyline curveLine = new Polyline
            {
                Points = curvePts,
                Stroke = new SolidColorBrush(ColMagenta),
                StrokeThickness = 2.8,
                Effect = new DropShadowEffect { Color = ColMagenta, BlurRadius = 10, ShadowDepth = 0 }
            };
            _fanCurveCanvas.Children.Add(curveLine);

            // Live Operating Point Crosshair Dot
            double curTempClamped = Math.Max(40.0, Math.Min(100.0, _lastCpuTemp));
            double curRpmRatio = (_lastFanRpm - _fanMinRpm) / Math.Max(1.0, _fanMaxRpm - _fanMinRpm);
            curRpmRatio = Math.Max(0.0, Math.Min(1.0, curRpmRatio));

            double dotX = padL + ((curTempClamped - 40.0) / 60.0) * plotW;
            double dotY = padT + (1.0 - curRpmRatio) * plotH;

            Ellipse liveDot = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(ColCyan),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Effect = new DropShadowEffect { Color = ColCyan, BlurRadius = 12, ShadowDepth = 0 }
            };
            Canvas.SetLeft(liveDot, dotX - 6);
            Canvas.SetTop(liveDot, dotY - 6);
            _fanCurveCanvas.Children.Add(liveDot);
        }

        private void DrawBatteryVisualBar(int currentPct)
        {
            if (_battVisualCanvas == null || _battVisualCanvas.ActualWidth < 40) return;
            _battVisualCanvas.Children.Clear();

            double w = _battVisualCanvas.ActualWidth;
            double h = _battVisualCanvas.ActualHeight;
            int segments = 20;
            double gap = 4.0;
            double segW = Math.Max(4.0, (w - (segments - 1) * gap) / segments);

            for (int i = 0; i < segments; i++)
            {
                int segThreshold = (i + 1) * 5;
                bool filled = currentPct >= segThreshold - 2;
                bool aboveCap = _cfg.BatteryCapEnabled && segThreshold > _cfg.BatteryCapPct;

                Color segCol = !filled ? Color.FromArgb(40, 71, 85, 105)
                             : aboveCap ? ColAmber
                             : ColGreen;

                Rectangle rect = new Rectangle
                {
                    Width = segW,
                    Height = h - 8,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = new SolidColorBrush(segCol)
                };
                Canvas.SetLeft(rect, i * (segW + gap));
                Canvas.SetTop(rect, 4);
                _battVisualCanvas.Children.Add(rect);
            }

            // Target Cap Marker Line
            if (_cfg.BatteryCapEnabled && _cfg.BatteryCapPct < 100)
            {
                double capX = (_cfg.BatteryCapPct / 100.0) * w;
                Line capLine = new Line
                {
                    X1 = capX, Y1 = 0, X2 = capX, Y2 = h,
                    Stroke = new SolidColorBrush(ColCyan),
                    StrokeThickness = 3,
                    Effect = new DropShadowEffect { Color = ColCyan, BlurRadius = 8, ShadowDepth = 0 }
                };
                _battVisualCanvas.Children.Add(capLine);
            }
        }

        // =====================================================================
        // Reusable Cyberpunk × Apple UI Component Helpers
        // =====================================================================
        private Border CreateTrafficLightButton(Color color, string tooltip, Action onClick)
        {
            Border dot = new Border
            {
                Width = 13,
                Height = 13,
                CornerRadius = new CornerRadius(6.5),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                BorderThickness = new Thickness(0.8),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Effect = new DropShadowEffect { Color = color, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.65 }
            };
            dot.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                onClick();
            };
            return dot;
        }

        private TextBlock CreateHudPillBadge(string text, Color color)
        {
            Border pill = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(35, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(4, 0, 4, 0)
            };
            TextBlock tb = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color)
            };
            pill.Child = tb;
            return tb;
        }

        private StackPanel CreateLegendDot(string label, Color color)
        {
            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
            sp.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center
            });
            return sp;
        }

        private Border CreateCyberCard(string heading, Color accent)
        {
            Border b = new Border
            {
                Background = CardBg,
                BorderBrush = new SolidColorBrush(Color.FromArgb(95, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18, 14, 18, 14),
                Margin = new Thickness(0, 0, 0, 14)
            };
            StackPanel sp = new StackPanel();

            DockPanel hdr = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            Border accentBar = new Border
            {
                Width = 4,
                Height = 14,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(accent),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock lbl = new TextBlock
            {
                Text = heading,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center
            };
            hdr.Children.Add(accentBar);
            hdr.Children.Add(lbl);
            sp.Children.Add(hdr);

            b.Child = sp;
            return b;
        }

        private Border CreateToggleCard(string title, string subtitle, CyberToggleSwitch toggle, Color accent)
        {
            Border tile = new Border
            {
                Background = SubCardBg,
                BorderBrush = new SolidColorBrush(Color.FromArgb(65, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(4)
            };
            DockPanel dp = new DockPanel();
            DockPanel.SetDock(toggle, Dock.Right);
            dp.Children.Add(toggle);

            StackPanel sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            sp.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White
            });
            sp.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 10.5,
                Foreground = TextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            dp.Children.Add(sp);
            tile.Child = dp;
            return tile;
        }

        private TextBlock AddSensorMatrixTile(UniformGrid parent, string label, string smcKey, string initialVal, Color accent, out Border fillBar)
        {
            Border tile = new Border
            {
                Background = SubCardBg,
                BorderBrush = MutedBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(4)
            };
            StackPanel sp = new StackPanel();

            DockPanel topRow = new DockPanel();
            TextBlock lblName = new TextBlock
            {
                Text = label,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center
            };
            Border keyBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(keyBadge, Dock.Right);
            keyBadge.Child = new TextBlock
            {
                Text = smcKey,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent)
            };
            topRow.Children.Add(keyBadge);
            topRow.Children.Add(lblName);
            sp.Children.Add(topRow);

            TextBlock lblVal = new TextBlock
            {
                Text = initialVal,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 21,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
                Margin = new Thickness(0, 5, 0, 6)
            };
            sp.Children.Add(lblVal);

            // Mini Neon Thermal Progress Track
            Border track = new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = new SolidColorBrush(Color.FromArgb(140, 10, 14, 24)),
                ClipToBounds = true
            };
            fillBar = new Border
            {
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 40,
                Background = new SolidColorBrush(accent)
            };
            track.Child = fillBar;
            sp.Children.Add(track);

            tile.Child = sp;
            parent.Children.Add(tile);
            return lblVal;
        }

        private TextBlock AddSubHudBar(UniformGrid parent, string label, string initialValue, Color accent)
        {
            Border tile = new Border
            {
                Background = SubCardBg,
                BorderBrush = new SolidColorBrush(Color.FromArgb(65, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(4)
            };
            DockPanel dp = new DockPanel();
            TextBlock lblCap = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = TextMuted,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock lblVal = new TextBlock
            {
                Text = initialValue,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 14.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            dp.Children.Add(lblCap);
            dp.Children.Add(lblVal);
            tile.Child = dp;
            parent.Children.Add(tile);
            return lblVal;
        }

        private Button CreateCyberButton(string text, bool active, Color accent)
        {
            return new Button
            {
                Content = new TextBlock
                {
                    Text = text,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    LineHeight = 17,
                    TextWrapping = TextWrapping.Wrap
                },
                Background = active
                    ? new SolidColorBrush(Color.FromArgb(65, accent.R, accent.G, accent.B))
                    : new SolidColorBrush(Color.FromArgb(215, 16, 18, 23)),
                Foreground = TextPrimary,
                BorderBrush = active
                    ? new SolidColorBrush(accent)
                    : MutedBorder,
                BorderThickness = new Thickness(1.5),
                Padding = new Thickness(10, 9, 10, 9),
                Cursor = Cursors.Hand
            };
        }

        private void StyleCyberButton(Button btn, bool active, Color accent)
        {
            if (btn == null) return;
            btn.Background = active
                ? new SolidColorBrush(Color.FromArgb(65, accent.R, accent.G, accent.B))
                : new SolidColorBrush(Color.FromArgb(215, 16, 18, 23));
            btn.BorderBrush = active
                ? new SolidColorBrush(accent)
                : MutedBorder;
        }

        private Button CreateStepperButton(string text, Action onClick)
        {
            Button b = new Button
            {
                Content = text,
                FontFamily = new FontFamily("Consolas, Segoe UI"),
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromArgb(225, 16, 18, 23)),
                Foreground = TextPrimary,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(2),
                Cursor = Cursors.Hand
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        // =====================================================================
        // System Tray Integration (Single-Click Open + Right-Click Menu)
        // =====================================================================
        private void InitializeTray()
        {
            _trayMenu = new WinForms.ContextMenuStrip
            {
                Font = new Drawing.Font("Segoe UI", 10f)
            };

            var itemOpen = _trayMenu.Items.Add("Open MacBook Air Gotham Gold Control Center", null, (s, e) => RestoreFromTray());
            itemOpen.Font = new Drawing.Font("Segoe UI", 10f, Drawing.FontStyle.Bold);
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

            _trayMenu.Items.Add("Fan: Smart Curve (60°C – 85°C)", null, (s, e) => SetFanMode("Curve"));
            _trayMenu.Items.Add("Fan: Apple SMC Auto", null, (s, e) => SetFanMode("Auto"));
            _trayMenu.Items.Add("Fan: Constant 4200 RPM", null, (s, e) => { _sldManualRpm.Value = 4200; SetFanMode("Manual"); });
            _trayMenu.Items.Add("Fan: Full Blast (" + (int)_fanMaxRpm + " RPM)", null, (s, e) => { _sldManualRpm.Value = _fanMaxRpm; SetFanMode("Manual"); });
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

            _trayMenu.Items.Add("Turbo: Sweet Spot (2.2 GHz AC / 1.8 GHz Batt)", null, (s, e) => ApplyTurboMode("Sweet"));
            _trayMenu.Items.Add("Turbo: OFF (1.1 GHz Silent / Max Battery)", null, (s, e) => ApplyTurboMode("Off"));
            _trayMenu.Items.Add("Turbo: Full Boost (3.5 GHz Max)", null, (s, e) => ApplyTurboMode("Full"));
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

            _trayMenu.Items.Add("Battery: Enable 80% Cap (AlDente)", null, (s, e) =>
            {
                _cfg.BatteryCapEnabled = true;
                _cfg.BatteryCapPct = 80;
                _cfg.Save();
                ApplyBatteryCap(false);
            });
            _trayMenu.Items.Add("Battery: Top Up to 100%", null, (s, e) =>
            {
                _cfg.BatteryCapEnabled = false;
                _cfg.Save();
                ApplyBatteryCap(true);
            });
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

            _trayMenu.Items.Add("Quit MacBook Air Control Center", null, (s, e) =>
            {
                _allowClose = true;
                Close();
            });

            _trayIcon = new WinForms.NotifyIcon
            {
                Text = "MacBook Air Control Center",
                Visible = true,
                ContextMenuStrip = _trayMenu
            };

            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                {
                    if (IsVisible && WindowState != WindowState.Minimized)
                        HideToTray();
                    else
                        RestoreFromTray();
                }
            };

            UpdateTrayIcon(50);
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void HideToTray()
        {
            Hide();
            _trayIcon.ShowBalloonTip(1200, "MacBook Air Control Center",
                "Running in the system tray. Click the tray temperature icon anytime to open.",
                WinForms.ToolTipIcon.Info);
        }

        private void UpdateTrayIcon(int tempC)
        {
            try
            {
                using (Drawing.Bitmap bmp = new Drawing.Bitmap(32, 32))
                using (Drawing.Graphics g = Drawing.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Drawing.Color.FromArgb(9, 10, 14));

                    Drawing.Color c = tempC >= 80 ? Drawing.Color.FromArgb(214, 54, 56)
                                    : tempC >= 65 ? Drawing.Color.FromArgb(255, 196, 46)
                                    : Drawing.Color.FromArgb(229, 184, 66);

                    using (Drawing.Pen pen = new Drawing.Pen(c, 2f))
                    {
                        g.DrawRectangle(pen, 1, 1, 29, 29);
                    }

                    string s = Math.Max(0, Math.Min(99, tempC)).ToString("00");
                    g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (Drawing.Font f = new Drawing.Font("Consolas", 13f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel))
                    using (Drawing.Brush b = new Drawing.SolidBrush(c))
                    {
                        Drawing.SizeF sz = g.MeasureString(s, f);
                        g.DrawString(s, f, b, (32 - sz.Width) / 2f, (32 - sz.Height) / 2f);
                    }

                    IntPtr hIcon = bmp.GetHicon();
                    Drawing.Icon old = _trayIcon.Icon;
                    _trayIcon.Icon = Drawing.Icon.FromHandle(hIcon);
                    if (old != null) DestroyIcon(old.Handle);
                }
            }
            catch { }
        }

        // =====================================================================
        // Fan Control Logic
        // =====================================================================
        private void SetFanMode(string mode)
        {
            _cfg.FanMode = mode;
            _cfg.Save();
            SyncFanUiState();
            ApplyFanControl();
            DrawFanCurveGraph();
        }

        private void SyncFanUiState()
        {
            _suppressSliderEvents = true;
            StyleCyberButton(_btnFanCurve, _cfg.FanMode == "Curve", ColMagenta);
            StyleCyberButton(_btnFanAuto, _cfg.FanMode == "Auto", ColCyan);
            StyleCyberButton(_btnFanManual, _cfg.FanMode == "Manual", ColAmber);

            if (_tglDashSmartFan != null) _tglDashSmartFan.IsChecked = (_cfg.FanMode == "Curve");

            _pnlFanCurveSettings.Visibility = _cfg.FanMode == "Curve" ? Visibility.Visible : Visibility.Collapsed;
            _pnlFanManualSettings.Visibility = _cfg.FanMode == "Manual" ? Visibility.Visible : Visibility.Collapsed;

            _sldCurveMin.Value = _cfg.CurveMinTemp;
            _sldCurveMax.Value = _cfg.CurveMaxTemp;
            _txtCurveMinVal.Text = _cfg.CurveMinTemp + " °C";
            _txtCurveMaxVal.Text = _cfg.CurveMaxTemp + " °C";

            if (_txtTopPowerBadge != null)
            {
                _txtTopPowerBadge.Text = "FAN: " + _cfg.FanMode.ToUpperInvariant();
            }
            _suppressSliderEvents = false;
        }

        private void AdjustCurveTemp(bool isMin, int delta)
        {
            if (isMin)
            {
                _cfg.CurveMinTemp = Math.Max(45, Math.Min(_cfg.CurveMaxTemp - 5, _cfg.CurveMinTemp + delta));
            }
            else
            {
                _cfg.CurveMaxTemp = Math.Max(_cfg.CurveMinTemp + 5, Math.Min(95, _cfg.CurveMaxTemp + delta));
            }
            _cfg.Save();
            SyncFanUiState();
            ApplyFanControl();
            DrawFanCurveGraph();
        }

        private void ApplyFanControl()
        {
            if (!_smc.IsConnected) return;

            if (_cfg.FanMode == "Auto")
            {
                _lastTargetRpm = _lastFanRpm;
                _smc.WriteByte("F0Md", 0);
                _txtFanTargetInfo.Text = string.Format("ACTIVE MODE: APPLE SMC FIRMWARE AUTO  •  BOUNDS: {0:0} – {1:0} RPM", _fanMinRpm, _fanMaxRpm);
            }
            else if (_cfg.FanMode == "Manual")
            {
                int target = (int)Math.Max(_fanMinRpm, Math.Min(_fanMaxRpm, _cfg.ManualRpm));
                _lastTargetRpm = target;
                _smc.WriteByte("F0Md", 1);
                _smc.WriteFloatOrFpe2("F0Tg", target);
                _txtFanTargetInfo.Text = string.Format("ACTIVE MODE: MANUAL CONSTANT TURBINE SPEED ({0} RPM)", target);
            }
            else
            {
                double tMin = _cfg.CurveMinTemp;
                double tMax = _cfg.CurveMaxTemp;
                if (tMax <= tMin) tMax = tMin + 10;

                double targetRpm;
                if (_lastCpuTemp <= tMin) targetRpm = _fanMinRpm;
                else if (_lastCpuTemp >= tMax) targetRpm = _fanMaxRpm;
                else
                {
                    double ratio = (_lastCpuTemp - tMin) / (tMax - tMin);
                    targetRpm = _fanMinRpm + ratio * (_fanMaxRpm - _fanMinRpm);
                }

                targetRpm = Math.Round(targetRpm / 50.0) * 50.0;
                _lastTargetRpm = targetRpm;
                _smc.WriteByte("F0Md", 1);
                _smc.WriteFloatOrFpe2("F0Tg", targetRpm);
                _txtFanTargetInfo.Text = string.Format("ACTIVE MODE: SMART CURVE ({0}°C – {1}°C)  •  TARGET: {2:0} RPM  •  BOUNDS: {3:0}–{4:0} RPM",
                    _cfg.CurveMinTemp, _cfg.CurveMaxTemp, targetRpm, _fanMinRpm, _fanMaxRpm);
            }
        }

        // =====================================================================
        // Turbo Boost & Power Plan Logic
        // =====================================================================
        private void DetectCurrentTurboState()
        {
            try
            {
                string output = Program.RunHiddenCmd("powercfg.exe", "/query SCHEME_CURRENT SUB_PROCESSOR");
                bool is99 = output.Contains("bc5038f7-23e0-4960-96da-33abaf5935ec") && output.Contains("0x00000063");
                bool is2200 = output.Contains("0x00000898");

                if (is99) _activeTurboMode = "Off";
                else if (is2200) _activeTurboMode = "Sweet";
                else _activeTurboMode = "Full";
            }
            catch
            {
                _activeTurboMode = "Sweet";
            }
            SyncTurboUiState();
        }

        private void ApplyTurboMode(string mode)
        {
            _activeTurboMode = mode;
            if (mode == "Off")
            {
                Program.RunHiddenCmd("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 99");
                Program.RunHiddenCmd("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 99");
                Program.RunHiddenCmd("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 0");
                Program.RunHiddenCmd("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 0");
                Program.RunHiddenCmd("powercfg.exe", "/setactive SCHEME_CURRENT");
            }
            else if (mode == "Sweet")
            {
                Program.RunHiddenCmd("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100");
                Program.RunHiddenCmd("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100");
                Program.RunHiddenCmd("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCFREQMAX 2200");
                Program.RunHiddenCmd("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCFREQMAX 1800");
                Program.RunHiddenCmd("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 3");
                Program.RunHiddenCmd("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 3");
                Program.RunHiddenCmd("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP 50");
                Program.RunHiddenCmd("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP 65");
                Program.RunHiddenCmd("powercfg.exe", "/setactive SCHEME_CURRENT");
            }
            else if (mode == "Full")
            {
                Program.RunHiddenCmd("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100");
                Program.RunHiddenCmd("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100");
                Program.RunHiddenCmd("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCFREQMAX 0");
                Program.RunHiddenCmd("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCFREQMAX 0");
                Program.RunHiddenCmd("powercfg.exe", "/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 2");
                Program.RunHiddenCmd("powercfg.exe", "/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE 3");
                Program.RunHiddenCmd("powercfg.exe", "/setactive SCHEME_CURRENT");
            }
            SyncTurboUiState();
        }

        private void SyncTurboUiState()
        {
            StyleCyberButton(_btnTurboOff, _activeTurboMode == "Off", ColCyan);
            StyleCyberButton(_btnTurboSweet, _activeTurboMode == "Sweet", ColGreen);
            StyleCyberButton(_btnTurboFull, _activeTurboMode == "Full", ColAmber);

            StyleCyberButton(_btnQuickSilent, _activeTurboMode == "Off", ColCyan);
            StyleCyberButton(_btnQuickSweet, _activeTurboMode == "Sweet", ColGreen);
            StyleCyberButton(_btnQuickOverdrive, _activeTurboMode == "Full", ColMagenta);

            if (_tglDashTurboBoost != null) _tglDashTurboBoost.IsChecked = (_activeTurboMode != "Off");
            if (_tglTurboMaster != null) _tglTurboMaster.IsChecked = (_activeTurboMode != "Off");

            if (_activeTurboMode == "Off")
            {
                _txtTurboDetail.Text = "ACTIVE PROFILE: TURBO BOOST OFF (1.1 GHz BASE CLOCK — ULTRA COOL & SILENT)";
                _txtTurboDetail.Foreground = AccentBlue;
                if (_txtTopTurboBadge != null) _txtTopTurboBadge.Text = "TURBO: OFF (1.1 GHz)";
            }
            else if (_activeTurboMode == "Sweet")
            {
                _txtTurboDetail.Text = "ACTIVE PROFILE: SWEET SPOT BOOST (2.2 GHz AC / 1.8 GHz BATT — ZERO THROTTLING)";
                _txtTurboDetail.Foreground = AccentGreen;
                if (_txtTopTurboBadge != null) _txtTopTurboBadge.Text = "TURBO: SWEET SPOT";
            }
            else
            {
                _txtTurboDetail.Text = "ACTIVE PROFILE: FULL OVERDRIVE BOOST (3.5 GHz UNCAPPED — PEAK PERFORMANCE)";
                _txtTurboDetail.Foreground = AccentAmber;
                if (_txtTopTurboBadge != null) _txtTopTurboBadge.Text = "TURBO: 3.5 GHz MAX";
            }
        }

        // =====================================================================
        // AlDente Battery Charge Limiter (80% Cap)
        // =====================================================================
        private int GetCurrentBatteryPercent()
        {
            WinForms.PowerStatus ps = WinForms.SystemInformation.PowerStatus;
            int winPct = (int)Math.Round(ps.BatteryLifePercent * 100.0);
            double? smcPct = _smc.ReadNumber("BRSC");
            if (smcPct.HasValue && smcPct.Value >= 1 && smcPct.Value <= 100 && (winPct <= 0 || winPct > 100))
            {
                winPct = (int)smcPct.Value;
            }
            return Math.Max(0, Math.Min(100, winPct));
        }

        private void AdjustBatteryCap(int delta)
        {
            _cfg.BatteryCapPct = Math.Max(50, Math.Min(100, _cfg.BatteryCapPct + delta));
            _cfg.BatteryCapEnabled = _cfg.BatteryCapPct < 100;
            _cfg.Save();
            ApplyBatteryCap(false);
        }

        private void ApplyBatteryCap(bool forceTopUp100)
        {
            _suppressSliderEvents = true;
            int target = (!forceTopUp100 && _cfg.BatteryCapEnabled) ? _cfg.BatteryCapPct : 100;
            if (_txtBattCapTarget != null) _txtBattCapTarget.Text = _cfg.BatteryCapPct + "%";
            if (_sldBattCap != null) _sldBattCap.Value = _cfg.BatteryCapPct;
            if (_tglBattCap != null) _tglBattCap.IsChecked = (_cfg.BatteryCapEnabled && !forceTopUp100);
            if (_tglDashBattCap != null) _tglDashBattCap.IsChecked = (_cfg.BatteryCapEnabled && !forceTopUp100);
            if (_txtTopBattBadge != null)
            {
                _txtTopBattBadge.Text = (_cfg.BatteryCapEnabled && !forceTopUp100)
                    ? "ALDENTE: " + _cfg.BatteryCapPct + "% CAP"
                    : "ALDENTE: 100% FULL";
            }
            _suppressSliderEvents = false;

            if (!_smc.IsConnected) return;

            byte bTarget = (byte)target;
            if (_smc.KeyExists("BCLM")) _smc.WriteByte("BCLM", bTarget);
            if (_smc.KeyExists("BFCL")) _smc.WriteByte("BFCL", bTarget);
            if (_smc.KeyExists("CHLS")) _smc.WriteUInt16BE("CHLS", bTarget);

            if (target == 100)
            {
                SetChargeInhibit(false);
            }
            UpdateBatteryTelemetry();
        }

        private void SetChargeInhibit(bool inhibit)
        {
            if (!_smc.IsConnected) return;
            byte val = inhibit ? (byte)0x02 : (byte)0x00;
            byte flag = inhibit ? (byte)0x01 : (byte)0x00;
            if (_smc.KeyExists("CH0B")) _smc.WriteByte("CH0B", val);
            if (_smc.KeyExists("CH0C")) _smc.WriteByte("CH0C", flag);
            _isChargingInhibited = inhibit;
        }

        private void UpdateBatteryTelemetry()
        {
            WinForms.PowerStatus ps = WinForms.SystemInformation.PowerStatus;
            int winPct = GetCurrentBatteryPercent();
            bool onAc = ps.PowerLineStatus == WinForms.PowerLineStatus.Online;

            double? cycles = _smc.ReadNumber("B0CT");
            double? bclm = _smc.ReadNumber("BCLM");
            double? voltMv = _smc.ReadNumber("B0AV");

            if (_cfg.BatteryCapEnabled && _cfg.BatteryCapPct < 100)
            {
                if (onAc && winPct >= _cfg.BatteryCapPct)
                {
                    if (!_isChargingInhibited) SetChargeInhibit(true);
                }
                else if (winPct <= _cfg.BatteryCapPct - 3)
                {
                    if (_isChargingInhibited) SetChargeInhibit(false);
                }
            }

            string acStr = onAc
                ? ((_isChargingInhibited || (_cfg.BatteryCapEnabled && winPct >= _cfg.BatteryCapPct))
                    ? "AC ONLINE (HELD AT " + _cfg.BatteryCapPct + "% ALDENTE CAP)"
                    : "AC ONLINE (CHARGING)")
                : "DISCHARGING ON BATTERY";

            string voltStr = (voltMv.HasValue && voltMv.Value > 5000 && voltMv.Value < 20000)
                ? string.Format("{0:0.00} V", voltMv.Value / 1000.0)
                : "-- V";

            string bclmStr = bclm.HasValue
                ? ((int)bclm.Value) + "%"
                : (_cfg.BatteryCapEnabled ? _cfg.BatteryCapPct + "%" : "100%");

            string cycStr = cycles.HasValue ? ((int)cycles.Value).ToString() : "N/A";

            _txtBattStatusLine.Text = string.Format("BATTERY CHARGE: {0}%   •   {1}   •   {2}", winPct, acStr, voltStr);
            _txtBattSmcState.Text = string.Format("SMC HARDWARE LIMIT (BCLM): {0}   •   CHARGE INHIBIT: {1}   •   CYCLE COUNT: {2}",
                bclmStr,
                _isChargingInhibited ? "ACTIVE (HOLD)" : "NORMAL",
                cycStr);

            if (_txtBattTilePct != null)    _txtBattTilePct.Text = winPct + "%";
            if (_txtBattTileBclm != null)   _txtBattTileBclm.Text = bclmStr;
            if (_txtBattTileCycles != null) _txtBattTileCycles.Text = cycStr;
            if (_txtBattTileVolt != null)   _txtBattTileVolt.Text = voltStr;

            DrawBatteryVisualBar(winPct);
        }

        // =====================================================================
        // Windows Auto-Start (Scheduled Task with Highest Privileges & Battery)
        // =====================================================================
        private const string TaskName = "MacBookAirControlCenter";

        private bool IsAutoStartRegistered()
        {
            string outStr = Program.RunHiddenCmd("schtasks.exe", "/Query /TN \"" + TaskName + "\"");
            return outStr.IndexOf(TaskName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SetAutoStart(bool enable)
        {
            string exePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MacAirControl.exe");
            if (enable)
            {
                string args = string.Format("/Create /F /RL HIGHEST /SC ONLOGON /TN \"{0}\" /TR \"\\\"{1}\\\" --minimized\"", TaskName, exePath);
                Program.RunHiddenCmd("schtasks.exe", args);
                string psCmd = "-NoProfile -WindowStyle Hidden -Command \"Set-ScheduledTask -TaskName '" + TaskName + "' -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero))\"";
                Program.RunHiddenCmd("powershell.exe", psCmd);
            }
            else
            {
                Program.RunHiddenCmd("schtasks.exe", "/Delete /F /TN \"" + TaskName + "\"");
            }
        }

        // =====================================================================
        // Periodic Telemetry Poll & Afterburner Cockpit Update
        // =====================================================================
        private void UpdateSensorTileBar(TextBlock txt, Border bar, double? val, Color normalCol)
        {
            if (!val.HasValue || val.Value <= 5 || val.Value >= 130) return;
            double v = val.Value;
            Color c = v >= 80 ? ColRed : (v >= 65 ? ColAmber : normalCol);
            SolidColorBrush b = new SolidColorBrush(c);
            txt.Text = v.ToString("0.0") + " °C";
            txt.Foreground = b;
            if (bar != null)
            {
                Border parent = bar.Parent as Border;
                double maxW = (parent != null && parent.ActualWidth > 10) ? parent.ActualWidth : 120.0;
                bar.Width = Math.Max(6.0, Math.Min(maxW, (v / 100.0) * maxW));
                bar.Background = b;
            }
        }

        private void UpdateTelemetryAndControl()
        {
            if (!_smc.IsConnected)
            {
                _smc.Connect();
                _txtSmcStatus.Text = _smc.StatusMessage + "  •  MacBookAir9,1 (Core i5-1030NG7)";
                _txtSmcStatus.Foreground = _smc.IsConnected ? AccentGreen : AccentAmber;
            }

            double? cpuTemp   = _smc.ReadNumber("TCXC") ?? _smc.ReadNumber("TC0E") ?? _smc.ReadNumber("TC0F") ?? _smc.ReadNumber("TC0P");
            double? proxTemp  = _smc.ReadNumber("TC0P") ?? _smc.ReadNumber("TC0E");
            double? boardTemp = _smc.ReadNumber("TW0P") ?? _smc.ReadNumber("Th0N") ?? _smc.ReadNumber("TH0X");
            double? battTemp  = _smc.ReadNumber("TB0T") ?? _smc.ReadNumber("TB1T");
            double? palmTemp  = _smc.ReadNumber("Ts0P") ?? _smc.ReadNumber("Ts1P");

            if (cpuTemp.HasValue && cpuTemp.Value > 5 && cpuTemp.Value < 125)
            {
                _lastCpuTemp = cpuTemp.Value;
                UpdateTrayIcon((int)Math.Round(cpuTemp.Value));
            }

            UpdateSensorTileBar(_txtCpuTemp,   _barCpuTemp,   cpuTemp,   ColCyan);
            UpdateSensorTileBar(_txtProxTemp,  _barProxTemp,  proxTemp,  ColGreen);
            UpdateSensorTileBar(_txtBoardTemp, _barBoardTemp, boardTemp, ColGreen);
            UpdateSensorTileBar(_txtBattTemp,  _barBattTemp,  battTemp,  ColAmber);
            UpdateSensorTileBar(_txtPalmTemp,  _barPalmTemp,  palmTemp,  ColCyan);

            // Per-core & die sensors (Tab 3)
            UpdateSensorTileBar(_txtCore1,   _barCore1,   _smc.ReadNumber("TC1C"), ColCyan);
            UpdateSensorTileBar(_txtCore2,   _barCore2,   _smc.ReadNumber("TC2C"), ColCyan);
            UpdateSensorTileBar(_txtCore3,   _barCore3,   _smc.ReadNumber("TC3C"), ColCyan);
            UpdateSensorTileBar(_txtCore4,   _barCore4,   _smc.ReadNumber("TC4C"), ColCyan);
            UpdateSensorTileBar(_txtGpuTemp, _barGpuTemp, _smc.ReadNumber("TCGC"), ColMagenta);
            UpdateSensorTileBar(_txtSaTemp,  _barSaTemp,  _smc.ReadNumber("TCSA"), ColGreen);

            double? fanRpm = _smc.ReadNumber("F0Ac");
            if (fanRpm.HasValue && fanRpm.Value >= 0 && fanRpm.Value <= 10000)
            {
                _lastFanRpm = fanRpm.Value;
                _txtFanRpm.Text = string.Format("{0:0} RPM", fanRpm.Value);
            }
            ApplyFanControl();

            try
            {
                float perfPct = _cpuPerfCounter != null ? _cpuPerfCounter.NextValue() : 0f;
                float loadPct = _cpuLoadCounter != null ? _cpuLoadCounter.NextValue() : 0f;

                if (perfPct <= 1f)
                {
                    perfPct = _activeTurboMode == "Off" ? 100f : (_activeTurboMode == "Sweet" ? 185f : 240f);
                }

                _lastEstMhz = (int)Math.Round(1100.0 * (perfPct / 100.0));
                _lastCpuLoad = Math.Max(1.0, Math.Min(100.0, loadPct));
                _txtCpuClock.Text = string.Format("{0} MHz ({1:0.00} GHz)", _lastEstMhz, _lastEstMhz / 1000.0);
                _txtCpuLoad.Text = string.Format("{0:0}%", _lastCpuLoad);
            }
            catch { }

            // Update MSI Afterburner Left Dial (CPU Core & Clock)
            Color cpuDialCol = _lastCpuTemp >= 80 ? ColRed : (_lastCpuTemp >= 65 ? ColAmber : ColCyan);
            string turboBadge = _activeTurboMode == "Off" ? "SILENT 1.1 GHz"
                              : (_activeTurboMode == "Sweet" ? "SWEET SPOT 2.2 GHz" : "FULL BOOST 3.5 GHz");
            _dialCpu.UpdateDial(
                (_lastCpuTemp - 25.0) / 75.0,
                _lastCpuTemp.ToString("0.0"),
                string.Format("{0} MHz ({1:0.00} GHz)", _lastEstMhz, _lastEstMhz / 1000.0),
                string.Format("CPU LOAD: {0:0}%", _lastCpuLoad),
                turboBadge,
                cpuDialCol);

            // Update MSI Afterburner Right Dial (Exhaust Turbine & Target)
            double fanPct = (_lastFanRpm - _fanMinRpm) / Math.Max(1.0, _fanMaxRpm - _fanMinRpm);
            fanPct = Math.Max(0.0, Math.Min(1.0, fanPct));
            string fanBadge = _cfg.FanMode == "Curve"
                ? string.Format("SMART CURVE ({0}–{1}°C)", _cfg.CurveMinTemp, _cfg.CurveMaxTemp)
                : (_cfg.FanMode == "Auto" ? "APPLE SMC AUTO" : "MANUAL CONSTANT");
            _dialFan.UpdateDial(
                fanPct,
                _lastFanRpm.ToString("0"),
                string.Format("TARGET: {0:0} RPM ({1:0}%)", _lastTargetRpm, fanPct * 100.0),
                string.Format("BOUNDS: {0:0}–{1:0} RPM", _fanMinRpm, _fanMaxRpm),
                fanBadge,
                ColMagenta);

            // Append to Scrolling Waveform History
            _histCpuTemp.Add(_lastCpuTemp);
            _histFanPct.Add(fanPct * 100.0);
            _histCpuLoad.Add(_lastCpuLoad);
            while (_histCpuTemp.Count > MaxHistoryPoints) _histCpuTemp.RemoveAt(0);
            while (_histFanPct.Count > MaxHistoryPoints)  _histFanPct.RemoveAt(0);
            while (_histCpuLoad.Count > MaxHistoryPoints) _histCpuLoad.RemoveAt(0);

            if (_selectedTabIndex == 0) DrawWaveformGraph();
            else if (_selectedTabIndex == 1) DrawFanCurveGraph();

            UpdateBatteryTelemetry();

            string tip = string.Format("MacBook Air: CPU {0:0}°C | Fan {1:0} RPM | Cap {2}%",
                _lastCpuTemp, _lastFanRpm, _cfg.BatteryCapEnabled ? _cfg.BatteryCapPct : 100);
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            _trayIcon.Text = tip;

            WriteLiveStatusSnapshot(cpuTemp, proxTemp, boardTemp, battTemp, palmTemp, fanRpm);
        }

        private void WriteLiveStatusSnapshot(double? cpuT, double? proxT, double? boardT, double? battT, double? palmT, double? fanRpm)
        {
            try
            {
                string showFlag = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "show_window.flag");
                if (File.Exists(showFlag))
                {
                    try { File.Delete(showFlag); } catch { }
                    RestoreFromTray();
                }

                string statusPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "live_status.txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Timestamp=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("UiTheme=Dark_Gotham_Gold_Afterburner");
                sb.AppendLine("WindowVisible=" + (IsVisible && WindowState != WindowState.Minimized));
                sb.AppendLine("SmcConnected=" + _smc.IsConnected);
                sb.AppendLine("SmcStatus=" + _smc.StatusMessage);
                sb.AppendLine("CpuTemp_TCXC=" + (cpuT.HasValue ? cpuT.Value.ToString("0.0") : "null"));
                sb.AppendLine("CpuTemp_TC0E=" + (_smc.ReadNumber("TC0E") ?? -1).ToString("0.0"));
                sb.AppendLine("CpuTemp_TC0F=" + (_smc.ReadNumber("TC0F") ?? -1).ToString("0.0"));
                sb.AppendLine("CpuTemp_TC0C=" + (_smc.ReadNumber("TC0C") ?? -1).ToString("0.0"));
                sb.AppendLine("CpuTemp_TC1C=" + (_smc.ReadNumber("TC1C") ?? -1).ToString("0.0"));
                sb.AppendLine("CpuTemp_TC2C=" + (_smc.ReadNumber("TC2C") ?? -1).ToString("0.0"));
                sb.AppendLine("CpuTemp_TC3C=" + (_smc.ReadNumber("TC3C") ?? -1).ToString("0.0"));
                sb.AppendLine("CpuTemp_TC4C=" + (_smc.ReadNumber("TC4C") ?? -1).ToString("0.0"));
                sb.AppendLine("CpuTemp_TCGC=" + (_smc.ReadNumber("TCGC") ?? -1).ToString("0.0"));
                sb.AppendLine("CpuTemp_TCSA=" + (_smc.ReadNumber("TCSA") ?? -1).ToString("0.0"));
                sb.AppendLine("ProxTemp_TC0P=" + (proxT.HasValue ? proxT.Value.ToString("0.0") : "null"));
                sb.AppendLine("BoardTemp_TW0P=" + (boardT.HasValue ? boardT.Value.ToString("0.0") : "null"));
                sb.AppendLine("BattTemp_TB0T=" + (battT.HasValue ? battT.Value.ToString("0.0") : "null"));
                sb.AppendLine("PalmTemp_Ts0P=" + (palmT.HasValue ? palmT.Value.ToString("0.0") : "null"));
                sb.AppendLine("FanActual_F0Ac=" + (fanRpm.HasValue ? fanRpm.Value.ToString("0") : "null"));
                sb.AppendLine("FanTarget_F0Tg=" + (_smc.ReadNumber("F0Tg") ?? -1).ToString("0"));
                sb.AppendLine("FanMode_F0Md=" + (_smc.ReadNumber("F0Md") ?? -1).ToString("0"));
                sb.AppendLine("FanMin_F0Mn=" + _fanMinRpm);
                sb.AppendLine("FanMax_F0Mx=" + _fanMaxRpm);
                sb.AppendLine("AppFanMode=" + _cfg.FanMode);
                sb.AppendLine("TurboProfile=" + _activeTurboMode);
                sb.AppendLine("CpuClockUI=" + _txtCpuClock.Text);
                sb.AppendLine("CpuLoadUI=" + _txtCpuLoad.Text);
                sb.AppendLine("BattCapEnabled=" + _cfg.BatteryCapEnabled);
                sb.AppendLine("BattCapTarget=" + _cfg.BatteryCapPct);
                sb.AppendLine("SmcBCLM=" + (_smc.ReadNumber("BCLM") ?? -1).ToString("0"));
                sb.AppendLine("SmcBRSC=" + (_smc.ReadNumber("BRSC") ?? -1).ToString("0"));
                sb.AppendLine("SmcCycles_B0CT=" + (_smc.ReadNumber("B0CT") ?? -1).ToString("0"));
                sb.AppendLine("SmcVolt_B0AV=" + (_smc.ReadNumber("B0AV") ?? -1).ToString("0"));
                File.WriteAllText(statusPath, sb.ToString());
            }
            catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (_cfg.SafetyResetOnExit)
            {
                WinForms.PowerStatus ps = WinForms.SystemInformation.PowerStatus;
                int pct = (int)Math.Round(ps.BatteryLifePercent * 100.0);
                if (pct < 25 && _isChargingInhibited)
                {
                    SetChargeInhibit(false);
                }
            }

            _pollTimer.Stop();
            _trayIcon.Visible = false;
            _smc.Dispose();
            base.OnClosing(e);
        }
    }

    public static class Program
    {
        public static void StopConflictingProcesses()
        {
            int currentId = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != currentId &&
                        (p.ProcessName.StartsWith("MacAirControl", StringComparison.OrdinalIgnoreCase) ||
                         p.ProcessName.StartsWith("MacsFanControl", StringComparison.OrdinalIgnoreCase)))
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                }
                catch { }
            }
            try
            {
                foreach (string f in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.prev.exe"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        public static string RunHiddenCmd(string fileName, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(fileName, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(3000);
                    return output;
                }
            }
            catch
            {
                return "";
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            StopConflictingProcesses();
            Thread.Sleep(250);

            bool startMinimized = false;
            if (args != null)
            {
                foreach (string a in args)
                {
                    if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase))
                        startMinimized = true;
                }
            }

            Application app = new Application();
            app.Run(new MainWindow(startMinimized));
        }
    }
}
