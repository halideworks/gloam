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
        private bool _cleanedUp;

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
            // Update notifications are raised directly by TrayViewModel through the toast
            // service (silent background download + a "Restart now" action), so there is no
            // separate update event to route here anymore.

            // Set DataContext for the specific bindings in XAML
            MyNotifyIcon.DataContext = _trayViewModel;

            // This window is never shown (it only hosts the tray icon + message loop), and
            // the app uses OnExplicitShutdown, so OnClosed is not a reliable teardown hook.
            // Application.Exit fires on Application.Shutdown() (the tray "Exit" command), so
            // hook it to run the idempotent Cleanup - this is what makes service teardown and
            // the pending-update apply-on-exit actually run.
            if (Application.Current != null)
                Application.Current.Exit += (_, _) => Cleanup();

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
            Cleanup();
            base.OnClosed(e);
        }

        /// <summary>
        /// Idempotent teardown: disposes the tray view model (which stops the night-mode and
        /// ramp-guard timers, unhooks the foreground-window hook, and applies any pending
        /// update on the way out) and the hotkey manager. Called from both OnClosed and the
        /// Application.Exit handler; the guard makes a double-call a no-op.
        /// </summary>
        private void Cleanup()
        {
            if (_cleanedUp) return;
            _cleanedUp = true;
            _trayViewModel?.Dispose();
            _hotkeyManager?.Dispose();
        }
    }
}
