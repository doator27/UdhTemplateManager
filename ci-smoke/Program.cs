using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HardwareTemplateBuilder.Wpf.Ui;

static class Program
{
    [STAThread]
    static void Main()
    {
        var app = new HardwareTemplateBuilder.Wpf.App();
        app.InitializeComponent();
        Win95Chrome.Register();
        Shot("main", w => { w.ShowInTaskbar = true; w.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/HardwareTemplateBuilder.Wpf;component/Assets/AppIcon.ico")); }, false);
        Shot("dialog", w => { w.ResizeMode = ResizeMode.NoResize; w.Title = "Copy Items To..."; }, false);
        Shot("inactive", w => { w.ResizeMode = ResizeMode.CanResizeWithGrip; w.Title = "Select Description"; }, true);
        Console.WriteLine("SMOKE OK");
    }

    static void Shot(string name, Action<Window> setup, bool deactivate)
    {
        var w = new Win95Window { Title = "Hardware Template Builder", Width = 420, Height = 220, WindowStartupLocation = WindowStartupLocation.Manual, Left = 50, Top = 50 };
        var panel = new DockPanel();
        var menu = new Menu(); DockPanel.SetDock(menu, Dock.Top);
        menu.Items.Add(new MenuItem { Header = "File" }); menu.Items.Add(new MenuItem { Header = "Jobs" });
        panel.Children.Add(menu);
        panel.Children.Add(new StackPanel { Margin = new Thickness(10), Children = { new TextBlock { Text = "Sample content" }, new Button { Content = "OK", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left } } });
        w.Content = panel;
        setup(w);
        w.Show();
        if (deactivate) { var other = new Window { Width = 10, Height = 10, Left = 900, Top = 900 }; other.Show(); other.Activate(); }
        else w.Activate();
        w.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var rtb = new RenderTargetBitmap((int)w.ActualWidth * 2, (int)w.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory("shots");
        using var f = File.Create(Path.Combine("shots", name + ".png")); enc.Save(f);
        Console.WriteLine($"{name}: template root found = {w.Template.FindName("PART_Win95Root", w) != null}, active={w.IsActive}");
        w.Close();
    }
}
