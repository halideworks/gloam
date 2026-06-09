using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using HDRGammaController.Interop;

namespace HDRGammaController.Services
{
    public class HotkeyManager : IDisposable
    {
        private IntPtr _hwnd;
        private HwndSource? _source;
        private int _currentId;
        private readonly List<int> _registeredIds = new();

        public event Action<int>? HotkeyPressed;

        public HotkeyManager(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
        }

        public int Register(Key key, ModifierKeys modifiers)
        {
            _currentId++;
            int id = _currentId;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            uint fsModifiers = 0;
            
            // Win32 Modifiers
            if ((modifiers & ModifierKeys.Alt) != 0) fsModifiers |= 0x0001;
            if ((modifiers & ModifierKeys.Control) != 0) fsModifiers |= 0x0002;
            if ((modifiers & ModifierKeys.Shift) != 0) fsModifiers |= 0x0004;
            if ((modifiers & ModifierKeys.Windows) != 0) fsModifiers |= 0x0008;

            if (!User32.RegisterHotKey(_hwnd, id, fsModifiers, vk))
            {
                // Failed
                return 0;
            }
            _registeredIds.Add(id);
            return id;
        }

        public void Unregister(int id)
        {
            User32.UnregisterHotKey(_hwnd, id);
            _registeredIds.Remove(id);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                HotkeyPressed?.Invoke(id);
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var id in _registeredIds)
            {
                User32.UnregisterHotKey(_hwnd, id);
            }
            _registeredIds.Clear();
            _source?.RemoveHook(WndProc);
        }
    }
}
