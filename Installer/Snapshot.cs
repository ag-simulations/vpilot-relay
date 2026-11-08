#if DEBUG
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VpilotRelaySetup
{
    /// <summary>
    /// Debug builds only: vPilotRelaySetup.exe --snapshot PAGE OUT.png [variant]
    /// renders one wizard page to a PNG and exits (for checking the design without clicking through).
    /// </summary>
    internal static class Snapshot
    {
        public static void TryRun(MainWindow w)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "--snapshot");
            if (i < 0 || args.Length < i + 3) return;

            var page = int.Parse(args[i + 1]);
            var path = args[i + 2];
            var variant = args.Length > i + 3 ? args[i + 3] : null;
            w.PreviewState(page, variant);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var root = (FrameworkElement)w.Content;
                var dpi = VisualTreeHelper.GetDpi(root);
                var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX) + 40, (int)(root.ActualHeight * dpi.DpiScaleY) + 40,
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var f = File.Create(path)) enc.Save(f);
                w.Close();
            };
            timer.Start();
        }
    }
}
#endif
