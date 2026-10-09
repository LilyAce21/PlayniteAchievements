using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Raises Playnite's GPU scheduling priority to realtime while a notification is on screen, and
    /// restores the previous class afterwards.
    ///
    /// A game that keeps the GPU busy queues its frames ahead of WPF's, and the card then reached the
    /// screen 3 to 11 times per second at any card size (tools/capture-harness GameLoad under
    /// TemplateBench). At the realtime class the card returned to about the display's composition
    /// rate; the high class changed nothing. The class covers the whole process, so it is held only
    /// for the wave. It needs no elevation.
    ///
    /// All members are wrapped so they can never throw into the toast pipeline.
    /// </summary>
    internal static class ToastGpuPriority
    {
        private const int Realtime = 5;

        private static int _previous = -1;

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr process, out int priority);

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

        /// <summary>Raises the process to realtime, remembering the class to restore. Idempotent.</summary>
        public static void Raise()
        {
            if (_previous >= 0)
            {
                return;
            }

            try
            {
                var process = Process.GetCurrentProcess().Handle;
                if (D3DKMTGetProcessSchedulingPriorityClass(process, out var current) != 0 || current >= Realtime)
                {
                    return;
                }

                if (D3DKMTSetProcessSchedulingPriorityClass(process, Realtime) == 0)
                {
                    _previous = current;
                }
            }
            catch
            {
            }
        }

        /// <summary>Restores the class from before <see cref="Raise"/>, if it raised it.</summary>
        public static void Restore()
        {
            if (_previous < 0)
            {
                return;
            }

            try
            {
                D3DKMTSetProcessSchedulingPriorityClass(Process.GetCurrentProcess().Handle, _previous);
            }
            catch
            {
            }

            _previous = -1;
        }
    }
}
