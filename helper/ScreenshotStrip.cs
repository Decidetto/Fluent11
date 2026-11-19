using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fluent11Helper
{
    internal static class ScreenshotStrip
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static IPlayniteAPI api;
        private static Func<Fluent11HelperSettings> getSettings;
        private static Fluent11HelperSettings settings => getSettings();
        private static Plugin plugin;
        private static bool pluginSearched;
        private static Guid currentGame;
        private static int generation;

        private const double MinWidth = 213;
        private const double Gap = 8;

        private const int MaxDecodeWidth = 432;
        private const int FirstBatch = 8;
        private const int CacheSize = 150;
        private static readonly Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>> cache =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, BitmapSource>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<KeyValuePair<string, BitmapSource>> cacheOrder = new LinkedList<KeyValuePair<string, BitmapSource>>();

        public static void Register(IPlayniteAPI playniteApi, Func<Fluent11HelperSettings> helperSettings)
        {
            api = playniteApi;
            getSettings = helperSettings;
            EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnClick));
            EventManager.RegisterClassHandler(typeof(ScrollViewer), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnSizeChanged));
            EventManager.RegisterClassHandler(typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
        }

        public static void OnGameSelected(Game game)
        {
            currentGame = game?.Id ?? Guid.Empty;
            var trailer = game != null && settings.ShowTrailer ? MediaViewer.TrailerPath(api, game.Id, settings.TrailerMicro) : null;

            var groupsType = settings.ScreenshotStrip
                ? FindPlugin()?.GetType().Assembly.GetType("KNARZhelper.ScreenshotsCommon.Models.ScreenshotGroups")
                : null;
            if (game == null || (groupsType == null && trailer == null))
            {
                generation++;
                Publish(new List<ShotThumb>());
                return;
            }

            var gameId = game.Id;
            var basePath = groupsType == null ? null : plugin.GetPluginUserDataPath();
            Start(() =>
            {
                var items = new List<ShotThumb>();
                if (trailer != null)
                {
                    items.Add(TrailerThumb(trailer));
                }

                if (groupsType != null)
                {
                    var groups = Activator.CreateInstance(groupsType) as IEnumerable;
                    groupsType.GetMethod("CreateGroupsFromFiles")?.Invoke(groups, new object[] { basePath, gameId, false });
                    items.AddRange(Flatten(Sorted(groups)));
                }

                return items;
            });
        }

        private static ShotThumb TrailerThumb(string path)
        {
            return new ShotThumb { Path = path, FullPath = path, IsVideo = true, IsTrailer = true, Image = Cached(VideoKey(path)) };
        }

        private static INotifyPropertyChanged watchedSettings;

        private static Plugin FindPlugin()
        {
            if (!pluginSearched)
            {
                pluginSearched = true;
                plugin = api.Addons.Plugins.FirstOrDefault(a => a.GetType().Assembly.GetName().Name == "ScreenshotUtilities");
            }

            var current = plugin == null ? null : AddonSettings() as INotifyPropertyChanged;
            if (!ReferenceEquals(current, watchedSettings))
            {
                if (watchedSettings != null)
                {
                    watchedSettings.PropertyChanged -= OnAddonSettingsChanged;
                }

                watchedSettings = current;
                if (watchedSettings != null)
                {
                    watchedSettings.PropertyChanged += OnAddonSettingsChanged;
                }
            }

            return plugin;
        }

        private static object AddonSettings()
        {
            return Reflect.GetMember(Reflect.GetMember(plugin, "Settings"), "Settings");
        }

        private static void OnAddonSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "CurrentScreenshotGroups" || !settings.ScreenshotStrip || currentGame == Guid.Empty)
            {
                return;
            }

            var items = Flatten(Reflect.GetMember(sender, "CurrentScreenshotGroups") as IEnumerable);
            var current = settings.ScreenshotThumbs.Where(a => !a.IsTrailer);
            if (items.Count == 0 || items.Select(a => a.Path).SequenceEqual(current.Select(a => a.Path), StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            items.InsertRange(0, settings.ScreenshotThumbs.Where(a => a.IsTrailer));
            Start(() => items);
        }

        private static void Start(Func<List<ShotThumb>> create)
        {
            var run = ++generation;
            Task.Run(() =>
            {
                try
                {
                    var items = create();
                    if (run != generation)
                    {
                        return;
                    }

                    var watch = Stopwatch.StartNew();
                    var published = false;
                    var local = items.Where(a => !IsWeb(a.Path) && !a.IsTrailer).ToList();
                    for (var i = 0; i < local.Count; i++)
                    {
                        if (run != generation)
                        {
                            return;
                        }

                        local[i].Image = Load(local[i].Path);
                        if (!published && (i + 1 >= FirstBatch || watch.ElapsedMilliseconds > 150))
                        {
                            published = PublishFrom(run, items);
                        }
                    }

                    if (!published && !PublishFrom(run, items))
                    {
                        return;
                    }

                    foreach (var item in items.Where(a => a.IsTrailer && a.Image == null))
                    {
                        if (run != generation)
                        {
                            return;
                        }

                        item.Image = LoadVideoFrame(item.Path);
                    }

                    foreach (var item in items.Where(a => IsWeb(a.Path)))
                    {
                        if (run != generation)
                        {
                            return;
                        }

                        item.Image = Load(item.Path);
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e, "Fluent 11: failed to load screenshot thumbnails.");
                }
            });
        }

        private static bool PublishFrom(int run, List<ShotThumb> items)
        {
            api.MainView.UIDispatcher.Invoke(() =>
            {
                if (run == generation)
                {
                    Publish(items);
                }
            });
            return run == generation;
        }

        private static void Publish(List<ShotThumb> items)
        {
            if (settings.ScreenshotThumbs.Count != 0 || items.Count != 0)
            {
                settings.ScreenshotThumbs = items;
            }

            var hasShots = items.Any(a => !a.IsTrailer);
            var hasTrailer = items.Any(a => a.IsTrailer);
            settings.ScreenshotStripTitle = hasShots && hasTrailer
                ? "Media"
                : hasShots
                    ? Application.Current?.TryFindResource("LOC_ScreenshotUtilities_ControlLabel") as string ?? "Screenshots"
                    : hasTrailer ? "Trailer" : string.Empty;
        }

        private static IEnumerable Sorted(IEnumerable groups)
        {
            var providerSettings = Reflect.GetMember(AddonSettings(), "ProviderSettings") as IDictionary;
            return groups?.Cast<object>()
                .OrderBy(g =>
                {
                    var name = Reflect.GetMember(Reflect.GetMember(g, "Provider"), "Name") as string;
                    var order = name != null && providerSettings?.Contains(name) == true
                        ? Reflect.GetMember(providerSettings[name], "SortOrder") : null;
                    return order is int value ? value : 0;
                })
                .ThenBy(g => Reflect.GetMember(g, "Name") as string, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static List<ShotThumb> Flatten(IEnumerable groups)
        {
            var items = new List<ShotThumb>();
            var limit = settings.ScreenshotLimit;
            foreach (var group in groups?.Cast<object>() ?? Enumerable.Empty<object>())
            {
                var shots = (Reflect.GetMember(group, "Screenshots") as IEnumerable)?.Cast<object>() ?? Enumerable.Empty<object>();
                var source = Reflect.GetMember(Reflect.GetMember(group, "Provider"), "Name") as string;
                if (settings.ScreenshotOrder == 1)
                {
                    shots = shots.OrderByDescending(FileDate).ToList();
                }

                foreach (var shot in shots)
                {
                    if (limit > 0 && items.Count >= limit)
                    {
                        return items;
                    }

                    var path = Reflect.GetMember(shot, "DisplayThumbnail") as string;
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    var isVideo = true.Equals(Reflect.GetMember(shot, "IsVideo"));
                    var full = Reflect.GetMember(shot, isVideo ? "VideoPath" : "DisplayPath") as string;
                    items.Add(new ShotThumb
                    {
                        Path = path,
                        FullPath = string.IsNullOrEmpty(full) ? path : full,
                        IsVideo = isVideo,
                        Source = source,
                        Image = Cached(path)
                    });
                }
            }

            return items;
        }

        private static DateTime FileDate(object shot)
        {
            try
            {
                var path = Reflect.GetMember(shot, true.Equals(Reflect.GetMember(shot, "IsVideo")) ? "VideoPath" : "DisplayPath") as string;
                return string.IsNullOrEmpty(path) || IsWeb(path) || !File.Exists(path) ? DateTime.MinValue : File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static bool IsWeb(string path)
        {
            return path.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        }

        private static string CacheKey(string path, bool trim)
        {
            return (trim ? "trim|" : string.Empty) + path;
        }

        private static BitmapSource Cached(string path)
        {
            lock (cache)
            {
                if (cache.TryGetValue(CacheKey(path, settings.ScreenshotTrimBars), out var node))
                {
                    cacheOrder.Remove(node);
                    cacheOrder.AddFirst(node);
                    return node.Value.Value;
                }
            }

            return null;
        }

        private static BitmapSource Load(string path)
        {
            var trim = settings.ScreenshotTrimBars;
            var key = CacheKey(path, trim);
            var image = Cached(path);
            if (image != null)
            {
                return image;
            }

            try
            {
                byte[] data;
                if (IsWeb(path))
                {
                    using (var client = new SteamWebClient())
                    {
                        data = client.DownloadData(path);
                    }
                }
                else if (File.Exists(path))
                {
                    data = File.ReadAllBytes(path);
                }
                else
                {
                    return null;
                }

                using (var stream = new MemoryStream(data))
                {
                    var width = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
                    stream.Position = 0;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    if (width > MaxDecodeWidth)
                    {
                        bitmap.DecodePixelWidth = MaxDecodeWidth;
                    }

                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = trim ? TrimBars(bitmap) : bitmap;
                }
            }
            catch (Exception e)
            {
                logger.Debug($"Fluent 11: no thumbnail for {path}: {e.Message}");
                return null;
            }

            lock (cache)
            {
                if (!cache.ContainsKey(key))
                {
                    cache[key] = cacheOrder.AddFirst(new KeyValuePair<string, BitmapSource>(key, image));
                    while (cacheOrder.Count > CacheSize)
                    {
                        cache.Remove(cacheOrder.Last.Value.Key);
                        cacheOrder.RemoveLast();
                    }
                }
            }

            return image;
        }

        private static string VideoKey(string path)
        {
            return "video|" + path;
        }

        private static BitmapSource LoadVideoFrame(string path)
        {
            var key = VideoKey(path);
            var image = Cached(key);
            if (image != null)
            {
                return image;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    image = ShellThumbnail(path, MaxDecodeWidth);
                }
                catch (Exception e)
                {
                    logger.Debug($"Fluent 11: no video thumbnail for {path}: {e.Message}");
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(10)) || image == null)
            {
                return null;
            }

            if (settings.ScreenshotTrimBars)
            {
                image = TrimBars(image);
            }

            lock (cache)
            {
                if (!cache.ContainsKey(CacheKey(key, settings.ScreenshotTrimBars)))
                {
                    cache[CacheKey(key, settings.ScreenshotTrimBars)] = cacheOrder.AddFirst(
                        new KeyValuePair<string, BitmapSource>(CacheKey(key, settings.ScreenshotTrimBars), image));
                    while (cacheOrder.Count > CacheSize)
                    {
                        cache.Remove(cacheOrder.Last.Value.Key);
                        cacheOrder.RemoveLast();
                    }
                }
            }

            return image;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        private const int ThumbnailOnly = 0x8;

        private static BitmapSource ShellThumbnail(string path, int size)
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            var handle = IntPtr.Zero;
            try
            {
                if (factory.GetImage(new NativeSize { Width = size, Height = size }, ThumbnailOnly, out handle) != 0 || handle == IntPtr.Zero)
                {
                    return null;
                }

                var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                if (source.Format.BitsPerPixel != 32)
                {
                    source = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
                }

                var stride = source.PixelWidth * 4;
                var pixels = new byte[stride * source.PixelHeight];
                source.CopyPixels(pixels, stride, 0);
                var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
                result.Freeze();
                return result;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    DeleteObject(handle);
                }

                Marshal.ReleaseComObject(factory);
            }
        }

        private const int BarThreshold = 24;
        private const int BarEdge = 2;

        private static BitmapSource TrimBars(BitmapSource source)
        {
            try
            {
                var format = source.Format;
                var pixels = format == PixelFormats.Bgr32 || format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32
                    ? source
                    : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
                int width = pixels.PixelWidth, height = pixels.PixelHeight;
                if (width < 32 || height < 32)
                {
                    return source;
                }

                var stride = width * 4;
                var data = new byte[stride * height];
                var copied = new bool[height];

                bool Dark(int x, int y)
                {
                    if (!copied[y])
                    {
                        pixels.CopyPixels(new Int32Rect(0, y, width, 1), data, stride, y * stride);
                        copied[y] = true;
                    }

                    var i = y * stride + x * 4;
                    return data[i] <= BarThreshold && data[i + 1] <= BarThreshold && data[i + 2] <= BarThreshold;
                }

                bool DarkRow(int y)
                {
                    for (var x = 0; x < width; x++)
                    {
                        if (!Dark(x, y))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                var top = BarWidth(height, DarkRow);
                var bottom = top > 0 ? BarWidth(height, i => DarkRow(height - 1 - i)) : 0;
                if (AreBars(top, bottom, height))
                {
                    top += BarEdge;
                    bottom += BarEdge;
                }
                else
                {
                    top = bottom = 0;
                }

                var inner = height - top - bottom;
                bool DarkColumn(int x)
                {
                    for (var y = top; y < top + inner; y++)
                    {
                        if (!Dark(x, y))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                var left = BarWidth(width, DarkColumn);
                var right = left > 0 ? BarWidth(width, i => DarkColumn(width - 1 - i)) : 0;
                if (AreBars(left, right, width))
                {
                    left += BarEdge;
                    right += BarEdge;
                }
                else
                {
                    left = right = 0;
                }

                if (top + bottom + left + right == 0)
                {
                    return source;
                }

                var cropped = new CroppedBitmap(source, new Int32Rect(left, top, width - left - right, inner));
                cropped.Freeze();
                return cropped;
            }
            catch (Exception)
            {
                return source;
            }
        }

        private static int BarWidth(int size, Func<int, bool> isDark)
        {
            var limit = size / 4;
            var minBright = Math.Max(3, size / 16);
            var bar = 0;
            var bright = 0;
            for (var i = 0; i < limit; i++)
            {
                if (isDark(i))
                {
                    bar = i + 1;
                    bright = 0;
                }
                else if (bar == 0 || ++bright >= minBright)
                {
                    return bar;
                }
            }

            return -1;
        }

        private static bool AreBars(int first, int second, int size)
        {
            return first >= 2 && second >= 2 && Math.Abs(first - second) <= Math.Max(2, size / 50);
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged || !(sender is ScrollViewer scroller) || !"Fluent11ShotScroller".Equals(scroller.Tag))
            {
                return;
            }

            var available = e.NewSize.Width;
            if (available <= 0)
            {
                return;
            }

            var count = Math.Max(2, Math.Floor((available + Gap) / (MinWidth + Gap)));
            var width = Math.Floor((available - (count - 1) * Gap) / count);
            scroller.Resources["Fluent11ShotWidth"] = width;
            scroller.Resources["Fluent11ShotHeight"] = Math.Round(width * 9 / 16);
            scroller.ScrollToHorizontalOffset(Snap(scroller, scroller.HorizontalOffset, width + Gap));
        }

        private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!(sender is ScrollViewer scroller) || !"Fluent11ShotScroller".Equals(scroller.Tag) || !(scroller.Parent is Panel strip))
            {
                return;
            }

            var step = Step(scroller);
            foreach (var button in strip.Children.OfType<ButtonBase>())
            {
                switch (button.Tag as string)
                {
                    case "Fluent11ShotLeft":
                        button.IsEnabled = scroller.HorizontalOffset > 1;
                        break;
                    case "Fluent11ShotRight":
                        button.IsEnabled = scroller.HorizontalOffset < LastOffset(scroller, step) - 1;
                        break;
                }
            }
        }

        private static double Step(ScrollViewer scroller)
        {
            return (scroller.Resources["Fluent11ShotWidth"] as double? ?? MinWidth) + Gap;
        }

        private static double LastOffset(ScrollViewer scroller, double step)
        {
            return Math.Max(0, Math.Floor((scroller.ScrollableWidth + 0.5) / step) * step);
        }

        private static double Snap(ScrollViewer scroller, double offset, double step)
        {
            return Math.Min(Math.Max(0, Math.Round(offset / step) * step), LastOffset(scroller, step));
        }

        private const double ScrollAnimationMs = 250;
        private static readonly ConditionalWeakTable<ScrollViewer, ScrollAnimation> scrollAnimations = new ConditionalWeakTable<ScrollViewer, ScrollAnimation>();

        private class ScrollAnimation
        {
            public double From;
            public double To;
            public DateTime Started;
            public EventHandler Handler;
        }

        private static void SmoothScrollTo(ScrollViewer scroller, double target)
        {
            var animation = scrollAnimations.GetOrCreateValue(scroller);
            if (animation.Handler != null)
            {
                CompositionTarget.Rendering -= animation.Handler;
                animation.Handler = null;
            }

            if (!SystemParameters.ClientAreaAnimation)
            {
                scroller.ScrollToHorizontalOffset(target);
                return;
            }

            animation.From = scroller.HorizontalOffset;
            animation.To = target;
            animation.Started = DateTime.UtcNow;
            animation.Handler = (s, e) =>
            {
                var t = Math.Min(1.0, (DateTime.UtcNow - animation.Started).TotalMilliseconds / ScrollAnimationMs);
                var eased = 1 - Math.Pow(1 - t, 3);
                scroller.ScrollToHorizontalOffset(animation.From + (animation.To - animation.From) * eased);
                if (t >= 1 && animation.Handler != null)
                {
                    CompositionTarget.Rendering -= animation.Handler;
                    animation.Handler = null;
                }
            };
            CompositionTarget.Rendering += animation.Handler;
        }

        private static void OnClick(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            if (tag == null || !tag.StartsWith("Fluent11Shot", StringComparison.Ordinal) || !(sender is FrameworkElement button))
            {
                return;
            }

            e.Handled = true;
            try
            {
                if (tag == "Fluent11ShotOpen")
                {
                    if (button.DataContext is ShotThumb thumb && settings.ScreenshotThumbs is IList<ShotThumb> all)
                    {
                        var items = settings.ScreenshotFullScreen ? all : all.Where(a => a.IsTrailer).ToList();
                        MediaViewer.Open(button, items, items.IndexOf(thumb), settings.VideoStartMuted);
                    }
                }
                else if (button.Parent is Panel strip && strip.Children.OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer scroller)
                {
                    var step = Step(scroller);
                    var page = Math.Max(1, Math.Floor((scroller.ViewportWidth + Gap) / step)) * step;
                    var basis = scrollAnimations.TryGetValue(scroller, out var running) && running.Handler != null ? running.To : scroller.HorizontalOffset;
                    SmoothScrollTo(scroller, Snap(scroller, basis + (tag == "Fluent11ShotLeft" ? -page : page), step));
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Fluent 11: screenshot action failed.");
            }
        }
    }

    public class ShotThumb : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private BitmapSource image;
        public BitmapSource Image
        {
            get => image;
            set
            {
                image = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }

        public bool IsVideo { get; set; }
        public bool IsTrailer { get; set; }
        internal string Path { get; set; }
        internal string FullPath { get; set; }
        internal string Source { get; set; }
    }
}
