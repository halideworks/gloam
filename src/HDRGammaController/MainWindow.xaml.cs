using System;
using System.Windows;
using System.Windows.Interop;
using HDRGammaController.Interop;
using HDRGammaController.Services;
using HDRGammaController.ViewModels;

namespace HDRGammaController
{
    public partial class MainWindow : Window
    {
        private HotkeyManager? _hotkeyManager;
        private TrayViewModel? _trayViewModel;

        public MainWindow()
        {
            Console.WriteLine("MainWindow: Constructor started.");
            InitializeComponent();
            
            // Set Icon from executable (requires net8.0-windows)
            try {
                MyNotifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            } catch (Exception ex) { Console.WriteLine("MainWindow: Failed to set icon: " + ex.Message); }

            // Force Handle creation early
            Console.WriteLine("MainWindow: Ensuring Handle...");
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle(); 
            
            // Initialize services with handle
            Console.WriteLine("MainWindow: Initializing HotkeyManager...");
            _hotkeyManager = new HotkeyManager(helper.Handle);
            
            Console.WriteLine("MainWindow: Initializing TrayViewModel...");
            _trayViewModel = new TrayViewModel(_hotkeyManager);
            
            // Subscribe to notifications
            _trayViewModel.NotificationRequested += (title, msg) => 
            {
                MyNotifyIcon.ShowBalloonTip(title, msg, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            };
            
            // Set DataContext for the specific bindings in XAML
            MyNotifyIcon.DataContext = _trayViewModel;

            // Hook message loop for system events (Display Change, Power)
            HwndSource.FromHwnd(helper.Handle)!.AddHook(WndProc);
            Console.WriteLine("MainWindow: Constructor finished.");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_POWERBROADCAST = 0x0218;
            const int PBT_APMRESUMEAUTOMATIC = 0x0012;

            if (msg == WM_DISPLAYCHANGE)
            {
                 // Handle display change
                 _trayViewModel?.HandleDisplayChange();
            }
            else if (msg == WM_POWERBROADCAST)
            {
                if (wParam.ToInt32() == PBT_APMRESUMEAUTOMATIC)
                {
                    _trayViewModel?.HandleResume();
                }
            }

            return IntPtr.Zero;
        }
        
        protected override void OnClosed(EventArgs e)
        {
            _trayViewModel?.Dispose();
            _hotkeyManager?.Dispose();
            base.OnClosed(e);
        }
    }
}
