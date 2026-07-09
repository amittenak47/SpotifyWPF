using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SpotifyWPF.View.Extension;

namespace SpotifyWPF.View
{
    public partial class MiniPlayerWindow
    {
        private const int DwmwaNcRenderingPolicy = 2;
        private const int DwmncrpDisabled = 1;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        public MiniPlayerWindow()
        {
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            SourceInitialized -= OnSourceInitialized;
            DisableNonClientRendering();
            DarkWindowChrome.Apply(this);
        }

        private void DisableNonClientRendering()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var policy = DwmncrpDisabled;
            DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref policy, sizeof(int));
        }
    }
}
