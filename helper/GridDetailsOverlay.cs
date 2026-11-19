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

            if (grid.Template.FindName("Fluent11OverlayBackdrop", grid) is UIElement backdrop)
            {
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

        private bool IsOpen => overview?.Visibility == Visibility.Visible;

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
            overview.Visibility = Visibility.Visible;
            reportOpen(true);

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
                overview.Visibility = Visibility.Collapsed;
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
