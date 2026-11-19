using Playnite.SDK;
using System;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Fluent11Helper
{
    internal static class CoverFormat
    {
        private static IPlayniteAPI api;
        private static Func<bool> useIcons = () => false;

        public static void Configure(IPlayniteAPI playniteApi, Func<bool> listUseIcons)
        {
            api = playniteApi;
            useIcons = listUseIcons;
            if (Application.Current != null)
            {
                Application.Current.Resources["Fluent11ListArtConverter"] = new ListArtConverter();
            }
        }

        public static bool ListUsesIcons => useIcons();

        private class ListArtConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                var cover = (Reflect.GetMember(value, "Cover") ?? Reflect.GetMember(value, "CoverImagePath")) as string;
                var path = cover;
                if (useIcons() && Reflect.GetMember(value, "GameId") is Guid id && api != null)
                {
                    var icon = api.Database.Games.Get(id)?.Icon;
                    if (!string.IsNullOrEmpty(icon))
                    {
                        path = icon.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? icon : api.Database.GetFullFilePath(icon);
                    }
                }

                var images = Application.Current?.TryFindResource("ImageStringToImageConverterCached") as IValueConverter;
                return string.IsNullOrEmpty(path) ? null : images != null ? images.Convert(path, targetType, parameter, culture) : path;
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            {
                throw new NotSupportedException();
            }
        }

        public static double WidthPerHeight()
        {
            try
            {
                var appSettings = PlayniteInternals.AppSettings;
                var width = Convert.ToDouble(Reflect.GetMember(appSettings, "GridItemWidthRatio"));
                var height = Convert.ToDouble(Reflect.GetMember(appSettings, "GridItemHeightRatio"));
                return width > 0 && height > 0 ? width / height : 1;
            }
            catch
            {
                return 1;
            }
        }

        public static void Apply(FrameworkElement host)
        {
            if (host == null)
            {
                return;
            }

            var ratio = WidthPerHeight();
            host.Resources["Fluent11ListCoverWidth"] = ListUsesIcons ? 32 : Math.Round(32 * ratio);
            var flyoutWidth = Math.Round(88 * ratio);
            host.Resources["Fluent11FlyoutCoverWidth"] = flyoutWidth;
            var flyoutClip = new RectangleGeometry(new Rect(0, 0, flyoutWidth, 88), 6, 6);
            flyoutClip.Freeze();
            host.Resources["Fluent11FlyoutCoverClip"] = flyoutClip;
            host.Resources["Fluent11LargeCoverWidth"] = Math.Round(160 * ratio);
        }
    }
}
