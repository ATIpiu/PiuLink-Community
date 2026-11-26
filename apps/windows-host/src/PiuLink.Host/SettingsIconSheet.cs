using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PiuLink.Host;

/// <summary>Named cutouts from the ImageGen sheet, with transparent margins around each silhouette.</summary>
internal static class SettingsIconSheet
{
    private static readonly IReadOnlyDictionary<HostSymbol, BitmapSource> Icons = Load();

    private static Dictionary<HostSymbol, BitmapSource> Load()
    {
        var sheet = new BitmapImage();
        sheet.BeginInit();
        sheet.CacheOption = BitmapCacheOption.OnLoad;
        sheet.UriSource = new Uri("pack://application:,,,/PiuLink.Host;component/Assets/LunarSettings.png");
        sheet.EndInit();
        sheet.Freeze();

        var icons = new Dictionary<HostSymbol, BitmapSource>();
        void Add(HostSymbol symbol, int x, int y, int width, int height)
        {
            var cutout = new CroppedBitmap(sheet, new Int32Rect(x - 6, y - 6, width + 12, height + 12));
            cutout.Freeze();
            icons.Add(symbol, cutout);
        }
        Add(HostSymbol.Overview, 31, 70, 241, 203);
        Add(HostSymbol.PrivateNetwork, 321, 66, 209, 193);
        Add(HostSymbol.StreamingHost, 575, 97, 242, 150);
        Add(HostSymbol.Displays, 861, 54, 221, 218);
        Add(HostSymbol.VirtualDisplay, 1129, 68, 225, 188);
        Add(HostSymbol.WakeOnLan, 45, 330, 215, 218);
        Add(HostSymbol.AdvancedSettings, 324, 327, 203, 212);
        Add(HostSymbol.Diagnostics, 582, 335, 235, 199);
        Add(HostSymbol.Address, 871, 332, 203, 204);
        Add(HostSymbol.Invitation, 1138, 332, 222, 216);
        Add(HostSymbol.Browser, 37, 616, 232, 180);
        Add(HostSymbol.Services, 312, 614, 228, 184);
        Add(HostSymbol.Device, 616, 587, 164, 228);
        Add(HostSymbol.Download, 862, 605, 217, 198);
        Add(HostSymbol.Microphone, 1173, 588, 155, 225);
        Add(HostSymbol.Terminal, 38, 883, 228, 181);
        Add(HostSymbol.Configuration, 332, 864, 189, 217);
        Add(HostSymbol.Recovery, 597, 877, 196, 197);
        Add(HostSymbol.Driver, 861, 861, 221, 218);
        Add(HostSymbol.Shield, 1150, 863, 200, 223);
        return icons;
    }

    internal static void Draw(DrawingContext dc, HostSymbol symbol, Rect bounds)
    {
        var icon = Icons[symbol];
        var scale = Math.Min(bounds.Width / icon.PixelWidth, bounds.Height / icon.PixelHeight);
        var width = icon.PixelWidth * scale;
        var height = icon.PixelHeight * scale;
        dc.DrawImage(icon, new Rect(bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2, width, height));
    }
}
