using Playnite.SDK;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Fluent11Helper
{
    internal class GridDetailsOverlay
    {
        [DllImport("user32.dll")]
        private static extern int GetDoubleClickTime();

        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly Action<bool> reportOpen;
        private Func<bool> closeOnFilter;
        private object filterSettings;
        private Delegate filterHandler;
        private FrameworkElement overview;
        private ListBox list;
        private int openedAt;
        private const double ShadowPad = 40;
        private Border backdrop;
        private FrameworkElement gridLayer;
        private FrameworkElement shadow;
        private DispatcherTimer snapshotTimer;
        private bool snapshotted;
        private UIElement gridScrollBar;

        public GridDetailsOverlay(Action<bool> reportOpen)
        {
            this.reportOpen = reportOpen;
        }

        private static bool IsInButton(DependencyObject element, DependencyObject stop)
        {
            var current = element;
            while (current != null && current != stop)
            {
                if (current is ButtonBase)
                {
                    return true;
                }

                if (!(current is Visual))
                {
                    return false;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        public void Attach(ListBox list)
        {
            if (!(list.TemplatedParent is Control grid)
                || !(grid.Template?.FindName("PART_ControlGameView", grid) is FrameworkElement view)
                || view == overview)
            {
                return;
            }

            overview = view;
            this.list = list;
            overview.Visibility = Visibility.Collapsed;
            overview.Dispatcher.BeginInvoke(new Action(Prewarm), DispatcherPriority.ApplicationIdle);

            list.PreviewKeyDown += (s, e) =>
            {
                if (IsOpen && IsNavigationKey(e.Key))
                {
                    e.Handled = true;
                }
            };

            var pressedInList = false;
            list.PreviewMouseLeftButtonDown += (s, e) => pressedInList = true;
            list.PreviewMouseLeftButtonUp += (s, e) =>
            {
                var pressed = pressedInList;
                pressedInList = false;
                if (pressed && ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem item
                    && !IsInButton(e.OriginalSource as DependencyObject, item))
                {
                    Open();
                }
            };

            gridLayer = grid.Template.FindName("GridLayer", grid) as FrameworkElement;
            shadow = grid.Template.FindName("OverlayShadow", grid) as FrameworkElement;
            if (grid.Template.FindName("Fluent11OverlayBackdrop", grid) is Border backdrop)
            {
                this.backdrop = backdrop;
                // A snapshot is only right for the size it was taken at.
                grid.SizeChanged += (s, e) =>
                {
                    if (IsOpen && RestoreLayers())
                    {
                        ScheduleSnapshot();
                    }
                };

                backdrop.MouseLeftButtonDown += (s, e) =>
                {
                    if (unchecked(Environment.TickCount - openedAt) > GetDoubleClickTime())
                    {
                        Close();
                    }

                    e.Handled = true;
                };
            }

            grid.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if ((e.OriginalSource as FrameworkElement)?.Tag as string == "Fluent11CloseOverlay")
                {
                    Close();
                    e.Handled = true;
                }
            }));

            grid.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && IsOpen)
                {
                    Close();
                    e.Handled = true;
                }
            };

            grid.IsVisibleChanged += (s, e) =>
            {
                if (false.Equals(e.NewValue))
                {
                    Close();
                }
            };
        }

        // The details page is a large template that stays collapsed until first opened, so the
        // first open pays for building it. Build and lay it out once while idle, hidden.
        private void Prewarm()
        {
            if (overview == null || IsOpen)
            {
                return;
            }

            try
            {
                (overview as Control)?.ApplyTemplate();
                var size = new Size(Math.Max(960, list?.ActualWidth ?? 0), Math.Max(600, list?.ActualHeight ?? 0));
                overview.Visibility = Visibility.Hidden;
                overview.Measure(size);
                overview.Arrange(new Rect(size));
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to prewarm the grid details overlay.");
            }
            finally
            {
                if (overview.Visibility == Visibility.Hidden)
                {
                    overview.Visibility = Visibility.Collapsed;
                }
            }
        }

        private bool IsOpen => overview?.Visibility == Visibility.Visible;

        // Behind the card sit the grid, the dim layer and the card shadow. Even cached, blending
        // them again every frame the page scrolls makes the overlay less smooth than the full page,
        // which has none of them. Once the open animation is done, flatten all three into one
        // opaque image on the backdrop and hide the live layers until the overlay closes.
        private void ScheduleSnapshot()
        {
            if (backdrop == null || gridLayer == null)
            {
                return;
            }

            if (snapshotTimer == null)
            {
                snapshotTimer = new DispatcherTimer(DispatcherPriority.Background, overview.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
                snapshotTimer.Tick += (s, e) =>
                {
                    snapshotTimer.Stop();
                    if (IsOpen && !snapshotted)
                    {
                        HideGridScrollBar();
                        // Let layout apply the hidden scroll bar before the grid is drawn.
                        overview.Dispatcher.BeginInvoke(new Action(Snapshot), DispatcherPriority.Background);
                    }
                };
            }

            snapshotTimer.Stop();
            snapshotTimer.Start();
        }

        private void Snapshot()
        {
            if (!IsOpen || snapshotted || !backdrop.IsVisible || !gridLayer.IsVisible || backdrop.ActualWidth < 1 || backdrop.ActualHeight < 1)
            {
                return;
            }

            try
            {
                var size = new Size(backdrop.ActualWidth, backdrop.ActualHeight);
                var dpi = VisualTreeHelper.GetDpi(backdrop);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    Draw(dc, gridLayer, 0);
                    dc.DrawRectangle(backdrop.Background, null, new Rect(size));
                    if (shadow is Border shadowBorder && shadow.IsVisible)
                    {
                        DrawShadow(dc, shadowBorder, dpi.PixelsPerInchX, dpi.PixelsPerInchY);
                    }
                }

                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                bitmap.Freeze();
                var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
                brush.Freeze();

                backdrop.Background = brush;
                gridLayer.Visibility = Visibility.Hidden;
                shadow?.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
                snapshotted = true;
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to flatten the overlay background.");
                RestoreLayers();
            }
        }

        // Draws a live element into the snapshot at its place over the backdrop. The pad keeps
        // effects that spill past the element's bounds, such as the shadow blur.
        private void Draw(DrawingContext dc, FrameworkElement element, double pad)
        {
            var area = new Rect(-pad, -pad, element.ActualWidth + pad * 2, element.ActualHeight + pad * 2);
            var target = element.TransformToVisual(backdrop).TransformBounds(area);
            var brush = new VisualBrush(element) { Viewbox = area, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            dc.DrawRectangle(brush, null, target);
        }

        // A VisualBrush places an element with an effect by the effect's bounds, not the element's,
        // so copying the live shadow put its card-coloured fill down and right of the card. Rebuild
        // the shadow at a known position instead and draw that.
        private void DrawShadow(DrawingContext dc, Border source, double dpiX, double dpiY)
        {
            var size = new Size(source.ActualWidth, source.ActualHeight);
            var copy = new Border
            {
                Width = size.Width,
                Height = size.Height,
                Margin = new Thickness(ShadowPad),
                Background = source.Background,
                BorderBrush = source.BorderBrush,
                BorderThickness = source.BorderThickness,
                CornerRadius = source.CornerRadius,
                Opacity = source.Opacity,
                Effect = source.Effect,
            };
            var full = new Size(size.Width + ShadowPad * 2, size.Height + ShadowPad * 2);
            var root = new Grid { Width = full.Width, Height = full.Height };
            root.Children.Add(copy);
            root.Measure(full);
            root.Arrange(new Rect(full));

            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(full.Width * dpiX / 96), (int)Math.Ceiling(full.Height * dpiY / 96),
                dpiX, dpiY, PixelFormats.Pbgra32);
            bitmap.Render(root);
            bitmap.Freeze();
            var origin = source.TransformToVisual(backdrop).Transform(new Point(-ShadowPad, -ShadowPad));
            dc.DrawImage(bitmap, new Rect(origin, new Size(bitmap.PixelWidth * 96 / dpiX, bitmap.PixelHeight * 96 / dpiY)));
        }

        // The grid's scroll bar may be showing when the picture is taken, and would then stay frozen
        // on screen until the overlay closes. Hide it for the picture.
        private void HideGridScrollBar()
        {
            var scroller = FindListScroller(list);
            gridScrollBar = scroller?.Template?.FindName("PART_VerticalScrollBar", scroller) as UIElement;
            gridScrollBar?.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
        }

        private ScrollViewer FindListScroller(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer scroller && scroller.TemplatedParent == list)
                {
                    return scroller;
                }

                if (FindListScroller(child) is ScrollViewer found)
                {
                    return found;
                }
            }

            return null;
        }

        private bool RestoreLayers()
        {
            gridScrollBar?.ClearValue(UIElement.VisibilityProperty);
            gridScrollBar = null;
            if (!snapshotted)
            {
                return false;
            }

            snapshotted = false;
            backdrop.ClearValue(Border.BackgroundProperty);
            gridLayer.ClearValue(UIElement.VisibilityProperty);
            shadow?.ClearValue(UIElement.VisibilityProperty);
            return true;
        }

        private static bool IsNavigationKey(Key key)
        {
            switch (key)
            {
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                case Key.PageUp:
                case Key.PageDown:
                case Key.Home:
                case Key.End:
                    return true;
                default:
                    return false;
            }
        }

        private static ScrollViewer FindScroller(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer scroller && scroller.Name == "PART_ScrollViewHost")
                {
                    return scroller;
                }

                var found = FindScroller(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        public void WatchFilter(Func<bool> active)
        {
            closeOnFilter = active;
            try
            {
                var appSettings = PlayniteInternals.AppSettings;
                if (!Subscribe(Reflect.GetMember(appSettings, "FilterSettings")))
                {
                    logger.Warn("Fluent 11: Playnite FilterSettings.FilterChanged not found, search and filters won't close the game page.");
                    return;
                }

                if (appSettings is INotifyPropertyChanged notify)
                {
                    notify.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == "FilterSettings")
                        {
                            Subscribe(Reflect.GetMember(appSettings, "FilterSettings"));
                        }
                    };
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to watch Playnite filter changes.");
            }
        }

        private bool Subscribe(object settings)
        {
            var filterEvent = settings?.GetType().GetEvent("FilterChanged");
            if (filterEvent == null)
            {
                return false;
            }

            if (filterSettings != null && filterHandler != null)
            {
                filterSettings.GetType().GetEvent("FilterChanged")?.RemoveEventHandler(filterSettings, filterHandler);
            }

            filterHandler = Delegate.CreateDelegate(filterEvent.EventHandlerType, this,
                typeof(GridDetailsOverlay).GetMethod(nameof(OnFilterChanged), BindingFlags.NonPublic | BindingFlags.Instance));
            filterEvent.AddEventHandler(settings, filterHandler);
            filterSettings = settings;
            return true;
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (closeOnFilter?.Invoke() == true)
            {
                CloseFromAnyThread();
            }
        }

        private void Open()
        {
            if (overview == null)
            {
                return;
            }

            openedAt = Environment.TickCount;
            // WPF doesn't cull what's covered: every frame the overlay scrolls or fades, the grid
            // under it is redrawn too. It can't change while the overlay is up, so draw it once.
            if (list != null)
            {
                list.CacheMode = new BitmapCache();
            }

            overview.Visibility = Visibility.Visible;
            reportOpen(true);
            ScheduleSnapshot();

            overview.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsOpen)
                {
                    FindScroller(overview)?.Focus();
                }
            }), DispatcherPriority.Loaded);
        }

        public void OpenSelected()
        {
            if (list == null || !list.IsVisible || list.SelectedItem == null)
            {
                return;
            }

            list.ScrollIntoView(list.SelectedItem);
            Open();
        }

        public void Close()
        {
            if (overview != null && overview.Visibility != Visibility.Collapsed)
            {
                var hadFocus = overview.IsKeyboardFocusWithin;
                snapshotTimer?.Stop();
                RestoreLayers();
                overview.Visibility = Visibility.Collapsed;
                if (list != null)
                {
                    list.ClearValue(UIElement.CacheModeProperty);
                }

                reportOpen(false);
                if (hadFocus && list?.SelectedItem != null
                    && list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item)
                {
                    item.Focus();
                }
            }
        }

        public void CloseFromAnyThread()
        {
            overview?.Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
        }
    }
}
