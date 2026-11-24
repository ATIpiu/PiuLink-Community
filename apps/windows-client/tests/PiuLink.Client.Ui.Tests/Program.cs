using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PiuLink.Client;
using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;
using PiuLink.Client.Core.Storage;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.Exit(1); }
    }
    private static void Run(string[] args)
    {
        var app = new App(); app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string directory = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "piulink-ui-" + Guid.NewGuid()));
        Directory.CreateDirectory(directory);
        var repository = new JsonHostRepository(Path.Combine(directory, "hosts.json"));
        var window = new MainWindow(repository);
        var type = typeof(MainWindow);
        void Set(string field, object? value) => type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        object? Invoke(string name, params object?[] values) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, values);
        T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
        void Pump(Task task) {
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < limit) {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(1);
            }
            if (!task.IsCompleted) throw new TimeoutException("UI save did not finish");
            task.GetAwaiter().GetResult();
        }
        void Save() => Pump((Task)Invoke("SaveDraftsAsync")!);
        var first = new HostProfile(Guid.NewGuid(), "测试电脑：长名称仍应完整保留", "127.0.0.1", DateTimeOffset.UtcNow);
        var second = new HostProfile(Guid.NewGuid(), "另一台电脑", "127.0.0.2", DateTimeOffset.UtcNow);
        window.Hosts.Add(first); window.Hosts.Add(second);
        Control<ListBox>("HostsList").ItemsSource = window.Hosts;
        Set("_hostsLoaded", true);
        void Select(HostProfile host, HostDisplayInventory inventory, string? id) {
            // No Loaded event or network probe; exercise the actual controls and persistence.
            Set("_closed", true);
            Control<ListBox>("HostsList").SelectedItem = host;
            Invoke("UpdateSelectionState");
            Set("_displayInventory", inventory);
            Invoke("RenderDisplays", id);
            Set("_closed", false);
        }
        HostDisplayInventory Inventory(string id, int fps, int width) => new(false,
            [new(0, id, "虚拟屏幕", false, true, width, 1080, true, 100, [100, 125, 150], [new(width,1080,fps)]),
             new(1, "physical", "主显示器", true, false, 1920, 1080, false, 150, [100, 125, 150, 175])],
            [new(width, 1080)], [fps], true, true);

        Select(first, Inventory("virtual-a", 144, 3440), "");
        Check(Control<ComboBox>("DisplayScaleCombo").IsEnabled &&
              Control<ComboBox>("DisplayScaleCombo").Items.OfType<ComboBoxItem>()
                  .Select(item => item.Tag?.ToString()).SequenceEqual(["0", "100", "125", "150", "175"]),
            "screen settings expose Host-supported Windows scale percentages");
        Control<ComboBox>("BitrateCombo").SelectedIndex = 2;
        Console.WriteLine("Save feedback: " + Control<TextBlock>("SettingsSaveStatus").Text);
        Save();
        var saved = repository.LoadAsync().GetAwaiter().GetResult().Single(host => host.Id == first.Id);
        Check(saved.StreamSettings!.BitrateKbps == 40000, "idle parameter change persists without opening an application");
        Check(saved.DisplayId == "", "automatic display strategy remains automatic in saved profile");
        Select(second, Inventory("virtual-b", 60, 1920), "virtual-b");
        var fpsTags = Control<ComboBox>("FpsCombo").Items.OfType<ComboBoxItem>().Select(item => item.Tag?.ToString()).ToArray();
        Check(fpsTags.SequenceEqual(new[] { "60" }), "host B virtual modes exclude host A options");
        Select(first, Inventory("virtual-a", 144, 3440), "");
        Check(((StreamProfile)Invoke("ReadProfile")!).BitrateKbps == 40000, "switching hosts retains saved edits");
        Invoke("RenderDisplays", "removed-guid");
        Check(Control<TextBlock>("ScreenStatusText").Text.Contains("已断开"), "missing monitor is explicitly marked unavailable");
        Check(((ComboBoxItem)Control<ComboBox>("HostDisplayCombo").SelectedItem).Tag?.ToString() == "removed-guid", "missing explicit target cannot silently become automatic");
        Control<ComboBox>("FpsCombo").SelectedIndex = 3;
        Save();
        var records = repository.LoadAsync().GetAwaiter().GetResult();
        Check(records.Single(host => host.Id == first.Id).StreamSettings!.FramesPerSecond == 120 &&
            records.Single(host => host.Id == second.Id).StreamSettings is null, "saving another field does not overwrite another host profile");
        Invoke("UpdateStreamAction");
        Check(Control<Button>("ApplySettingsButton").Visibility == Visibility.Collapsed,
            "idle settings rely on autosave instead of presenting a redundant save action");

        Select(first, Inventory("virtual-a", 144, 3440), "virtual-a");
        Invoke("CaptureSettingsDraft", first); Save();
        Set("_displayInventory", Inventory("virtual-a", 60, 1920));
        Invoke("RenderDisplays", "virtual-a");
        Check(((StreamProfile)Invoke("ReadProfile")!).FramesPerSecond == 60, "changed host capabilities update the visible frame rate");
        Invoke("ApplyAndReconnect_Click", Control<Button>("ApplySettingsButton"), new RoutedEventArgs());
        Save();
        var adjusted = repository.LoadAsync().GetAwaiter().GetResult().Single(host => host.Id == first.Id);
        Check(adjusted.StreamSettings!.FramesPerSecond == 60 && adjusted.StreamSettings.Resolution == "1920x1080",
            "explicit save persists the visible fallback instead of an obsolete draft");
        Invoke("RenderDisplays", "removed-again");
        Invoke("ApplyAndReconnect_Click", Control<Button>("ApplySettingsButton"), new RoutedEventArgs());
        Save();
        Check(repository.LoadAsync().GetAwaiter().GetResult().Single(host => host.Id == first.Id).DisplayId == "removed-again",
            "saving another preference retains the missing explicit target until the user reselects");

        Set("_displayInventory", null);
        Invoke("UpdateSelectionState");
        Invoke("BeginDisplayInventoryLoad");
        Check(!Control<ComboBox>("HostDisplayCombo").IsEnabled &&
              !Control<CheckBox>("GatherWindowsCheckBox").IsEnabled &&
              Control<TextBlock>("DisplayCapabilityHint").Text.Contains("正在读取"),
            "display controls remain unavailable while another Host capability set is loading");

        Set("_displayInventory", new HostDisplayInventory(false,
            [new(1, "physical", "主显示器", true, false, 1920, 1080, false)],
            [], [], false));
        Invoke("RenderDisplays", "physical");
        Check(!Control<CheckBox>("GatherWindowsCheckBox").IsEnabled &&
              Control<TextBlock>("DisplayCapabilityHint").Text.Contains("尚不支持"),
            "unsupported Host capability disables window gathering and explains why");

        Set("_displayInventory", new HostDisplayInventory(false,
            [new(0, "virtual-only", "虚拟屏幕", false, true, null, null, true, Modes: [new(2560,1440,120)])],
            [new(2560, 1440)], [120], true));
        Invoke("RenderDisplays", "");
        Check(Control<ComboBox>("ResolutionCombo").Items.OfType<ComboBoxItem>().Select(item => item.Tag?.ToString()).SequenceEqual(["2560x1440"]) &&
              Control<ComboBox>("FpsCombo").Items.OfType<ComboBoxItem>().Select(item => item.Tag?.ToString()).SequenceEqual(["120"]) &&
              !Control<CheckBox>("GatherWindowsCheckBox").IsEnabled &&
              Control<TextBlock>("ScreenStatusText").Text.Contains("虚拟屏幕"),
            "automatic display uses the Host virtual screen and its supported modes when no physical screen exists");

        var hostFile = Path.Combine(directory, "hosts.json");
        var savedBytes = File.ReadAllBytes(hostFile);
        var damagedBytes = System.Text.Encoding.UTF8.GetBytes("[incomplete");
        File.WriteAllBytes(hostFile, damagedBytes);
        Set("_hostsLoaded", false);
        try
        {
            Pump((Task)Invoke("MergeDiscoveredHostsAsync", (object)new[] { new DiscoveredHost("New Host", "127.0.0.7") })!);
            throw new Exception("Discovery overwrote a failed host load");
        }
        catch (InvalidOperationException) { }
        Check(File.ReadAllBytes(hostFile).SequenceEqual(damagedBytes) && window.Hosts.Count == 2,
            "discovery preserves the original bytes and host list after a failed load");
        File.WriteAllBytes(hostFile, savedBytes);
        Set("_hostsLoaded", true);

        window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.WindowState = WindowState.Maximized;
        Set("_qtMoonlight", new PiuLink.Client.Core.Streaming.MoonlightQtCommandService(Environment.ProcessPath!));
        Pump((Task)Invoke("StartAppAsync", first with { Address = "https://invalid.invalid" }, "Desktop", 0, false)!);
        Check(window.WindowState == WindowState.Maximized && window.WindowStyle == WindowStyle.SingleBorderWindow,
            "failed stream preparation preserves maximized window and frame");

        // Render actual WPF content at the minimum supported width, without showing a desktop window.
        window.Applications.Add("Desktop"); window.Applications.Add("一个较长的应用名称");
        Control<TextBlock>("EngineStatusText").Text = "串流内核已就绪";
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(964, 710)); content.Arrange(new Rect(0, 0, 964, 710)); content.UpdateLayout();
        Check(Grid.GetRow(Control<Border>("SettingsCard")) == 1, "narrow window stacks settings below apps");
        var more = Control<System.Windows.Controls.Primitives.ToggleButton>("MoreHostActions");
        var extraActions = (WrapPanel)Control<Button>("SshHostButton").Parent;
        Check(extraActions.Visibility == Visibility.Collapsed, "secondary host actions start collapsed");
        more.IsChecked = true;
        Check(extraActions.Visibility == Visibility.Visible, "expanding host actions reveals SSH and device management");
        more.IsChecked = false;
        var primary = new Button { Content = "连接", Style = (Style)app.FindResource("PrimaryButtonStyle") };
        primary.Measure(new Size(120, 48));
        primary.Arrange(new Rect(0, 0, 120, 48));
        static TextBlock? FindLabel(DependencyObject visual)
        {
            if (visual is TextBlock label) return label;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
                if (FindLabel(VisualTreeHelper.GetChild(visual, i)) is { } child) return child;
            return null;
        }
        Check(FindLabel(primary)?.Foreground == app.FindResource("OnAccentBrush"),
            "the rendered primary button label uses the dark foreground on the light button");
        var bitmap = new RenderTargetBitmap(964, 710, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, "client-980.png"))) encoder.Save(file);
        Set("_closed", true); Set("_closingAfterSave", true); window.Close();
        Console.WriteLine("UI evidence: " + directory);
    }
}
