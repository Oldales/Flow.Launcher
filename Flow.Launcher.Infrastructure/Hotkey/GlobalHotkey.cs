using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Flow.Launcher.Plugin;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Flow.Launcher.Infrastructure.Hotkey
{
    /// <summary>
    /// Listens keyboard globally.
    /// <remarks>Uses WH_KEYBOARD_LL.</remarks>
    /// </summary>
    public unsafe class GlobalHotkey : IDisposable
    {
        private static readonly HOOKPROC _procKeyboard = HookKeyboardCallback;
        private static UnhookWindowsHookExSafeHandle hookId;

        public delegate bool KeyboardCallback(KeyEvent keyEvent, int vkCode, SpecialKeyState state);
        internal static Func<KeyEvent, int, SpecialKeyState, bool> hookedKeyboardCallback;

        static GlobalHotkey()
        {
            // A low-level hook is called on the thread that installed it, for every key pressed in any app.
            // Installing it on its own thread with a message loop keeps those keystrokes from waiting
            // whenever the UI thread is busy.
            // The thread must only use locals: touching this class's statics from another thread while the
            // static constructor is still running would wait for it to finish, and it is waiting for the thread.
            var proc = _procKeyboard;
            UnhookWindowsHookExSafeHandle installedHook = null;
            using var hookInstalled = new ManualResetEventSlim();
            var hookThread = new Thread(() =>
            {
                installedHook = HookInstaller.Install(proc, WINDOWS_HOOK_ID.WH_KEYBOARD_LL);
                hookInstalled.Set();

                while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0).Value > 0)
                {
                    PInvoke.TranslateMessage(msg);
                    PInvoke.DispatchMessage(msg);
                }
            })
            {
                IsBackground = true,
                Name = "Flow Launcher keyboard hook",
                Priority = ThreadPriority.AboveNormal
            };
            hookThread.Start();
            hookInstalled.Wait();
            hookId = installedHook;
        }

        public static SpecialKeyState CheckModifiers()
        {
            SpecialKeyState state = new SpecialKeyState();
            if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_SHIFT) & 0x8000) != 0)
            {
                //SHIFT is pressed
                state.ShiftPressed = true;
            }
            if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0)
            {
                //CONTROL is pressed
                state.CtrlPressed = true;
            }
            if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_MENU) & 0x8000) != 0)
            {
                //ALT is pressed
                state.AltPressed = true;
            }
            if ((PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_LWIN) & 0x8000) != 0 ||
                (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_RWIN) & 0x8000) != 0)
            {
                //WIN is pressed
                state.WinPressed = true;
            }

            return state;
        }

        private static LRESULT HookKeyboardCallback(int nCode, WPARAM wParam, LPARAM lParam)
        {
            bool continues = true;

            if (nCode >= 0)
            {
                if (wParam.Value == (int)KeyEvent.WM_KEYDOWN ||
                    wParam.Value == (int)KeyEvent.WM_KEYUP ||
                    wParam.Value == (int)KeyEvent.WM_SYSKEYDOWN ||
                    wParam.Value == (int)KeyEvent.WM_SYSKEYUP)
                {
                    if (hookedKeyboardCallback != null)
                        continues = hookedKeyboardCallback((KeyEvent)wParam.Value, Marshal.ReadInt32(lParam), CheckModifiers());
                }
            }

            if (continues)
            {
                return PInvoke.CallNextHookEx(hookId, nCode, wParam, lParam);
            }

            return new LRESULT(1);
        }

        public void Dispose()
        {
            hookId.Dispose();
        }

        ~GlobalHotkey()
        {
            Dispose();
        }

        /// <summary>
        /// Installs a hook for the calling thread. Kept outside <see cref="GlobalHotkey"/> so the hook thread
        /// can call it while that class's static constructor is still waiting for the hook.
        /// </summary>
        private static class HookInstaller
        {
            public static UnhookWindowsHookExSafeHandle Install(HOOKPROC proc, WINDOWS_HOOK_ID hookType)
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                return PInvoke.SetWindowsHookEx(hookType, proc, PInvoke.GetModuleHandle(curModule.ModuleName), 0);
            }
        }
    }
}
