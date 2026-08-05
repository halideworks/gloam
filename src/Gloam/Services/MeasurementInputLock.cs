using System;
using System.Runtime.InteropServices;
using Gloam.Core;

namespace Gloam.Services
{
    /// <summary>
    /// Blocks the shell shortcuts that draw over a measurement patch, for as long as the
    /// probe is reading it.
    ///
    /// A patch under measurement has to be immutable: the colorimeter reads the physical
    /// pixels, so anything that paints over it is measured AS the patch. Alt+Tab is the
    /// worst offender because the task switcher renders above ordinary topmost windows —
    /// re-asserting topmost after the fact cannot help, the reading is already poisoned by
    /// the time we hear about it. The only way to keep the patch clean is to stop the
    /// switcher from ever appearing.
    ///
    /// This installs a low-level keyboard hook and swallows Alt+Tab, Alt+Shift+Tab,
    /// Alt+Esc, Ctrl+Esc and the Windows keys while engaged. Keys the calibration UI needs
    /// pass through untouched: Escape still pauses and cancels, Space still resumes, and
    /// the arrow keys still nudge the patch.
    ///
    /// LIMITS, stated plainly: a user-mode hook cannot block Ctrl+Alt+Del (the secure
    /// attention sequence is handled below any hook), and it cannot stop another
    /// application from raising its own topmost window. This closes the interruption a
    /// person actually triggers by hand; it is not a guarantee that nothing can ever paint
    /// over the patch.
    /// </summary>
    internal sealed class MeasurementInputLock : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int HC_ACTION = 0;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const int VK_TAB = 0x09;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_CONTROL = 0x11;

        private const uint LLKHF_ALTDOWN = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private IntPtr _hook = IntPtr.Zero;

        // The delegate MUST be rooted for as long as the hook is installed. If it is
        // collected, Windows calls into freed memory and the process dies.
        private readonly HookProc _proc;

        private int _swallowed;

        internal MeasurementInputLock() => _proc = OnKey;

        internal bool IsEngaged => _hook != IntPtr.Zero;

        /// <summary>Number of shell shortcuts suppressed while engaged (diagnostics).</summary>
        internal int Swallowed => _swallowed;

        /// <summary>
        /// Installs the hook. Must be called on the UI thread (a low-level keyboard hook
        /// requires the installing thread to pump messages). Safe to call when engaged.
        /// </summary>
        internal void Engage()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero)
            {
                Log.Info($"MeasurementInputLock: could not install the keyboard hook (Win32 {Marshal.GetLastWin32Error()}); " +
                         "Alt+Tab can still interrupt this measurement.");
                return;
            }
            Log.Info("MeasurementInputLock: engaged; shell shortcuts are blocked for the measurement.");
        }

        /// <summary>Removes the hook. Safe to call when not engaged, and idempotent.</summary>
        internal void Release()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Log.Info($"MeasurementInputLock: released after suppressing {_swallowed} shell shortcut(s).");
        }

        private IntPtr OnKey(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // Never throw out of a hook callback, and do as little as possible: this runs
            // on every keystroke system-wide.
            try
            {
                if (nCode == HC_ACTION && ShouldSwallow(wParam, lParam))
                {
                    _swallowed++;
                    return new IntPtr(1); // handled: the shell never sees it
                }
            }
            catch
            {
                // A faulting hook would wedge input for the whole desktop. Fall through.
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool ShouldSwallow(IntPtr wParam, IntPtr lParam)
        {
            int message = wParam.ToInt32();
            if (message != WM_KEYDOWN && message != WM_SYSKEYDOWN) return false;

            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool alt = (info.flags & LLKHF_ALTDOWN) != 0;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

            return info.vkCode switch
            {
                // Alt+Tab / Alt+Shift+Tab: the task switcher, the reported case.
                VK_TAB => alt,
                // Alt+Esc cycles windows; Ctrl+Esc opens Start. Bare Escape is left alone —
                // the calibration UI uses it to pause and cancel.
                VK_ESCAPE => alt || ctrl,
                // Start menu / Win+D / Win+anything.
                VK_LWIN or VK_RWIN => true,
                _ => false,
            };
        }

        public void Dispose() => Release();
    }
}
