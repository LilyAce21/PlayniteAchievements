using System;
using System.Runtime.InteropServices;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Finds the window a game's picture is actually presented in, starting from the game window
    /// the tracker resolved. That is usually the same window, but a game can present through a
    /// separate window of its own process stacked above it: Resident Evil 0 (D3D9 fullscreen)
    /// shows every frame through a topmost, layered, full-screen <c>D3DProxyWindow</c> over the
    /// window that holds focus, so anything placed or captured against the focus window misses
    /// the picture.
    ///
    /// Structural rule, no class-name matching: the highest visible, uncloaked top-level window
    /// above the base window in z-order that belongs to the same process and overlaps it.
    /// </summary>
    internal static class GamePresentationWindow
    {
        private const uint GwHwndPrev = 3;
        private const int DwmwaCloaked = 14;

        public static IntPtr Resolve(IntPtr baseHwnd)
        {
            if (baseHwnd == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            try
            {
                GetWindowThreadProcessId(baseHwnd, out var pid);
                if (pid == 0 || !GetWindowRect(baseHwnd, out var baseRect))
                {
                    return baseHwnd;
                }

                var top = baseHwnd;
                for (var hwnd = GetWindow(baseHwnd, GwHwndPrev); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GwHwndPrev))
                {
                    GetWindowThreadProcessId(hwnd, out var ownerPid);
                    if (ownerPid != pid || !IsWindowVisible(hwnd) || IsCloaked(hwnd) ||
                        !GetWindowRect(hwnd, out var rect) || !Overlaps(rect, baseRect))
                    {
                        continue;
                    }

                    top = hwnd;
                }

                return top;
            }
            catch
            {
                return baseHwnd;
            }
        }

        private static bool Overlaps(RECT a, RECT b)
        {
            return a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;
        }

        private static bool IsCloaked(IntPtr hwnd)
        {
            try
            {
                return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
            }
            catch
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    }
}
