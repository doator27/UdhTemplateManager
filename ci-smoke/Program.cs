using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HardwareTemplateBuilder.Wpf.Helpers;
using HardwareTemplateBuilder.Wpf.Ui;

static class Program
{
    [STAThread]
    static void Main()
    {
        var app = new HardwareTemplateBuilder.Wpf.App();
        app.InitializeComponent();
        Win95Chrome.Register();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            var w = (Window)s;
            w.Dispatcher.BeginInvoke(() =>
            {
                var rtb = new RenderTargetBitmap((int)w.ActualWidth * 2, (int)w.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
                rtb.Render(w);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
                Directory.CreateDirectory("shots");
                using (var f = File.Create(Path.Combine("shots", "error.png"))) enc.Save(f);
                w.Close();
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }));
        DialogHelper.ShowErrorAsync(null, "An unexpected error occurred and has been recorded in CrashReports.txt.\n\nObject reference not set to an instance of an object.", "Hardware Template Builder");
        Console.WriteLine("SMOKE OK");
    }
}
