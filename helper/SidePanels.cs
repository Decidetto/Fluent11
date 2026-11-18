using Playnite.SDK;
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Fluent11Helper
{
    // The Explore and Filter panels sit in the main window collapsed until first opened, so the first
    // open builds them from scratch (the Filter panel is several hundred elements). Build them while
    // Playnite is idle after startup instead, so the first open is as quick as the later ones.
    internal static class SidePanels
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static void Prewarm()
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var window = Application.Current.MainWindow;
                    Build(FindType(window, "FilterPanel"));
                    Build(FindType(window, "ExplorerPanel"));
                }
                catch (Exception e)
                {
                    logger.Error(e, "Fluent 11: failed to prepare the side panels.");
                }
            }), DispatcherPriority.ApplicationIdle);
        }

        private static void Build(FrameworkElement panel)
        {
            // Library.xaml hosts each panel in a Grid with the panel's width, inside the library row.
            if (panel == null || VisualTreeHelper.GetChildrenCount(panel) > 0
                || !(panel.Parent is FrameworkElement host) || !(host.Parent is FrameworkElement row) || row.ActualHeight <= 0)
            {
                return;
            }

            panel.Measure(new Size(host.Width > 0 ? host.Width : 300, row.ActualHeight));
        }

        private static FrameworkElement FindType(DependencyObject parent, string typeName)
        {
            if (parent == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && child.GetType().Name == typeName)
                {
                    return fe;
                }

                var found = FindType(child, typeName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
