using Playnite.SDK;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Fluent11Helper
{
    internal static class AutoColumns
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public const int MinColumns = 2;
        public const int MaxColumns = 12;
        private const double MinWidth = 100;
        private const double MaxWidth = 500;

        private static Fluent11HelperSettingsViewModel model;
        private static Fluent11HelperSettings watched;
        private static object appSettings;
        private static WeakReference<Panel> lastPanel;
        private static WeakReference<ListBox> lastList;
        private static DispatcherTimer timer;
        private static Panel waitingPanel;
        private static WeakReference<ListBox> hookedList;
        private static WeakReference<ItemsPresenter> hookedPresenter;
        private static double baseListWidth;
        private static double baseItemsWidth;
        private static int baseColumns;
        private static int baseMargin;
        // List width minus tile panel width (padding, scrollbar gutter) at the last unscaled layout.
        private static double baseChrome = -1;
        // Slot size and column count of the layout last set (predicted, then confirmed once settled).
        private static Size shownSlot = Size.Empty;
        private static int shownColumns;
        // Window width at the last unscaled layout, to tell a side panel toggle from a window resize.
        private static double baseWindowWidth;
        // Set while a side panel is closing, so its re-layout runs right after the next frame.
        private static bool panelClosing;
        private static readonly TimeSpan DragDelay = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan PanelDelay = TimeSpan.FromMilliseconds(300);

        public static void Register(Fluent11HelperSettingsViewModel settingsModel)
        {
            try
            {
                appSettings = PlayniteInternals.AppSettings;
                if (appSettings?.GetType().GetProperty("GridItemWidth")?.CanWrite != true)
                {
                    logger.Warn("Fluent 11: Playnite GridItemWidth not writable, Fit window is not available.");
                    return;
                }

                model = settingsModel;
                model.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(Fluent11HelperSettingsViewModel.Settings))
                    {
                        Watch();
                        Schedule();
                    }
                };
                Watch();
                if (appSettings is INotifyPropertyChanged notify)
                {
                    notify.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == "GridItemSpacing" || e.PropertyName == "GridItemWidth")
                        {
                            Schedule();
                        }
                        else if (e.PropertyName == "ExplorerPanelVisible" || e.PropertyName == "FilterPanelVisible")
                        {
                            PinForPanel(!(Reflect.GetMember(s, e.PropertyName) is bool open && open));
                        }
                    };
                }

                EventManager.RegisterClassHandler(typeof(Panel), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnPanelSizeChanged));
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to set up Fit window.");
            }
        }

        private static void Watch()
        {
            if (watched != null)
            {
                watched.PropertyChanged -= OnSettingChanged;
            }

            watched = model.Settings;
            watched.PropertyChanged += OnSettingChanged;
        }

        private static void OnSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Fluent11HelperSettings.GridColumns) || e.PropertyName == nameof(Fluent11HelperSettings.GridFitWindow))
            {
                Schedule();
            }
        }

        private static void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged || e.NewSize.Width <= 0 || sender.GetType().Name != "VirtualizingWrapPanel" || !(sender is Panel panel) || !panel.IsItemsHost)
            {
                return;
            }

            var owner = (panel.TemplatedParent as ItemsPresenter)?.TemplatedParent;
            var list = owner is GroupItem group ? ItemsControl.ItemsControlFromItemContainer(group) as ListBox : owner as ListBox;
            if (list == null || list.Name != "PART_ListGames" || list.TemplatedParent?.GetType().Name != "LibraryGridView" || (owner == list && list.IsGrouping))
            {
                return;
            }

            lastPanel = new WeakReference<Panel>(panel);
            lastList = new WeakReference<ListBox>(list);
            HookList(list);
            Schedule();
        }

        private static void HookList(ListBox list)
        {
            if (hookedList != null && hookedList.TryGetTarget(out var hooked))
            {
                if (hooked == list)
                {
                    return;
                }

                hooked.SizeChanged -= OnListSizeChanged;
            }

            hookedList = new WeakReference<ListBox>(list);
            hookedPresenter = null;
            baseListWidth = 0;
            list.SizeChanged += OnListSizeChanged;
            HookWindow(list);
        }

        private const int WmEnterSizeMove = 0x0231;
        private const int WmExitSizeMove = 0x0232;
        private static HwndSource hookedSource;
        private static bool sizing;

        // Only a drag-resize needs the cheap scaled preview; maximise, restore and snapping change the
        // size once, so those get the real layout straight away.
        private static void HookWindow(ListBox list)
        {
            var window = Window.GetWindow(list);
            var source = window == null ? null : PresentationSource.FromVisual(window) as HwndSource;
            if (source == null || source == hookedSource)
            {
                return;
            }

            hookedSource?.RemoveHook(OnWindowMessage);
            hookedSource = source;
            sizing = false;
            source.AddHook(OnWindowMessage);
        }

        private static IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmEnterSizeMove)
            {
                sizing = true;
            }
            else if (msg == WmExitSizeMove && sizing)
            {
                sizing = false;
                if (IsScaled())
                {
                    timer?.Stop();
                    Apply();
                }
            }

            return IntPtr.Zero;
        }

        private static void OnListSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged || !(sender is ListBox list) || model?.Settings?.GridFitWindow != true || !list.IsVisible
                || baseListWidth <= 0 || baseItemsWidth <= 0)
            {
                return;
            }

            if (!sizing && !IsScaled() && !PanelToggled(list))
            {
                if (WindowStatePending(list))
                {
                    // Maximise/restore: the new window size is laid out before WPF updates WindowState,
                    // and the window style's maximised padding then changes the width once more in the
                    // next layout pass (before anything is drawn). Fit to that final width instead of
                    // laying out twice; the timer is only a fallback if no second size change comes.
                    Schedule();
                    return;
                }

                // SizeChanged runs after the layout pass, before anything is drawn, so laying out
                // again here shows only the final grid.
                timer?.Stop();
                Apply();
                return;
            }

            var presenter = HostPresenter(list);
            var margins = (double)baseColumns * baseMargin;
            var scale = (baseItemsWidth + list.ActualWidth - baseListWidth - margins) / (baseItemsWidth - margins);
            if (presenter == null || baseItemsWidth - margins <= 0 || scale <= 0.1)
            {
                return;
            }

            presenter.Width = baseItemsWidth;
            if (presenter.LayoutTransform is ScaleTransform transform && !transform.IsFrozen)
            {
                transform.ScaleX = scale;
                transform.ScaleY = scale;
            }
            else
            {
                presenter.LayoutTransform = new ScaleTransform(scale, scale);
            }

            if (!sizing && panelClosing)
            {
                panelClosing = false;
                ApplyAfterNextFrame();
                return;
            }

            Schedule(sizing ? DragDelay : PanelDelay);
        }

        // A closing panel has no animation to protect, and the user looks at the grid again, so lay it
        // out as soon as the frame without the panel is on screen. Subscribing here (inside a layout
        // pass) means the handler runs at the next frame, after the scaled grid has been drawn.
        private static void ApplyAfterNextFrame()
        {
            EventHandler rendering = null;
            rendering = (s, e) =>
            {
                CompositionTarget.Rendering -= rendering;
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    timer?.Stop();
                    Apply();
                }), DispatcherPriority.Normal);
            };
            CompositionTarget.Rendering += rendering;
        }

        // A side panel is about to open or close. Pin the tiles at their current width before WPF's next
        // layout pass: otherwise that pass first lays the grid out at the new width with the old tile size
        // (other columns, other games realized, covers loaded), and only then does the size change switch
        // to the scaled preview. The size change then just sets the scale; the real re-layout follows.
        private static void PinForPanel(bool closing)
        {
            panelClosing = false;
            if (model?.Settings?.GridFitWindow != true || IsScaled() || baseItemsWidth <= 0
                || hookedList == null || !hookedList.TryGetTarget(out var list) || !list.IsVisible
                || !(HostPresenter(list) is ItemsPresenter presenter))
            {
                return;
            }

            presenter.Width = baseItemsWidth;
            presenter.LayoutTransform = new ScaleTransform(1, 1);
            panelClosing = closing;
            // Unpins again if the grid width doesn't change after all.
            Schedule(PanelDelay);
        }

        // The window kept its width, so a side panel (Explore, Filter, details) opened or closed. Show
        // the grid scaled first so the panel appears straight away; the user looks at the panel for the
        // first moments anyway, and the real re-layout (which reloads every cover) follows shortly.
        private static bool PanelToggled(ListBox list)
        {
            var window = Window.GetWindow(list);
            return window != null && baseWindowWidth > 0 && Math.Abs(window.ActualWidth - baseWindowWidth) < 0.5;
        }

        // The main window style pads the window while maximised (MainWindowStyle.xaml, WindowBorder),
        // but that padding only follows WindowState in the next layout pass.
        private static bool WindowStatePending(ListBox list)
        {
            var window = Window.GetWindow(list);
            return window?.Template?.FindName("WindowBorder", window) is Border border
                && border.Padding.Left > 0 != (window.WindowState == WindowState.Maximized);
        }

        private static ItemsPresenter HostPresenter(ListBox list)
        {
            if (hookedPresenter != null && hookedPresenter.TryGetTarget(out var cached) && cached.TemplatedParent == list)
            {
                return cached;
            }

            var found = FindPresenter(list, list);
            hookedPresenter = found == null ? null : new WeakReference<ItemsPresenter>(found);
            return found;
        }

        private static ItemsPresenter FindPresenter(DependencyObject parent, ListBox list)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ItemsPresenter presenter && presenter.TemplatedParent == list)
                {
                    return presenter;
                }

                var found = FindPresenter(child, list);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static bool IsScaled()
        {
            return hookedPresenter != null && hookedPresenter.TryGetTarget(out var presenter)
                && presenter.ReadLocalValue(FrameworkElement.WidthProperty) != DependencyProperty.UnsetValue;
        }

        private static bool ResetScale()
        {
            if (hookedPresenter == null || !hookedPresenter.TryGetTarget(out var presenter) || presenter.ReadLocalValue(FrameworkElement.WidthProperty) == DependencyProperty.UnsetValue)
            {
                return false;
            }

            presenter.ClearValue(FrameworkElement.WidthProperty);
            presenter.ClearValue(FrameworkElement.LayoutTransformProperty);
            return true;
        }

        private static void Schedule()
        {
            Schedule(DragDelay);
        }

        private static void Schedule(TimeSpan delay)
        {
            if (timer == null)
            {
                // Normal priority: at the default (Background) the tick can wait behind Playnite's own
                // background work for a second or more, leaving the grid scaled for that long.
                timer = new DispatcherTimer(DispatcherPriority.Normal);
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    Apply();
                };
            }

            timer.Stop();
            timer.Interval = delay;
            timer.Start();
        }

        private static void Apply()
        {
            try
            {
                // Never force layout here (UpdateLayout): everything below is set up so that WPF's own
                // next layout pass shows the new grid in one go, without an in-between layout.
                var settings = model?.Settings;
                var scaled = IsScaled();
                if (settings == null || !settings.GridFitWindow || lastPanel == null || !lastPanel.TryGetTarget(out var panel))
                {
                    ResetScale();
                    return;
                }

                // After a live resize the panel is still laid out at the old width and scaled. Work out
                // the unscaled width from the list instead of resetting first, so the scale and the new
                // tile width are swapped in one layout pass and the grid doesn't jump in between.
                var list = lastList != null && lastList.TryGetTarget(out var l) ? l : null;
                if (scaled && (list == null || baseChrome < 0))
                {
                    ResetScale();
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(Apply), DispatcherPriority.Loaded);
                    return;
                }

                // Right after a size change the panel can still be at an in-between width, so go by
                // the list (which is already final) whenever the chrome around the panel is known.
                var available = list != null && baseChrome >= 0 ? list.ActualWidth - baseChrome : panel.ActualWidth;

                if (!panel.IsVisible)
                {
                    WaitUntilVisible(panel);
                    return;
                }

                // Each tile reserves the item spacing (ItemSpacingMargin, half on each side) plus
                // Playnite's own GridItemMargin. Leaving either out makes the tiles a bit too wide,
                // the panel drops a column and spreads the leftover width into wide column gaps.
                var spacing = Reflect.GetMember(appSettings, "GridItemSpacing") is int value ? value : 0;
                var itemMargin = Reflect.GetMember(appSettings, "GridItemMargin");
                var itemMarginX = itemMargin is Thickness thickness ? thickness.Left + thickness.Right
                    : itemMargin is IConvertible ? Convert.ToDouble(itemMargin) * 2 : 0;
                baseMargin = spacing / 2 * 2 + (int)Math.Ceiling(itemMarginX);
                var width = Compute(available, settings.GridColumns, baseMargin, out baseColumns);
                var current = Convert.ToDouble(Reflect.GetMember(appSettings, "GridItemWidth"));
                if (Math.Abs(current - width) >= 1)
                {
                    // Start from the layout last set, not from the panel: if this runs again before the
                    // previous change has settled, the panel and its cache still describe the one before.
                    var oldSize = shownSlot.IsEmpty ? CachedItemSize(panel) : shownSlot;
                    if (!IsTileSlot(oldSize))
                    {
                        oldSize = Size.Empty;
                    }

                    var oldColumns = shownColumns > 0 ? shownColumns
                        : oldSize.IsEmpty || oldSize.Width <= 0 ? 0 : Math.Max(1, (int)Math.Floor(panel.ActualWidth / oldSize.Width));
                    // Predict the new slot instead of measuring a tile: right now a tile can still report
                    // its old size, and forcing a measure would add a layout pass. The cover scales with the
                    // tile width; the spacing around it stays the same.
                    var oldTileWidth = oldSize.Width - baseMargin;
                    var newSlot = oldSize.IsEmpty || oldTileWidth <= 0 ? Size.Empty
                        : new Size(width + baseMargin, baseMargin + (oldSize.Height - baseMargin) * width / oldTileWidth);
                    appSettings.GetType().GetProperty("GridItemWidth").SetValue(appSettings, width);
                    ResetScale();
                    RemeasurePanels(newSlot);
                    if (!newSlot.IsEmpty)
                    {
                        KeepScrollRow(list, oldSize, oldColumns, newSlot.Height, baseColumns);
                    }

                    shownSlot = newSlot;
                    shownColumns = newSlot.IsEmpty ? 0 : baseColumns;
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(RememberWidths), DispatcherPriority.Loaded);
                    return;
                }

                if (ResetScale())
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(RememberWidths), DispatcherPriority.Loaded);
                    return;
                }

                RememberWidths();
            }
            catch (Exception e)
            {
                logger.Error(e, "Fluent 11: failed to fit the grid to the window.");
            }
        }

        // The wrap panel sizes every slot from item 0 (sizeOfFirstItem). When that cache is empty and
        // the list is scrolled down, it measures item 0's container, which is then a recycled one off
        // screen: its tile never re-measured after the width change, so it reports the size from an
        // older, wider window and every slot ends up huge. Use a tile that is on screen instead, as
        // last measured by the panel itself.
        // Only trust a tile that is already laid out at the current tile width: the panel can also hold
        // containers with no game in them (recycled or kept for focus), which measure as just their
        // spacing. Caching such a size makes every slot a few pixels big, the panel then realizes the
        // whole library at once.
        private static Size OnScreenTileSize(Panel panel)
        {
            var expected = Convert.ToDouble(Reflect.GetMember(appSettings, "GridItemWidth")) + baseMargin;
            foreach (UIElement tile in panel.Children)
            {
                var size = tile.DesiredSize;
                if (Math.Abs(size.Width - expected) < 1.5 && size.Height > baseMargin + 1)
                {
                    return size;
                }
            }

            return Size.Empty;
        }

        private static bool IsTileSlot(Size size)
        {
            return !size.IsEmpty && size.Width > baseMargin + 1 && size.Height > baseMargin + 1;
        }

        private static Size CachedItemSize(Panel panel)
        {
            return SizeOfFirstItemField(panel.GetType())?.GetValue(panel) is Size size ? size : Size.Empty;
        }

        // The scroll offset is in pixels, so when the rows change height (or the column count changes,
        // which changes the number of rows) the same offset lands on a different row. Find the first
        // game in the top row and keep its row at the top, at the same fraction of the row.
        private static void KeepScrollRow(ListBox list, Size oldSize, int oldColumns, double newRowHeight, int newColumns)
        {
            if (list == null || list.IsGrouping || oldColumns <= 0 || newColumns <= 0 || oldSize.Height <= 0 || newRowHeight <= 0
                || !(FindScrollViewer(list) is ScrollViewer viewer) || viewer.VerticalOffset <= 0)
            {
                return;
            }

            var row = viewer.VerticalOffset / oldSize.Height;
            var topRow = Math.Floor(row);
            var firstItem = (int)topRow * oldColumns;
            var offset = (firstItem / newColumns + (row - topRow)) * newRowHeight;
            if (Math.Abs(offset - viewer.VerticalOffset) >= 0.5)
            {
                viewer.ScrollToVerticalOffset(offset);
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollViewer viewer)
                {
                    return viewer;
                }

                var found = FindScrollViewer(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        // Tile bindings can also settle a moment after GridItemWidth changes, so once layout has
        // settled, re-check the cached slot against a tile on screen.
        private static void FixStaleItemSize()
        {
            if (lastList == null || !lastList.TryGetTarget(out var list))
            {
                return;
            }

            foreach (var panel in TilePanels(list))
            {
                var field = SizeOfFirstItemField(panel.GetType());
                if (field == null)
                {
                    return;
                }

                var cached = CachedItemSize(panel);
                var size = OnScreenTileSize(panel);
                if (!size.IsEmpty && (cached.IsEmpty || Math.Abs(cached.Width - size.Width) >= 1 || Math.Abs(cached.Height - size.Height) >= 1))
                {
                    field.SetValue(panel, size);
                    panel.InvalidateMeasure();
                }
                else if (size.IsEmpty && !cached.IsEmpty && !IsTileSlot(cached))
                {
                    // The panel cached an empty container's size itself; let it measure afresh.
                    field.SetValue(panel, Size.Empty);
                    panel.InvalidateMeasure();
                }
            }
        }

        private static void RememberWidths()
        {
            if (IsScaled())
            {
                // A new live resize started before this ran; its own Apply will remember.
                return;
            }

            FixStaleItemSize();
            baseListWidth = 0;
            if (lastList == null || !lastList.TryGetTarget(out var list) || !(HostPresenter(list) is ItemsPresenter presenter))
            {
                return;
            }

            baseListWidth = list.ActualWidth;
            baseItemsWidth = presenter.ActualWidth;
            baseWindowWidth = Window.GetWindow(list)?.ActualWidth ?? 0;
            Panel panel = null;
            lastPanel?.TryGetTarget(out panel);
            var panelWidth = panel != null ? panel.ActualWidth : 0;
            baseChrome = panelWidth > 0 ? list.ActualWidth - panelWidth : -1;
            shownSlot = panel != null ? CachedItemSize(panel) : Size.Empty;
            if (!IsTileSlot(shownSlot))
            {
                shownSlot = Size.Empty;
            }

            shownColumns = shownSlot.IsEmpty || shownSlot.Width <= 0 ? 0 : Math.Max(1, (int)Math.Floor(panelWidth / shownSlot.Width));
        }

        private static void WaitUntilVisible(Panel panel)
        {
            if (waitingPanel != null)
            {
                waitingPanel.IsVisibleChanged -= OnWaitingPanelVisible;
            }

            waitingPanel = panel;
            panel.IsVisibleChanged += OnWaitingPanelVisible;
        }

        private static void OnWaitingPanelVisible(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(e.NewValue is bool visible) || !visible || !(sender is Panel panel))
            {
                return;
            }

            panel.IsVisibleChanged -= OnWaitingPanelVisible;
            waitingPanel = null;
            panel.Dispatcher.BeginInvoke(new Action(Apply), DispatcherPriority.Render);
        }

        // Hand the panel the new slot size up front; the settle check corrects it if the prediction
        // was off (or unknown, in which case it starts from the on-screen tile's last size).
        private static void RemeasurePanels(Size slot)
        {
            if (lastList == null || !lastList.TryGetTarget(out var list))
            {
                return;
            }

            foreach (var panel in TilePanels(list))
            {
                var field = SizeOfFirstItemField(panel.GetType());
                if (field == null)
                {
                    return;
                }

                field.SetValue(panel, slot.IsEmpty ? OnScreenTileSize(panel) : slot);
                panel.InvalidateMeasure();
            }
        }

        private static System.Collections.Generic.IEnumerable<Panel> TilePanels(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Panel panel && panel.IsItemsHost && panel.GetType().Name == "VirtualizingWrapPanel")
                {
                    yield return panel;
                    continue;
                }

                foreach (var found in TilePanels(child))
                {
                    yield return found;
                }
            }
        }

        internal static FieldInfo SizeOfFirstItemField(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var field = current.GetField("sizeOfFirstItem", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null && field.FieldType == typeof(Size))
                {
                    return field;
                }
            }

            return null;
        }

        private static double Compute(double available, int targetColumns, int margin, out int resultColumns)
        {
            double Width(int n) => Math.Floor(available / n) - margin - 1;

            var columns = Math.Max(MinColumns, Math.Min(MaxColumns, targetColumns));
            while (columns > 1 && Width(columns) < MinWidth)
            {
                columns--;
            }

            while (Width(columns) > MaxWidth)
            {
                columns++;
            }

            resultColumns = columns;
            return Math.Max(60, Math.Min(700, Width(columns)));
        }
    }
}
