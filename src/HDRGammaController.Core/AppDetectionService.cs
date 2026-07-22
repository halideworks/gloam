using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HDRGammaController.Interop;

namespace HDRGammaController.Core
{
    public class AppDetectionService : IDisposable
    {
        private IntPtr _hookHandle = IntPtr.Zero;
        private User32.WinEventDelegate? _winEventDelegate; // Keep ref to prevent GC
        private bool _isDisposed;

        public event Action<string, string?, Dxgi.RECT?>? ForegroundAppChanged;

        public void Start()
        {
            if (_hookHandle != IntPtr.Zero) return;

            _winEventDelegate = new User32.WinEventDelegate(WinEventProc);
            _hookHandle = User32.SetWinEventHook(
                User32.EVENT_SYSTEM_FOREGROUND, 
                User32.EVENT_SYSTEM_FOREGROUND, 
                IntPtr.Zero, 
                _winEventDelegate, 
                0, 
                0, 
                User32.WINEVENT_OUTOFCONTEXT);
                
            // Check initial state
            CheckForeground();
        }

        public void Stop()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                User32.UnhookWinEvent(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            _winEventDelegate = null;
        }

        /// <summary>Re-evaluates the current foreground window after profile/settings edits.</summary>
        public void Refresh() => CheckForeground();

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (eventType == User32.EVENT_SYSTEM_FOREGROUND)
            {
                CheckForeground(hwnd);
            }
        }
        
        private void CheckForeground(IntPtr? hwndOverride = null)
        {
            try
            {
                IntPtr hwnd = hwndOverride ?? User32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;

                // A zero thread ID means the Win32 lookup failed; do not reuse an undefined
                // process ID or report a stale foreground application.
                uint threadId = User32.GetWindowThreadProcessId(hwnd, out uint pid);
                if (threadId == 0 || pid == 0) return;

                string processName = "";
                string? executablePath = null;
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    processName = p.ProcessName.ToLowerInvariant() + ".exe";
                    try { executablePath = p.MainModule?.FileName; }
                    catch { executablePath = null; }
                }
                catch 
                {
                    // Access denied or process exited
                    return;
                }

                // Identify window bounds
                Dxgi.RECT rect = new Dxgi.RECT();
                bool hasRect = User32.GetWindowRect(hwnd, out rect);

                ForegroundAppChanged?.Invoke(processName, executablePath, hasRect ? rect : (Dxgi.RECT?)null);
            }
            catch (Exception ex)
            {
                // Log but don't throw - this is a background monitoring operation
                System.Diagnostics.Debug.WriteLine($"AppDetectionService.CheckForeground: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                Stop();
                _isDisposed = true;
            }
        }
        
        ~AppDetectionService()
        {
            Dispose(false);
        }
    }
}
