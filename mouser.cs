// Mouser: пока пользователь неактивен дольше заданного времени, водит курсор кругами.
// Сборка: build.bat (нужен только компилятор C#, встроенный в Windows).

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Mouser")]
[assembly: AssemblyProduct("Mouser")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace Mouser
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool isFirst;
            using (new Mutex(true, @"Local\Mouser.SingleInstance", out isFirst))
            {
                if (!isFirst)
                {
                    MessageBox.Show("Mouser уже запущен — значок в трее, возле часов.", "Mouser",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new TrayApp())
                    Application.Run(app);
            }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        enum State { Waiting, Moving, Paused }

        const int WaitingTickMs = 500;
        const int MovingTickMs = 10; // реально около 15 мс — точность таймера Windows

        static readonly int[] IdleChoices = { 1, 3, 5, 10 }; // минуты

        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly UserInputWatcher watcher = new UserInputWatcher();
        readonly CircleMover mover = new CircleMover();

        readonly Icon waitingIcon = DrawIcon(Color.FromArgb(46, 160, 67));
        readonly Icon movingIcon = DrawIcon(Color.FromArgb(240, 136, 0));
        readonly Icon pausedIcon = DrawIcon(Color.FromArgb(140, 140, 140));

        readonly ToolStripMenuItem pauseItem = new ToolStripMenuItem("Пауза");
        readonly ToolStripMenuItem intervalItem = new ToolStripMenuItem("Ждать неактивности");

        State state;
        int idleMinutes = 3; // выбор в меню действует до выхода, нигде не сохраняется

        public TrayApp()
        {
            foreach (int minutes in IdleChoices)
            {
                int choice = minutes;
                var item = new ToolStripMenuItem(choice + " мин") { Tag = choice };
                item.Click += delegate { ChooseInterval(choice); };
                intervalItem.DropDownItems.Add(item);
            }

            pauseItem.Click += delegate { SetState(state == State.Paused ? State.Waiting : State.Paused); };

            var menu = new ContextMenuStrip();
            menu.Items.Add(pauseItem);
            menu.Items.Add(intervalItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выход", null, delegate { ExitThread(); });
            tray.ContextMenuStrip = menu;

            watcher.UserInput += delegate { if (state == State.Moving) SetState(State.Waiting); };
            timer.Tick += delegate { OnTick(); };

            SetState(State.Waiting);
            tray.Visible = true;
        }

        void OnTick()
        {
            if (state == State.Waiting)
            {
                bool idleLongEnough = Native.IdleMilliseconds() >= idleMinutes * 60 * 1000;
                if (idleLongEnough && Native.DesktopAcceptsInput())
                    SetState(State.Moving);
            }
            else if (state == State.Moving && !mover.Step())
            {
                // Экран заблокирован или открыт запрос UAC: Windows не принимает ввод.
                SetState(State.Waiting);
            }
        }

        void SetState(State next)
        {
            if (state == State.Moving && next != State.Moving)
            {
                watcher.Stop();
                Native.KeepAwake(false);
            }

            state = next;

            if (state == State.Moving)
            {
                watcher.Start();
                Native.KeepAwake(true);
                mover.Start();
            }

            timer.Interval = state == State.Moving ? MovingTickMs : WaitingTickMs;
            timer.Enabled = state != State.Paused;
            RefreshTray();
        }

        void ChooseInterval(int minutes)
        {
            idleMinutes = minutes;
            RefreshTray();
        }

        void RefreshTray()
        {
            pauseItem.Checked = state == State.Paused;
            foreach (ToolStripMenuItem item in intervalItem.DropDownItems)
                item.Checked = (int)item.Tag == idleMinutes;

            switch (state)
            {
                case State.Moving:
                    tray.Icon = movingIcon;
                    tray.Text = "Mouser: двигаю мышку";
                    break;
                case State.Paused:
                    tray.Icon = pausedIcon;
                    tray.Text = "Mouser: пауза";
                    break;
                default:
                    tray.Icon = waitingIcon;
                    tray.Text = "Mouser: жду неактивности " + idleMinutes + " мин";
                    break;
            }
        }

        static Icon DrawIcon(Color color)
        {
            Size size = SystemInformation.SmallIconSize;
            using (var bitmap = new Bitmap(size.Width, size.Height))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                using (var body = new SolidBrush(color))
                using (var line = new Pen(Color.White, Math.Max(1f, size.Width / 12f)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float w = size.Width, h = size.Height;
                    g.FillEllipse(body, w * 0.14f, 0.5f, w * 0.72f, h - 1.5f);
                    g.DrawLine(line, w / 2f, h * 0.16f, w / 2f, h * 0.42f);
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon borrowed = Icon.FromHandle(handle))
                        return (Icon)borrowed.Clone();
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SetState(State.Paused);
                tray.Visible = false;
                tray.Dispose();
                timer.Dispose();
                watcher.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Ведёт курсор по кругу вокруг точки, где его оставил пользователь.
    sealed class CircleMover
    {
        const double MaxRadius = 200;       // пикселей
        const double SecondsPerTurn = 3;
        const double RampSeconds = 1;       // плавный выход из исходной точки на круг
        const int EdgeMargin = 8;           // не касаться краёв экрана

        Point start;
        double centerX, centerY, radius;
        Stopwatch clock;

        public void Start()
        {
            start = Cursor.Position;

            Rectangle area = Screen.FromPoint(start).Bounds;
            area.Inflate(-EdgeMargin, -EdgeMargin);
            radius = Math.Min(MaxRadius, Math.Min(area.Width, area.Height) / 2.0);

            // У края экрана круг сдвигается внутрь, чтобы не сплющиваться.
            centerX = Clamp(start.X, area.Left + radius, area.Right - radius);
            centerY = Clamp(start.Y, area.Top + radius, area.Bottom - radius);

            clock = Stopwatch.StartNew();
        }

        // false — система не приняла ввод.
        public bool Step()
        {
            double t = clock.Elapsed.TotalSeconds;
            double angle = t / SecondsPerTurn * 2 * Math.PI;
            double ramp = Math.Min(1, t / RampSeconds);
            ramp = ramp * ramp * (3 - 2 * ramp);

            double circleX = centerX + radius * Math.Cos(angle);
            double circleY = centerY + radius * Math.Sin(angle);

            return Native.MoveCursor(
                (int)Math.Round(start.X + (circleX - start.X) * ramp),
                (int)Math.Round(start.Y + (circleY - start.Y) * ramp));
        }

        static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }

    // Сообщает о любом вводе, кроме движений самой программы (они помечены Native.OwnInputTag).
    // Следит только во время движения; что именно нажато, не читает.
    sealed class UserInputWatcher : NativeWindow, IDisposable
    {
        static readonly IntPtr MessageOnlyParent = new IntPtr(-3);

        public event EventHandler UserInput;

        public UserInputWatcher()
        {
            CreateHandle(new CreateParams { Parent = MessageOnlyParent });
        }

        public void Start() { Register(Native.RIDEV_INPUTSINK, Handle); }
        public void Stop() { Register(Native.RIDEV_REMOVE, IntPtr.Zero); }

        public void Dispose() { DestroyHandle(); }

        static void Register(uint flags, IntPtr target)
        {
            var devices = new[]
            {
                Device(0x01, 0x02, flags, target), // мышь и тачпад
                Device(0x01, 0x06, flags, target), // клавиатура
                Device(0x0D, 0x04, flags, target), // сенсорный экран
                Device(0x0D, 0x02, flags, target), // перо
            };
            if (!Native.RegisterRawInputDevices(devices, (uint)devices.Length,
                    (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                throw new Win32Exception();
        }

        static Native.RAWINPUTDEVICE Device(ushort page, ushort usage, uint flags, IntPtr target)
        {
            return new Native.RAWINPUTDEVICE
            {
                usUsagePage = page, usUsage = usage, dwFlags = flags, hwndTarget = target
            };
        }

        protected override void WndProc(ref Message m)
        {
            bool fromUser = m.Msg == Native.WM_INPUT && !IsOwnInput(m.LParam);
            base.WndProc(ref m);
            if (fromUser && UserInput != null)
                UserInput(this, EventArgs.Empty);
        }

        static bool IsOwnInput(IntPtr rawInput)
        {
            uint headerSize = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));

            Native.RAWINPUTHEADER header;
            uint size = headerSize;
            if (Native.GetRawInputHeader(rawInput, Native.RID_HEADER, out header, ref size, headerSize) == uint.MaxValue
                || header.dwType != Native.RIM_TYPEMOUSE)
                return false;

            Native.RAWMOUSEINPUT mouse;
            size = (uint)Marshal.SizeOf(typeof(Native.RAWMOUSEINPUT));
            return Native.GetRawInputMouse(rawInput, Native.RID_INPUT, out mouse, ref size, headerSize) != uint.MaxValue
                && mouse.ulExtraInformation == Native.OwnInputTag;
        }
    }

    static class Native
    {
        public const uint OwnInputTag = 0x4D4F5553; // "MOUS"

        public const int WM_INPUT = 0x00FF;
        public const uint RIDEV_REMOVE = 0x00000001;
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RID_INPUT = 0x10000003;
        public const uint RID_HEADER = 0x10000005;
        public const uint RIM_TYPEMOUSE = 0;

        const uint INPUT_MOUSE = 0;
        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        const uint ES_SYSTEM_REQUIRED = 0x00000001;
        const uint ES_DISPLAY_REQUIRED = 0x00000002;
        const uint ES_CONTINUOUS = 0x80000000;

        const uint DESKTOP_SWITCHDESKTOP = 0x0100;

        public static int IdleMilliseconds()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref info))
                return 0;
            return Math.Max(0, unchecked(Environment.TickCount - (int)info.dwTime));
        }

        // false на экране блокировки и при запросе UAC.
        public static bool DesktopAcceptsInput()
        {
            IntPtr desktop = OpenInputDesktop(0, false, DESKTOP_SWITCHDESKTOP);
            if (desktop == IntPtr.Zero)
                return false;
            CloseDesktop(desktop);
            return true;
        }

        public static bool MoveCursor(int x, int y)
        {
            Rectangle screen = SystemInformation.VirtualScreen;
            var input = new INPUT { type = INPUT_MOUSE };
            input.mi.dx = (int)Math.Ceiling((x - screen.Left) * 65536.0 / screen.Width);
            input.mi.dy = (int)Math.Ceiling((y - screen.Top) * 65536.0 / screen.Height);
            input.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            input.mi.dwExtraInfo = new UIntPtr(OwnInputTag);
            return SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) == 1;
        }

        public static void KeepAwake(bool on)
        {
            SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public MOUSEINPUT mi; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage, usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType, dwSize;
            public IntPtr hDevice, wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWMOUSEINPUT
        {
            public RAWINPUTHEADER header;
            public ushort usFlags;
            public uint ulButtons, ulRawButtons;
            public int lLastX, lLastY;
            public uint ulExtraInformation;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

        [DllImport("user32.dll")]
        static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll", EntryPoint = "GetRawInputData")]
        public static extern uint GetRawInputHeader(IntPtr rawInput, uint command,
            out RAWINPUTHEADER data, ref uint size, uint headerSize);

        [DllImport("user32.dll", EntryPoint = "GetRawInputData")]
        public static extern uint GetRawInputMouse(IntPtr rawInput, uint command,
            out RAWMOUSEINPUT data, ref uint size, uint headerSize);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);
    }
}
