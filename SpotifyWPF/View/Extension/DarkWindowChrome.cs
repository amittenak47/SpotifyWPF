using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SpotifyWPF.View.Extension
{
    public static class DarkWindowChrome
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        public static void Apply(Window window)
        {
            if (window == null) return;

            if (window.IsLoaded)
                EnableDarkTitleBar(window);
            else
                window.SourceInitialized += OnSourceInitialized;
        }

        private static void OnSourceInitialized(object sender, EventArgs e)
        {
            if (!(sender is Window window)) return;

            window.SourceInitialized -= OnSourceInitialized;
            EnableDarkTitleBar(window);
        }

        private static void EnableDarkTitleBar(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var useDarkMode = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDarkMode, sizeof(int));
        }
    }
}
