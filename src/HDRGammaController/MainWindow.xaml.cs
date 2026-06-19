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
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    MyNotifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
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
            
            // Route themed notifications. The old OS balloon is replaced by the in-app
            // ToastWindow; NotificationRequested is the Core→app boundary so Core code can
            // request a toast without depending on WPF.
            var toast = App.Services.GetService(typeof(IToastService)) as IToastService;
            _trayViewModel.NotificationRequested += (title, msg) =>
            {
                toast?.Show(title, msg, ToastKind.Info);
            };

            // Update notifications carry a "Click here to download" action; route that
            // through the toast's action button instead of relying on a balloon click.
            _trayViewModel.UpdateNotificationRequested += info =>
            {
                toast?.Show("Update available", info.Version, ToastKind.Info, "Download",
                    () => _trayViewModel?.OpenPendingUpdate());
            };
            
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
