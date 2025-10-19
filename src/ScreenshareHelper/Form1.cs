using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using ScreenshareHelper.Properties;

namespace ScreenshareHelper
{
    public partial class Form1 : Form
    {
        readonly Color transKey = Color.SaddleBrown;
        private bool isActive = true;
        private const int HOT_SIZE = 50; // thickness of the resize hot area in pixels
        private bool isResizing = false;
        private Point resizeStartMouse;
        private Rectangle resizeStartBounds;
        private HitTestResult currentHit = HitTestResult.None;

        public Form1()
        {
            InitializeComponent();
            FormBorderStyle = FormBorderStyle.None;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            this.TransparencyKey = transKey;
            RestoreWindowPosition();
            this.BackColor = Color.Black;

            this.MouseDown += Form1_MouseDown;
            this.MouseMove += Form1_MouseMove_ForResize;
            this.MouseUp += Form1_MouseUp_ForResize;
            this.MouseLeave += Form1_MouseLeave_ForResize;
            this.SizeChanged += Form1_SizeChanged;
        }

        #region Drag/Move the form
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        private void Form1_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var hit = HitTest(e.Location);
                if (hit != HitTestResult.None)
                {
                    isResizing = true;
                    resizeStartMouse = Cursor.Position;
                    resizeStartBounds = this.Bounds;
                    currentHit = hit;
                    return;
                }

                Cursor.Current = Cursors.Cross;
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (isActive)
                    cp.Style |= 0x40000; //WS_SIZEBOX;  
                else
                    cp.Style &= ~0x40000; //WS_SIZEBOX;  
                return cp;
            }
        }
        #endregion
        protected void OnPaintBackground(Graphics g)
        {
            if (isActive)
            {
                g.Clear(this.BackColor);
            }
            else
                paint(g);
        }

        private void Form1_SizeChanged(object sender, EventArgs e)
        {
            UpdateSizeDisplay();
        }

        private void UpdateSizeDisplay()
        {
            try
            {
                var sizeText = $"{this.Width} × {this.Height}";
                if (this.labelSize != null)
                    this.labelSize.Text = sizeText;
            }
            catch (Exception) { }
        }

        #region Cursor
        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO
        {
            public Int32 cbSize;
            public Int32 flags;
            public IntPtr hCursor;
            public POINTAPI ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINTAPI
        {
            public int x;
            public int y;
        }

        [DllImport("user32.dll")]
        static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        const Int32 CURSOR_SHOWING = 0x00000001;
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);
        private const int SRCCOPY = 0x00CC0020;
        #endregion Cursor


        private void paint(Graphics graphics)
        {
            try
            {
                graphics.CopyFromScreen(Settings.Default.CaptureLocation.X, Settings.Default.CaptureLocation.Y, 0, 0, Settings.Default.CaptureSize);
                if (Settings.Default.CopyMouse)
                {
                    CopyMousePointer(graphics);
                }
            }
            catch (Exception)
            { }
        }

        private static void CopyMousePointer(Graphics graphics)
        {
            CURSORINFO pci;
            pci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(CURSORINFO));
            int offsetX = SystemInformation.FrameBorderSize.Width + SystemInformation.BorderSize.Width;
            int offsetY = SystemInformation.FrameBorderSize.Height + SystemInformation.BorderSize.Height;
            if (GetCursorInfo(out pci))
            {
                if (pci.flags == CURSOR_SHOWING)
                {
                    DrawIcon(graphics.GetHdc(),
                        pci.ptScreenPos.x - Settings.Default.CaptureLocation.X - offsetX,
                        pci.ptScreenPos.y - Settings.Default.CaptureLocation.Y - offsetY,
                        pci.hCursor);
                    graphics.ReleaseHdc();
                }
            }
        }

        private enum HitTestResult
        {
            None,
            Top,
            Bottom,
            Left,
            Right,
            TopLeft,
            TopRight,
            BottomLeft,
            BottomRight
        }

        private HitTestResult HitTest(Point clientPt)
        {
            var r = this.ClientRectangle;

            bool left = clientPt.X >= 0 && clientPt.X <= HOT_SIZE;
            bool right = clientPt.X >= r.Width - HOT_SIZE && clientPt.X <= r.Width;
            bool top = clientPt.Y >= 0 && clientPt.Y <= HOT_SIZE;
            bool bottom = clientPt.Y >= r.Height - HOT_SIZE && clientPt.Y <= r.Height;

            if (top && left) return HitTestResult.TopLeft;
            if (top && right) return HitTestResult.TopRight;
            if (bottom && left) return HitTestResult.BottomLeft;
            if (bottom && right) return HitTestResult.BottomRight;
            if (left) return HitTestResult.Left;
            if (right) return HitTestResult.Right;
            if (top) return HitTestResult.Top;
            if (bottom) return HitTestResult.Bottom;

            return HitTestResult.None;
        }

        private void UpdateCursorForHit(HitTestResult hit)
        {
            switch (hit)
            {
                case HitTestResult.Top:
                case HitTestResult.Bottom:
                    Cursor = Cursors.SizeNS;
                    break;
                case HitTestResult.Left:
                case HitTestResult.Right:
                    Cursor = Cursors.SizeWE;
                    break;
                case HitTestResult.TopLeft:
                case HitTestResult.BottomRight:
                    Cursor = Cursors.SizeNWSE;
                    break;
                case HitTestResult.TopRight:
                case HitTestResult.BottomLeft:
                    Cursor = Cursors.SizeNESW;
                    break;
                default:
                    Cursor = Cursors.Cross; // keep the existing cross for move
                    break;
            }
        }

        private void Form1_MouseMove_ForResize(object sender, MouseEventArgs e)
        {
            if (isResizing)
            {
                PerformResize(e.Location);
                return;
            }

            var hit = HitTest(e.Location);
            if (hit != currentHit)
            {
                currentHit = hit;
                UpdateCursorForHit(currentHit);
            }
        }

        private void Form1_MouseDown_ForResize(object sender, MouseEventArgs e)
        {
            // not wired directly - reuse existing Form1_MouseDown for move; if hit area present start resizing
            if (e.Button == MouseButtons.Left)
            {
                var hit = HitTest(e.Location);
                if (hit != HitTestResult.None)
                {
                    isResizing = true;
                    resizeStartMouse = Cursor.Position;
                    resizeStartBounds = this.Bounds;
                    currentHit = hit;
                }
            }
        }

        private void Form1_MouseUp_ForResize(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && isResizing)
            {
                isResizing = false;
                currentHit = HitTest(e.Location);
                UpdateCursorForHit(currentHit);
            }
        }

        private void Form1_MouseLeave_ForResize(object sender, EventArgs e)
        {
            if (!isResizing)
            {
                currentHit = HitTestResult.None;
                Cursor = Cursors.Cross;
            }
        }

        private void PerformResize(Point clientMouse)
        {
            var screenMouse = Cursor.Position;
            int dx = screenMouse.X - resizeStartMouse.X;
            int dy = screenMouse.Y - resizeStartMouse.Y;

            var b = resizeStartBounds;
            var newBounds = b;

            switch (currentHit)
            {
                case HitTestResult.Top:
                    newBounds.Y = b.Y + dy;
                    newBounds.Height = b.Height - dy;
                    break;
                case HitTestResult.Bottom:
                    newBounds.Height = Math.Max(1, b.Height + dy);
                    break;
                case HitTestResult.Left:
                    newBounds.X = b.X + dx;
                    newBounds.Width = b.Width - dx;
                    break;
                case HitTestResult.Right:
                    newBounds.Width = Math.Max(1, b.Width + dx);
                    break;
                case HitTestResult.TopLeft:
                    newBounds.X = b.X + dx;
                    newBounds.Width = b.Width - dx;
                    newBounds.Y = b.Y + dy;
                    newBounds.Height = b.Height - dy;
                    break;
                case HitTestResult.TopRight:
                    newBounds.Y = b.Y + dy;
                    newBounds.Height = b.Height - dy;
                    newBounds.Width = Math.Max(1, b.Width + dx);
                    break;
                case HitTestResult.BottomLeft:
                    newBounds.X = b.X + dx;
                    newBounds.Width = b.Width - dx;
                    newBounds.Height = Math.Max(1, b.Height + dy);
                    break;
                case HitTestResult.BottomRight:
                    newBounds.Width = Math.Max(1, b.Width + dx);
                    newBounds.Height = Math.Max(1, b.Height + dy);
                    break;
            }

            const int minW = 50, minH = 30;
            if (newBounds.Width < minW) newBounds.Width = minW;
            if (newBounds.Height < minH) newBounds.Height = minH;

            this.Bounds = newBounds;
            Settings.Default.CaptureSize = this.Size;
            this.Invalidate();
        }



        #region Window Events
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        private void buttonSetCaptureArea_Click(object sender, EventArgs e)
        {
            Settings.Default.CaptureLocation = this.Location;
            Settings.Default.CaptureSize = this.Size;

            setWindowToBackground();
        }

        private void setWindowToBackground()
        {
            SetWindowPos(this.Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }

        private void RestoreWindowPosition()
        {
            if (Settings.Default.HasSetDefaults)
            {
                this.Location = Settings.Default.Location;
                this.Size = Settings.Default.Size;
            }
        }
        private void SaveWindowPosition()
        {
            if (this.WindowState == FormWindowState.Normal)
            {
                Settings.Default.Location = this.Location;
                Settings.Default.Size = this.Size;
            }
            else
            {
                Settings.Default.Location = this.RestoreBounds.Location;
                Settings.Default.Size = this.RestoreBounds.Size;
            }

            Settings.Default.HasSetDefaults = true;
            Settings.Default.Save();
        }
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        

        private void Form1_Activated(object sender, EventArgs e)
        {
            isActive = true;
            FormBorderStyle = FormBorderStyle.None;//update CreateParams
            buttonSetCaptureArea.Visible = buttonCloseApp.Visible = labelSize.Visible = isActive;
        }
        private void Form1_Deactivate(object sender, EventArgs e)
        {
            isActive = false;
            FormBorderStyle = FormBorderStyle.None; //update CreateParams
            this.Size = Settings.Default.CaptureSize;

            buttonSetCaptureArea.Visible = buttonCloseApp.Visible = labelSize.Visible = isActive;
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveWindowPosition();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            this.BeginInvoke(new Action(() =>
            {
                IntPtr currentWindow = GetForegroundWindow(); // Get the current active window
                SetForegroundWindow(currentWindow); // Re-focus it, removing focus from our window
            }));

            var h = this.Handle;
            Thread t = new Thread(() =>
            {
                while (true)
                {
                    this.OnPaintBackground(Graphics.FromHwnd(h));
                    Thread.Sleep(100);
                }
            });
            t.IsBackground = true;
            t.Start();
            UpdateSizeDisplay();
            
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            isActive = true;
            setWindowToBackground();
        }

        private void buttonCloseApp_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        #endregion
    }
}
