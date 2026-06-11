using System;
using HDRGammaController.Core;
using System.Windows;
using System.Windows.Interop;
using HDRGammaController.Interop;
using HDRGammaController.Services;
using HDRGammaController.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace HDRGammaController
{
    public partial class MainWindow : Window
    {
        private HotkeyManager? _hotkeyManager;
        private TrayViewModel? _trayViewModel;

        public MainWindow()
        {
            Log.Info("MainWindow: Constructor started.");
            InitializeComponent();
            
            // Set Icon from executable (requires net8.0-windows)
            try {
                MyNotifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetEntryAssembly()!.Location);
            } catch (Exception ex) { Log.Info("MainWindow: Failed to set icon: " + ex.Message); }

            // Force Handle creation early
            Log.Info("MainWindow: Ensuring Handle...");
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle(); 
            
            // Initialize services with handle
            Log.Info("MainWindow: Initializing HotkeyManager...");
            _hotkeyManager = new HotkeyManager(helper.Handle);
            
            // HotkeyManager needs this window's handle, so it cannot live in the
            // container; ActivatorUtilities resolves the shared singletons from
            // App.Services and passes the HotkeyManager explicitly.
            Log.Info("MainWindow: Initializing TrayViewModel...");
            _trayViewModel = ActivatorUtilities.CreateInstance<TrayViewModel>(App.Services, _hotkeyManager);
            
            // Subscribe to notifications
            _trayViewModel.NotificationRequested += (title, msg) =>
            {
                MyNotifyIcon.ShowBalloonTip(title, msg, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            };

            // Update balloons say "Click here to download"; honor that by opening
            // the release page when the balloon is clicked.
            MyNotifyIcon.TrayBalloonTipClicked += (s, e) => _trayViewModel?.OpenPendingUpdate();
            
            // Set DataContext for the specific bindings in XAML
            MyNotifyIcon.DataContext = _trayViewModel;

            // Hook message loop for system events (Display Change, Power)
            HwndSource.FromHwnd(helper.Handle)!.AddHook(WndProc);
            Log.Info("MainWindow: Constructor finished.");
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
