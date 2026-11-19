using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Fluent11Helper
{
    internal static class MediaViewer
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string ViewerFile = "Views\\Fluent11MediaViewer.xaml";
        public const string FullScreenTag = "Fluent11VideoFullScreen";

        private static string themeDir;
        private static Session current;
        private static double volume = 0.5;

        public static void Register(string theme)
        {
            themeDir = theme;
        }

        public static bool IsOpen => current != null;

        public static void SchemeChanged()
        {
            var session = current;
            session?.Dispatcher.BeginInvoke(new Action(session.RefreshBackdrop), DispatcherPriority.ContextIdle);
        }

        public static string TrailerPath(IPlayniteAPI api, Guid gameId, bool micro)
        {
            var folder = Path.Combine(api.Paths.ConfigurationPath, "ExtraMetadata", "games", gameId.ToString());
            var trailer = Path.Combine(folder, "VideoTrailer.mp4");
            var microtrailer = Path.Combine(folder, "VideoMicrotrailer.mp4");
            foreach (var path in micro ? new[] { microtrailer, trailer } : new[] { trailer, microtrailer })
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static bool startMuted = true;

        public static void Open(FrameworkElement tile, IList<ShotThumb> items, int index, bool muted)
        {
            if (current != null || themeDir == null || tile == null || items == null || index < 0 || index >= items.Count)
            {
                return;
            }

            try
            {
                startMuted = muted;
                current = new Session(tile, new List<ShotThumb>(items), index);
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to open the viewer.");
                current = null;
            }
        }

        private static bool IsWeb(string path)
        {
            return path.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        }

        private static BitmapSource Decode(string path, int maxWidth, out int originalWidth)
        {
            originalWidth = 0;
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
                originalWidth = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (originalWidth > maxWidth)
                {
                    bitmap.DecodePixelWidth = maxWidth;
                }

                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        private sealed class Session
        {
            private const double Gap = 48;
            private const double MinWidth = 960;
            private const double MaxWidth = 1600;
            private const double WidthShare = 0.6;
            private const double Aspect = 16.0 / 9;
            private const double SeekStep = 5;
            private const double VolumeStep = 0.1;
            private const double VideoBarWidth = 640;
            private const double ArrowGap = 16;
            private const double BelowGap = 16;
            private const int BackdropWidth = 64;
            private const double CaptureWidth = 256;
            private const int BackdropBlurRadius = 2;
            private const int BackdropBlurPasses = 3;
            private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(2.5);
            private static readonly Duration GrowDuration = new Duration(TimeSpan.FromMilliseconds(250));
            private static readonly Duration FadeDuration = new Duration(TimeSpan.FromMilliseconds(150));
            private static readonly Duration SlideDuration = new Duration(TimeSpan.FromMilliseconds(300));

            private readonly List<ShotThumb> items;
            private readonly Window window;
            private readonly FrameworkElement adorned;
            private readonly AdornerLayer layer;
            private readonly HostAdorner adorner;
            private readonly Grid root;
            private readonly Grid frame;
            private readonly Grid backdrop;
            private readonly Image backdropImage;
            private readonly Border dim;
            private readonly Border bar;
            private readonly Image picture;
            private readonly Image behind;
            private readonly MediaElement video;
            private readonly FrameworkElement error;
            private readonly Button playPause;
            private readonly Button mute;
            private readonly Button fullScreen;
            private readonly Button previous;
            private readonly Button next;
            private readonly Button previousOutside;
            private readonly Button nextOutside;
            private readonly FrameworkElement below;
            private readonly TextBlock title;
            private readonly FrameworkElement pill;
            private readonly FrameworkElement counterOutside;
            private readonly TextBlock counterCurrent;
            private readonly TextBlock counterTotal;
            private readonly FrameworkElement belowDivider;
            private readonly Button fullScreenOutside;
            private readonly FrameworkElement fullPill;
            private readonly FrameworkElement fullCounter;
            private readonly TextBlock fullCounterCurrent;
            private readonly TextBlock fullCounterTotal;
            private readonly FrameworkElement fullDivider;
            private readonly FrameworkElement caption;
            private readonly Button captionMaximize;
            private readonly Slider seek;
            private readonly Slider volumeSlider;
            private readonly TextBlock time;
            private readonly TextBlock counter;
            private readonly FrameworkElement[] videoControls;
            private readonly DispatcherTimer positionTimer;
            private readonly DispatcherTimer hideTimer;
            private readonly IInputElement previousFocus;
            private readonly double cornerRadius;

            private int index;
            private Window fullScreenWindow;
            private bool playing;
            private bool updating;
            private bool dragging;
            private bool closed;
            private bool barVisible;
            private bool switching;
            private FrameworkElement standIn;
            private Point? lastMouse;
            private int loadGeneration;
            private int loadedWidth;
            private int originalWidth;

            private ShotThumb Current => items[index];

            private bool IsVideoShown => Current.IsVideo && error.Visibility != Visibility.Visible;

            public Dispatcher Dispatcher => root.Dispatcher;

            public Session(FrameworkElement tile, List<ShotThumb> items, int index)
            {
                this.items = items;
                this.index = index;
                window = Window.GetWindow(tile) ?? throw new InvalidOperationException("Tile is not in a window.");

                var decorator = FindDecorator(window);
                adorned = decorator?.Child as FrameworkElement ?? window.Content as FrameworkElement;
                layer = decorator?.AdornerLayer ?? (adorned == null ? null : AdornerLayer.GetAdornerLayer(adorned));
                if (adorned == null || layer == null)
                {
                    throw new InvalidOperationException("No adorner layer in the window.");
                }

                using (var stream = File.OpenRead(Path.Combine(themeDir, ViewerFile)))
                {
                    root = (Grid)XamlReader.Load(stream);
                }

                frame = (Grid)root.FindName("Frame");
                backdrop = (Grid)root.FindName("Backdrop");
                backdropImage = (Image)root.FindName("BackdropImage");
                dim = (Border)root.FindName("Dim");
                bar = (Border)root.FindName("Bar");
                picture = (Image)root.FindName("Picture");
                behind = (Image)root.FindName("Behind");
                video = (MediaElement)root.FindName("Video");
                error = (FrameworkElement)root.FindName("Error");
                playPause = (Button)root.FindName("PlayPause");
                mute = (Button)root.FindName("Mute");
                fullScreen = (Button)root.FindName("FullScreen");
                previous = (Button)root.FindName("Previous");
                next = (Button)root.FindName("Next");
                seek = (Slider)root.FindName("Seek");
                volumeSlider = (Slider)root.FindName("Volume");
                time = (TextBlock)root.FindName("Time");
                counter = (TextBlock)root.FindName("Counter");
                previousOutside = (Button)root.FindName("PreviousOutside");
                nextOutside = (Button)root.FindName("NextOutside");
                below = (FrameworkElement)root.FindName("Below");
                title = (TextBlock)root.FindName("Title");
                pill = (FrameworkElement)root.FindName("Pill");
                counterOutside = (FrameworkElement)root.FindName("CounterOutside");
                counterCurrent = (TextBlock)root.FindName("CounterCurrent");
                counterTotal = (TextBlock)root.FindName("CounterTotal");
                belowDivider = (FrameworkElement)root.FindName("BelowDivider");
                fullScreenOutside = (Button)root.FindName("FullScreenOutside");
                fullPill = (FrameworkElement)root.FindName("FullPill");
                fullCounter = (FrameworkElement)root.FindName("FullCounter");
                fullCounterCurrent = (TextBlock)root.FindName("FullCounterCurrent");
                fullCounterTotal = (TextBlock)root.FindName("FullCounterTotal");
                fullDivider = (FrameworkElement)root.FindName("FullDivider");
                caption = (FrameworkElement)root.FindName("Caption");
                captionMaximize = (Button)root.FindName("CaptionMaximize");
                videoControls = new[] { playPause, seek, time, (FrameworkElement)root.FindName("VolumePanel") };
                var openExternal = (Button)root.FindName("OpenExternal");
                cornerRadius = (Application.Current?.TryFindResource("CardCornerRadius") as CornerRadius?)?.TopLeft ?? 8;

                video.MediaOpened += OnMediaOpened;
                video.MediaEnded += OnMediaEnded;
                video.MediaFailed += OnMediaFailed;
                playPause.Click += (s, e) => TogglePlay();
                mute.Click += (s, e) => ToggleMute();
                fullScreen.Click += (s, e) => ToggleFullScreen();
                previous.Click += (s, e) => Go(-1);
                next.Click += (s, e) => Go(1);
                previousOutside.Click += (s, e) => Go(-1);
                nextOutside.Click += (s, e) => Go(1);
                fullScreenOutside.Click += (s, e) => ToggleFullScreen();
                ((Button)root.FindName("FullScreenExit")).Click += (s, e) => ToggleFullScreen();
                ((Button)root.FindName("CaptionMinimize")).Click += (s, e) => PressWindowButton("PART_ButtonMinimize");
                captionMaximize.Click += (s, e) => PressWindowButton("PART_ButtonMaximize");
                ((Button)root.FindName("CaptionClose")).Click += (s, e) =>
                {
                    Close();
                    PressWindowButton("PART_ButtonClose");
                };
                if (openExternal != null)
                {
                    openExternal.Click += (s, e) => OpenExternal();
                }

                seek.ValueChanged += OnSeekChanged;
                seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((s, e) => dragging = true));
                seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) =>
                {
                    dragging = false;
                    if (IsVideoShown)
                    {
                        SeekTo(seek.Value);
                    }

                    RestartHideTimer();
                }));
                volumeSlider.Value = volume;
                volumeSlider.ValueChanged += OnVolumeChanged;

                dim.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    Close();
                };
                frame.MouseLeftButtonDown += OnFrameClick;
                root.PreviewKeyDown += OnKey;
                root.MouseMove += OnMouseMove;
                adorned.SizeChanged += OnAdornedSizeChanged;

                positionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                positionTimer.Tick += (s, e) => UpdatePosition();
                hideTimer = new DispatcherTimer { Interval = HideDelay };
                hideTimer.Tick += (s, e) =>
                {
                    hideTimer.Stop();
                    if (playing && !dragging && !bar.IsMouseOver && !previous.IsMouseOver && !next.IsMouseOver
                        && !previousOutside.IsMouseOver && !nextOutside.IsMouseOver && !below.IsMouseOver && !fullPill.IsMouseOver)
                    {
                        HideBar();
                    }
                };

                video.Volume = volume;
                video.IsMuted = startMuted;
                UpdateMuteGlyph();

                previousFocus = Keyboard.FocusedElement;
                var snapshot = Snapshot(adorned);
                if (snapshot != null)
                {
                    backdropImage.Source = snapshot;
                }
                else
                {
                    dim.SetResourceReference(Border.BackgroundProperty, "OverlayDimBrush");
                }

                adorner = new HostAdorner(adorned, root);
                layer.Add(adorner);
                Layout();
                Show(index, false);
                layer.UpdateLayout();
                AnimateIn(tile);
                Keyboard.Focus(root);
            }

            private static AdornerDecorator FindDecorator(DependencyObject start)
            {
                var queue = new Queue<DependencyObject>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var element = queue.Dequeue();
                    if (element is AdornerDecorator decorator && decorator.Child != null)
                    {
                        return decorator;
                    }

                    var count = VisualTreeHelper.GetChildrenCount(element);
                    for (var i = 0; i < count; i++)
                    {
                        queue.Enqueue(VisualTreeHelper.GetChild(element, i));
                    }
                }

                return null;
            }

            private void Show(int i, bool showBar = true, int direction = 0)
            {
                var item = items[i];
                var leavingVideo = direction != 0 && index != i && IsVideoShown && video.Source != null && !item.IsVideo;
                StopVideo(leavingVideo);
                var shown = index != i && !leavingVideo ? (items[index].IsVideo ? items[index].Image : picture.Source) : null;
                error.Visibility = Visibility.Collapsed;
                frame.ClearValue(FrameworkElement.CursorProperty);
                loadGeneration++;
                index = i;

                if (item.IsVideo)
                {
                    picture.Visibility = Visibility.Collapsed;
                    picture.Source = null;
                    video.RenderTransform = Transform.Identity;
                    video.Visibility = Visibility.Visible;
                    SetVideoControls(true);
                    try
                    {
                        video.Source = new Uri(item.FullPath);
                        video.Play();
                        playing = true;
                        positionTimer.Start();
                    }
                    catch (Exception e)
                    {
                        logger.Warn($"Fluent 11: video can't be played ({item.FullPath}): {e.Message}");
                        ShowError();
                    }
                }
                else
                {
                    if (!leavingVideo)
                    {
                        video.Visibility = Visibility.Collapsed;
                    }

                    picture.Source = item.Image;
                    picture.Visibility = Visibility.Visible;
                    SetVideoControls(false);
                    LoadOriginal();
                }

                if (item.IsVideo)
                {
                    frame.SetResourceReference(Panel.BackgroundProperty, "VideoBackgroundBrush");
                }
                else
                {
                    frame.Background = null;
                }

                SlideIn(item.IsVideo ? (UIElement)video : picture, direction, shown, leavingVideo);
                UpdatePlayGlyph();
                counter.Text = items.Count > 1 ? (index + 1) + " / " + items.Count : string.Empty;
                counterCurrent.Text = (index + 1).ToString();
                counterTotal.Text = " / " + items.Count;
                fullCounterCurrent.Text = counterCurrent.Text;
                fullCounterTotal.Text = counterTotal.Text;
                title.Text = TitleOf(index);
                UpdateChrome();
                if (showBar)
                {
                    ShowBar();
                }
            }

            private void Go(int step)
            {
                var target = index + step;
                if (closed || target < 0 || target >= items.Count)
                {
                    return;
                }

                Show(target, true, step);
            }

            private void SlideIn(UIElement element, int direction, ImageSource previousImage, bool leavingVideo)
            {
                var width = frame.ActualWidth;
                if (direction == 0 || !SystemParameters.ClientAreaAnimation || width <= 0)
                {
                    if (leavingVideo)
                    {
                        ReleaseVideo();
                    }

                    return;
                }

                var leaving = leavingVideo ? (UIElement)video : behind;
                if (!leavingVideo)
                {
                    behind.Source = previousImage;
                    behind.Visibility = previousImage == null ? Visibility.Collapsed : Visibility.Visible;
                }

                var incoming = new TranslateTransform(direction * width, 0);
                var outgoing = new TranslateTransform();
                element.RenderTransform = incoming;
                leaving.RenderTransform = outgoing;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var slide = new DoubleAnimation(0, SlideDuration) { EasingFunction = ease };
                slide.Completed += (s, e) =>
                {
                    if (element.RenderTransform == incoming)
                    {
                        element.RenderTransform = Transform.Identity;
                    }

                    if (leaving.RenderTransform != outgoing)
                    {
                        return;
                    }

                    if (leavingVideo)
                    {
                        ReleaseVideo();
                    }
                    else
                    {
                        behind.RenderTransform = Transform.Identity;
                        behind.Source = null;
                        behind.Visibility = Visibility.Collapsed;
                    }
                };
                incoming.BeginAnimation(TranslateTransform.XProperty, slide);
                outgoing.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-direction * width, SlideDuration) { EasingFunction = ease });
            }

            private void UpdateChrome()
            {
                var full = fullScreenWindow != null;
                var several = items.Count > 1;
                var previousVisibility = !several ? Visibility.Collapsed : index > 0 ? Visibility.Visible : Visibility.Hidden;
                var nextVisibility = !several ? Visibility.Collapsed : index < items.Count - 1 ? Visibility.Visible : Visibility.Hidden;
                previous.Visibility = full ? previousVisibility : Visibility.Collapsed;
                next.Visibility = full ? nextVisibility : Visibility.Collapsed;
                previousOutside.Visibility = full ? Visibility.Collapsed : previousVisibility;
                nextOutside.Visibility = full ? Visibility.Collapsed : nextVisibility;
                counter.Visibility = full && several ? Visibility.Visible : Visibility.Collapsed;
                bar.Visibility = Current.IsVideo ? Visibility.Visible : Visibility.Collapsed;
                fullPill.Visibility = full && !Current.IsVideo ? Visibility.Visible : Visibility.Collapsed;
                fullCounter.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
                fullDivider.Visibility = several ? Visibility.Visible : Visibility.Collapsed;

                var showCounter = !full && several;
                var showFullScreen = !full && !Current.IsVideo;
                counterOutside.Visibility = showCounter ? Visibility.Visible : Visibility.Collapsed;
                fullScreenOutside.Visibility = showFullScreen ? Visibility.Visible : Visibility.Collapsed;
                belowDivider.Visibility = showCounter && showFullScreen ? Visibility.Visible : Visibility.Collapsed;
                pill.Visibility = showCounter || showFullScreen ? Visibility.Visible : Visibility.Collapsed;
                below.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
            }

            private string TitleOf(int i)
            {
                var item = items[i];
                if (item.IsTrailer)
                {
                    return "Trailer";
                }

                var number = 0;
                for (var j = 0; j <= i; j++)
                {
                    if (!items[j].IsTrailer && items[j].IsVideo == item.IsVideo && items[j].Source == item.Source)
                    {
                        number++;
                    }
                }

                var name = (item.IsVideo ? "Video " : "Screenshot ") + number;
                return string.IsNullOrEmpty(item.Source) ? name : name + " · " + item.Source;
            }

            private void SetVideoControls(bool isVideo)
            {
                foreach (var control in videoControls)
                {
                    control.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
                }

                bar.Width = isVideo ? VideoBarWidth : double.NaN;
                bar.HorizontalAlignment = HorizontalAlignment.Center;
            }

            private void LoadOriginal()
            {
                var path = Current.FullPath;
                loadedWidth = 0;
                originalWidth = 0;
                var run = ++loadGeneration;
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                var thumb = Current.Image;
                var needed = (int)Math.Ceiling(NeededWidth() * FillFactor(thumb));
                Task.Run(() =>
                {
                    BitmapSource image = null;
                    var original = 0;
                    try
                    {
                        image = Decode(path, needed, out original);
                    }
                    catch (Exception e)
                    {
                        logger.Debug($"Fluent 11: original image not loaded ({path}): {e.Message}");
                    }

                    window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (closed || run != loadGeneration || image == null || image.PixelHeight <= 0)
                        {
                            return;
                        }

                        loadedWidth = image.PixelWidth;
                        originalWidth = original;
                        picture.Source = SameCrop(image, thumb);
                    }));
                });
            }

            private static BitmapSource SameCrop(BitmapSource image, BitmapSource thumb)
            {
                if (!(thumb is CroppedBitmap crop) || crop.Source == null || crop.Source.PixelWidth <= 0 || crop.Source.PixelHeight <= 0)
                {
                    return image;
                }

                try
                {
                    var scaleX = (double)image.PixelWidth / crop.Source.PixelWidth;
                    var scaleY = (double)image.PixelHeight / crop.Source.PixelHeight;
                    var rect = crop.SourceRect;
                    var x = (int)Math.Round(rect.X * scaleX);
                    var y = (int)Math.Round(rect.Y * scaleY);
                    var width = Math.Min(image.PixelWidth - x, (int)Math.Round(rect.Width * scaleX));
                    var height = Math.Min(image.PixelHeight - y, (int)Math.Round(rect.Height * scaleY));
                    if (width <= 0 || height <= 0)
                    {
                        return image;
                    }

                    var cropped = new CroppedBitmap(image, new Int32Rect(x, y, width, height));
                    cropped.Freeze();
                    return cropped;
                }
                catch (Exception)
                {
                    return image;
                }
            }

            private static double FillFactor(BitmapSource thumb)
            {
                if (thumb == null || thumb.PixelWidth <= 0 || thumb.PixelHeight <= 0)
                {
                    return 1;
                }

                var factor = Math.Max(1, (double)thumb.PixelWidth / thumb.PixelHeight / Aspect);
                if (thumb is CroppedBitmap crop && crop.Source != null && crop.Source.PixelWidth > 0)
                {
                    factor *= (double)crop.Source.PixelWidth / thumb.PixelWidth;
                }

                return factor;
            }

            private int NeededWidth()
            {
                var target = (Visual)fullScreenWindow ?? window;
                var scale = PresentationSource.FromVisual(target)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                var width = fullScreenWindow != null ? fullScreenWindow.ActualWidth : MaxWidth;
                if (double.IsNaN(width) || width <= 0)
                {
                    width = MaxWidth;
                }

                return (int)Math.Ceiling(width * scale);
            }

            private void Layout()
            {
                if (closed)
                {
                    return;
                }

                var size = fullScreenWindow != null
                    ? FitAspect(fullScreenWindow.ActualWidth, fullScreenWindow.ActualHeight)
                    : WindowFrameSize();
                var radius = fullScreenWindow != null ? 0 : cornerRadius;
                frame.Width = size.Width;
                frame.Height = size.Height;
                frame.Clip = new RectangleGeometry(new Rect(size), radius, radius);

                var arrowOffset = size.Width + 2 * ArrowGap + previousOutside.Width;
                previousOutside.Margin = new Thickness(0, 0, arrowOffset, 0);
                nextOutside.Margin = new Thickness(arrowOffset, 0, 0, 0);
                below.Margin = new Thickness(0, size.Height + 2 * BelowGap + below.Height, 0, 0);

                var buttons = CaptionButtons();
                caption.Visibility = buttons.HasValue ? Visibility.Visible : Visibility.Collapsed;
                if (buttons.HasValue)
                {
                    caption.Margin = new Thickness(0, buttons.Value.Top, Math.Max(0, adorned.ActualWidth - buttons.Value.Right), 0);
                    captionMaximize.Content = window.WindowState == WindowState.Maximized ? "" : "";
                }
            }

            private Rect? CaptionButtons()
            {
                Rect? bounds = null;
                foreach (var name in new[] { "PART_ButtonMinimize", "PART_ButtonMaximize", "PART_ButtonClose" })
                {
                    if (window.Template?.FindName(name, window) is FrameworkElement button && button.IsVisible && button.ActualWidth > 0)
                    {
                        var rect = button.TransformToVisual(adorned).TransformBounds(new Rect(button.RenderSize));
                        bounds = bounds.HasValue ? Rect.Union(bounds.Value, rect) : rect;
                    }
                }

                return bounds;
            }

            public void RefreshBackdrop()
            {
                var snapshot = closed ? null : Snapshot(adorned);
                if (snapshot != null)
                {
                    backdropImage.Source = snapshot;
                }
            }

            private void PressWindowButton(string name)
            {
                if (window.Template?.FindName(name, window) is Button button
                    && new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is IInvokeProvider invoker)
                {
                    invoker.Invoke();
                }
            }

            private static BitmapSource Snapshot(FrameworkElement element)
            {
                try
                {
                    var width = element.ActualWidth;
                    var height = element.ActualHeight;
                    if (width < 1 || height < 1)
                    {
                        return null;
                    }

                    var scale = Math.Min(1, CaptureWidth / width);
                    var reduced = new Size(Math.Max(1, Math.Ceiling(width * scale)), Math.Max(1, Math.Ceiling(height * scale)));
                    var background = Application.Current?.TryFindResource("WindowBackgroundColor") as Color? ?? Colors.Gray;
                    var brush = new VisualBrush(element)
                    {
                        Viewbox = new Rect(0, 0, width, height),
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Stretch = Stretch.Fill
                    };
                    var capture = Render(reduced, dc =>
                    {
                        dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(reduced));
                        dc.DrawRectangle(brush, null, new Rect(reduced));
                    });

                    var small = new Size(BackdropWidth, Math.Max(1, Math.Round(BackdropWidth * height / width)));
                    var result = Blur(Render(small, dc => dc.DrawImage(capture, new Rect(small))));
                    result.Freeze();
                    return result;
                }
                catch (Exception e)
                {
                    logger.Warn($"Fluent 11: viewer backdrop failed: {e.Message}");
                    return null;
                }
            }

            private static BitmapSource Blur(BitmapSource source)
            {
                var width = source.PixelWidth;
                var height = source.PixelHeight;
                var stride = width * 4;
                var pixels = new byte[stride * height];
                source.CopyPixels(pixels, stride, 0);
                var buffer = new byte[pixels.Length];
                for (var pass = 0; pass < BackdropBlurPasses; pass++)
                {
                    BoxBlur(pixels, buffer, width, height, 4, stride);
                    BoxBlur(buffer, pixels, height, width, stride, 4);
                }

                return BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            }

            private static void BoxBlur(byte[] from, byte[] to, int length, int lines, int step, int lineStep)
            {
                for (var line = 0; line < lines; line++)
                {
                    var start = line * lineStep;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var sum = 0;
                        for (var k = -BackdropBlurRadius; k <= BackdropBlurRadius; k++)
                        {
                            sum += from[start + Math.Min(length - 1, Math.Max(0, k)) * step + channel];
                        }

                        for (var i = 0; i < length; i++)
                        {
                            to[start + i * step + channel] = (byte)((sum + BackdropBlurRadius) / (2 * BackdropBlurRadius + 1));
                            sum += from[start + Math.Min(length - 1, i + BackdropBlurRadius + 1) * step + channel]
                                - from[start + Math.Max(0, i - BackdropBlurRadius) * step + channel];
                        }
                    }
                }
            }

            private static RenderTargetBitmap Render(Size size, Action<DrawingContext> draw)
            {
                var visual = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                using (var dc = visual.RenderOpen())
                {
                    draw(dc);
                }

                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                return bitmap;
            }

            private Size WindowFrameSize()
            {
                var horizontalSpace = 2 * (Gap + ArrowGap + previousOutside.Width);
                var verticalSpace = 2 * (Gap + BelowGap + below.Height);
                var limit = Math.Min(MaxWidth, Math.Max(MinWidth, adorned.ActualWidth * WidthShare));
                var width = Math.Min(limit, Math.Max(0, adorned.ActualWidth - horizontalSpace));
                return FitAspect(width, Math.Max(0, adorned.ActualHeight - verticalSpace));
            }

            private static Size FitAspect(double width, double height)
            {
                var frameWidth = Math.Floor(Math.Min(width, height * Aspect));
                return new Size(frameWidth, Math.Floor(frameWidth / Aspect));
            }

            private void OnAdornedSizeChanged(object sender, SizeChangedEventArgs e)
            {
                Layout();
            }

            private void AnimateIn(FrameworkElement tile)
            {
                if (!SystemParameters.ClientAreaAnimation || !tile.IsVisible || frame.Width <= 0 || frame.Height <= 0)
                {
                    ShowBar();
                    return;
                }

                var from = tile.TransformToVisual(adorned).TransformBounds(new Rect(tile.RenderSize));
                var to = new Rect((adorned.ActualWidth - frame.Width) / 2, (adorned.ActualHeight - frame.Height) / 2, frame.Width, frame.Height);
                MoveFrame(from, to, () =>
                {
                    if (!closed)
                    {
                        ShowBar();
                    }
                });
                backdrop.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, GrowDuration));
            }

            private void MoveFrame(Rect away, Rect to, Action done)
            {
                var scale = new ScaleTransform();
                var move = new TranslateTransform();
                var group = new TransformGroup();
                group.Children.Add(scale);
                group.Children.Add(move);
                frame.RenderTransform = group;

                double scaleX = away.Width / to.Width, scaleY = away.Height / to.Height, x = away.X - to.X, y = away.Y - to.Y;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                DoubleAnimation Animate(double start, double end)
                {
                    return new DoubleAnimation(start, end, GrowDuration) { EasingFunction = ease };
                }

                var first = Animate(scaleX, 1);
                first.Completed += (s, e) =>
                {
                    if (frame.RenderTransform == group)
                    {
                        frame.RenderTransform = Transform.Identity;
                    }

                    done?.Invoke();
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, first);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Animate(scaleY, 1));
                move.BeginAnimation(TranslateTransform.XProperty, Animate(x, 0));
                move.BeginAnimation(TranslateTransform.YProperty, Animate(y, 0));
            }

            private void OnMediaOpened(object sender, RoutedEventArgs e)
            {
                if (video.NaturalDuration.HasTimeSpan)
                {
                    seek.Maximum = video.NaturalDuration.TimeSpan.TotalSeconds;
                }

                UpdatePosition();
            }

            private void OnMediaEnded(object sender, RoutedEventArgs e)
            {
                video.Pause();
                video.Position = TimeSpan.Zero;
                playing = false;
                UpdatePlayGlyph();
                UpdatePosition();
                ShowBar();
            }

            private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
            {
                if (error.Visibility == Visibility.Visible || !Current.IsVideo)
                {
                    return;
                }

                logger.Warn($"Fluent 11: video can't be played ({Current.FullPath}): {e.ErrorException?.Message}");
                ShowError();
            }

            private void ShowError()
            {
                playing = false;
                positionTimer.Stop();
                hideTimer.Stop();
                SetVideoControls(false);
                error.Visibility = Visibility.Visible;
                frame.ClearValue(FrameworkElement.CursorProperty);
                ShowBar();
            }

            private void StopVideo(bool keepPlayer = false)
            {
                positionTimer.Stop();
                hideTimer.Stop();
                playing = false;
                if (keepPlayer)
                {
                    video.Pause();
                }
                else
                {
                    ReleaseVideo();
                }

                updating = true;
                seek.Maximum = 1;
                seek.Value = 0;
                updating = false;
                time.Text = string.Empty;
            }

            private void ReleaseVideo()
            {
                if (video.Source != null)
                {
                    video.Stop();
                    video.Close();
                    video.Source = null;
                }

                video.RenderTransform = Transform.Identity;
                if (!Current.IsVideo)
                {
                    video.Visibility = Visibility.Collapsed;
                }
            }

            private void OpenExternal()
            {
                try
                {
                    Process.Start(Current.FullPath);
                }
                catch (Exception e)
                {
                    logger.Error(e, "Fluent 11: failed to open the video in the default player.");
                }

                Close();
            }

            private void TogglePlay()
            {
                if (!IsVideoShown)
                {
                    return;
                }

                if (playing)
                {
                    video.Pause();
                }
                else
                {
                    video.Play();
                }

                playing = !playing;
                UpdatePlayGlyph();
                ShowBar();
            }

            private void UpdatePlayGlyph()
            {
                playPause.Content = playing ? "" : "";
            }

            private void SeekBy(double seconds)
            {
                if (!IsVideoShown)
                {
                    return;
                }

                var target = Math.Max(0, Math.Min(seek.Maximum, video.Position.TotalSeconds + seconds));
                SeekTo(target);
                UpdatePosition();
            }

            private void SeekTo(double seconds)
            {
                video.Position = TimeSpan.FromSeconds(seconds);
            }

            private void UpdatePosition()
            {
                if (dragging || !IsVideoShown)
                {
                    return;
                }

                updating = true;
                seek.Value = Math.Min(seek.Maximum, video.Position.TotalSeconds);
                updating = false;
                UpdateTime(video.Position);
            }

            private void UpdateTime(TimeSpan position)
            {
                var duration = video.NaturalDuration.HasTimeSpan ? video.NaturalDuration.TimeSpan : TimeSpan.Zero;
                time.Text = Format(position) + " / " + Format(duration);
            }

            private static string Format(TimeSpan value)
            {
                return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
            }

            private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            {
                if (updating || !IsVideoShown)
                {
                    return;
                }

                SeekTo(e.NewValue);
                UpdateTime(TimeSpan.FromSeconds(e.NewValue));
            }

            private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            {
                volume = e.NewValue;
                video.Volume = volume;
                if (volume > 0)
                {
                    video.IsMuted = false;
                }

                UpdateMuteGlyph();
            }

            private void ChangeVolume(double delta)
            {
                if (IsVideoShown)
                {
                    volumeSlider.Value = Math.Max(0, Math.Min(1, volumeSlider.Value + delta));
                }
            }

            private void ToggleMute()
            {
                if (!IsVideoShown)
                {
                    return;
                }

                if (video.IsMuted && volumeSlider.Value <= 0)
                {
                    volumeSlider.Value = 0.5;
                    return;
                }

                video.IsMuted = !video.IsMuted;
                UpdateMuteGlyph();
            }

            private void UpdateMuteGlyph()
            {
                mute.Content = video.IsMuted || volume <= 0 ? "" : volume < 0.34 ? "" : volume < 0.67 ? "" : "";
            }

            private void OnFrameClick(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                var source = e.OriginalSource as DependencyObject;
                if (IsInside(source, bar) || IsInside(source, fullPill) || IsInside(source, previous) || IsInside(source, next) || error.Visibility == Visibility.Visible)
                {
                    return;
                }

                if (Current.IsVideo)
                {
                    TogglePlay();
                }

                if (e.ClickCount == 2)
                {
                    ToggleFullScreen();
                }
            }

            private static bool IsInside(DependencyObject element, DependencyObject container)
            {
                while (element != null)
                {
                    if (element == container)
                    {
                        return true;
                    }

                    element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
                }

                return false;
            }

            private void OnKey(object sender, KeyEventArgs e)
            {
                switch (e.Key)
                {
                    case Key.Escape:
                        if (fullScreenWindow != null)
                        {
                            LeaveFullScreen();
                        }
                        else
                        {
                            Close();
                        }

                        break;
                    case Key.Space:
                    case Key.K:
                        TogglePlay();
                        break;
                    case Key.Left:
                        if (items.Count > 1)
                        {
                            Go(-1);
                        }
                        else
                        {
                            SeekBy(-SeekStep);
                        }

                        break;
                    case Key.Right:
                        if (items.Count > 1)
                        {
                            Go(1);
                        }
                        else
                        {
                            SeekBy(SeekStep);
                        }

                        break;
                    case Key.J:
                        SeekBy(-SeekStep);
                        break;
                    case Key.L:
                        SeekBy(SeekStep);
                        break;
                    case Key.Up:
                        ChangeVolume(VolumeStep);
                        break;
                    case Key.Down:
                        ChangeVolume(-VolumeStep);
                        break;
                    case Key.M:
                        ToggleMute();
                        break;
                    case Key.F:
                        ToggleFullScreen();
                        break;
                    default:
                        return;
                }

                e.Handled = true;
                if (!closed)
                {
                    ShowBar();
                }
            }

            private void OnMouseMove(object sender, MouseEventArgs e)
            {
                var position = e.GetPosition((IInputElement)sender);
                if (lastMouse.HasValue && Math.Abs(lastMouse.Value.X - position.X) < 1 && Math.Abs(lastMouse.Value.Y - position.Y) < 1)
                {
                    return;
                }

                lastMouse = position;
                ShowBar();
            }

            private void ShowBar()
            {
                if (!barVisible)
                {
                    barVisible = true;
                    foreach (var element in new UIElement[] { bar, fullPill, previous, next, previousOutside, nextOutside, below })
                    {
                        element.IsHitTestVisible = true;
                        Fade(element, 1);
                    }

                    frame.ClearValue(FrameworkElement.CursorProperty);
                }

                RestartHideTimer();
            }

            private void HideBar()
            {
                barVisible = false;
                foreach (var element in new UIElement[] { bar, fullPill, previous, next, previousOutside, nextOutside, below })
                {
                    element.IsHitTestVisible = false;
                    Fade(element, 0);
                }

                frame.Cursor = Cursors.None;
            }

            private void RestartHideTimer()
            {
                hideTimer.Stop();
                if (playing)
                {
                    hideTimer.Start();
                }
            }

            private static void Fade(UIElement element, double to)
            {
                if (SystemParameters.ClientAreaAnimation)
                {
                    element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, FadeDuration));
                }
                else
                {
                    element.BeginAnimation(UIElement.OpacityProperty, null);
                    element.Opacity = to;
                }
            }

            private void ToggleFullScreen()
            {
                if (fullScreenWindow == null)
                {
                    EnterFullScreen();
                }
                else
                {
                    LeaveFullScreen();
                }
            }

            private void EnterFullScreen()
            {
                if (closed || switching)
                {
                    return;
                }

                frame.RenderTransform = Transform.Identity;
                var topLeft = frame.PointToScreen(new Point(0, 0));
                var bottomRight = frame.PointToScreen(new Point(frame.ActualWidth, frame.ActualHeight));
                var slot = root.Children.IndexOf(frame);
                root.Children.Remove(frame);
                standIn = StillImage();
                standIn.Clip = frame.Clip;
                root.Children.Insert(slot, standIn);

                var center = new Point(window.ActualWidth / 2, window.ActualHeight / 2);
                var screen = window.PointToScreen(center);
                var device = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var place = device.Transform(screen);

                var fullWindow = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Owner = window,
                    Tag = FullScreenTag,
                    Title = window.Title,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Width = 320,
                    Height = 180,
                    Left = place.X - 160,
                    Top = place.Y - 90,
                    Content = frame
                };
                fullWindow.SetResourceReference(Control.BackgroundProperty, "VideoBackgroundBrush");
                fullWindow.SourceInitialized += (s, e) => fullWindow.WindowState = WindowState.Maximized;
                fullWindow.PreviewKeyDown += OnKey;
                fullWindow.MouseMove += OnMouseMove;
                fullWindow.SizeChanged += (s, e) => Layout();
                fullWindow.ContentRendered += (s, e) =>
                {
                    RemoveStandIn();
                    ReloadIfTooSmall();
                };
                fullWindow.Closed += (s, e) =>
                {
                    if (fullScreenWindow == fullWindow)
                    {
                        ExitFullScreen();
                    }
                };

                fullScreenWindow = fullWindow;
                UpdateChrome();
                lastMouse = null;
                fullWindow.Show();
                fullWindow.Activate();
                Keyboard.Focus(fullWindow);
                fullScreen.Content = "";
                ShowBar();

                if (SystemParameters.ClientAreaAnimation)
                {
                    fullWindow.UpdateLayout();
                    Layout();
                    fullWindow.UpdateLayout();
                    var away = new Rect(fullWindow.PointFromScreen(topLeft), fullWindow.PointFromScreen(bottomRight));
                    var to = frame.TransformToVisual(fullWindow).TransformBounds(new Rect(frame.RenderSize));
                    if (to.Width > 0 && to.Height > 0)
                    {
                        switching = true;
                        MoveFrame(away, to, () => switching = false);
                    }
                }
            }

            private void LeaveFullScreen()
            {
                if (fullScreenWindow == null || switching)
                {
                    return;
                }

                if (!SystemParameters.ClientAreaAnimation)
                {
                    ExitFullScreen();
                    return;
                }

                var topLeft = frame.PointToScreen(new Point(0, 0));
                var bottomRight = frame.PointToScreen(new Point(frame.ActualWidth, frame.ActualHeight));
                ExitFullScreen(true);
                if (closed)
                {
                    return;
                }

                layer.UpdateLayout();
                var to = new Rect((adorned.ActualWidth - frame.Width) / 2, (adorned.ActualHeight - frame.Height) / 2, frame.Width, frame.Height);
                var away = new Rect(adorned.PointFromScreen(topLeft), adorned.PointFromScreen(bottomRight));
                if (to.Width > 0 && to.Height > 0)
                {
                    switching = true;
                    MoveFrame(away, to, () => switching = false);
                }
            }

            private void ReloadIfTooSmall()
            {
                if (closed || Current.IsVideo || loadedWidth == 0 || loadedWidth >= originalWidth
                    || loadedWidth >= NeededWidth() * FillFactor(Current.Image))
                {
                    return;
                }

                LoadOriginal();
            }

            private void ExitFullScreen(bool later = false)
            {
                var fullWindow = fullScreenWindow;
                if (fullWindow == null)
                {
                    return;
                }

                fullScreenWindow = null;
                if (!closed)
                {
                    UpdateChrome();
                }

                later = later && fullWindow.IsLoaded && !closed;
                fullWindow.Content = later ? StillImage() : null;
                RemoveStandIn();
                if (!closed)
                {
                    root.Children.Insert(Math.Min(1, root.Children.Count), frame);
                    Layout();
                    fullScreen.Content = "";
                    lastMouse = null;
                }

                if (later)
                {
                    window.Dispatcher.BeginInvoke(new Action(fullWindow.Close), DispatcherPriority.ContextIdle);
                }
                else if (fullWindow.IsLoaded)
                {
                    fullWindow.Close();
                }

                window.Activate();
                if (!closed)
                {
                    Keyboard.Focus(root);
                    ShowBar();
                }
            }

            private FrameworkElement StillImage()
            {
                UIElement content;
                if (Current.IsVideo)
                {
                    content = new Border { Background = new VisualBrush(video) { Stretch = Stretch.Uniform } };
                }
                else
                {
                    content = new Image { Source = picture.Source, Stretch = picture.Stretch };
                }

                var still = new Border
                {
                    Child = content,
                    Width = frame.ActualWidth,
                    Height = frame.ActualHeight,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                still.SetResourceReference(Border.BackgroundProperty, "VideoBackgroundBrush");
                return still;
            }

            private void RemoveStandIn()
            {
                if (standIn != null)
                {
                    root.Children.Remove(standIn);
                    standIn = null;
                }
            }

            public void Close()
            {
                if (closed)
                {
                    return;
                }

                closed = true;
                current = null;
                loadGeneration++;
                adorned.SizeChanged -= OnAdornedSizeChanged;
                ExitFullScreen();
                StopVideo();

                if (previousFocus is UIElement element && element.IsVisible)
                {
                    Keyboard.Focus(previousFocus);
                }

                if (!SystemParameters.ClientAreaAnimation)
                {
                    Remove();
                    return;
                }

                root.IsHitTestVisible = false;
                var fade = new DoubleAnimation(0, FadeDuration);
                fade.Completed += (s, e) => Remove();
                root.BeginAnimation(UIElement.OpacityProperty, fade);
            }

            private void Remove()
            {
                layer.Remove(adorner);
                picture.Source = null;
                behind.Source = null;
            }

            private sealed class HostAdorner : Adorner
            {
                private readonly UIElement child;

                public HostAdorner(UIElement adornedElement, UIElement child) : base(adornedElement)
                {
                    this.child = child;
                    AddVisualChild(child);
                }

                protected override int VisualChildrenCount => 1;

                protected override Visual GetVisualChild(int index)
                {
                    return child;
                }

                protected override Size MeasureOverride(Size constraint)
                {
                    var size = AdornedElement.RenderSize;
                    child.Measure(size);
                    return size;
                }

                protected override Size ArrangeOverride(Size finalSize)
                {
                    child.Arrange(new Rect(finalSize));
                    return finalSize;
                }
            }
        }
    }
}
