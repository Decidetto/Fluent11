using System;
using Playnite.SDK;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Fluent11Helper
{
    // Rounds grid tile covers with a cheap geometry clip, so the theme can draw them as a plain Image
    // instead of an ImageBrush on a rounded Border (slow to realise when new rows scroll in).
    //
    // Also renders the tile shadow once per tile size into a frozen bitmap shared by every tile
    // (resource "Fluent11TileShadow"), instead of a DropShadowEffect per tile.
    internal static class TileClip
    {
        // Room for blur + offset. The TileShadow image's negative margin in GridViewItemTemplate.xaml
        // must be at least this plus a pixel, so the centred bitmap is never layout-clipped.
        private const double ShadowPad = 16;
        private const string ShadowKey = "Fluent11TileShadow";
        private static readonly ILogger logger = LogManager.GetLogger();

        private static Size shadowSize = Size.Empty;
        private static double shadowDpi;

        public static void Register()
        {
            EventManager.RegisterClassHandler(typeof(Grid), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler(OnGridSizeChanged));
        }

        private static void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!(sender is Grid grid) || grid.Name != "CoverHost")
            {
                return;
            }

            var size = e.NewSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                grid.Clip = null;
                return;
            }

            var radius = grid.TryFindResource("CardCornerRadius") is CornerRadius corner ? corner.TopLeft : 8;
            // Inset by half a pixel so the cover's edge runs along the centre of the theme's 1px
            // stroke border (which is drawn inset the same way), instead of peeking out past its
            // corner arcs. The stroke itself sits outside CoverHost in the template, so this clip
            // doesn't thin it.
            var clip = size.Width > 1 && size.Height > 1
                ? new RectangleGeometry(new Rect(0.5, 0.5, size.Width - 1, size.Height - 1), radius - 0.5, radius - 0.5)
                : new RectangleGeometry(new Rect(size), radius, radius);
            clip.Freeze();
            grid.Clip = clip;

            UpdateShadow(grid, size, radius);
        }

        private static void UpdateShadow(FrameworkElement host, Size size, double radius)
        {
            var scale = VisualTreeHelper.GetDpi(host).DpiScaleX;
            if (size == shadowSize && scale == shadowDpi)
            {
                return;
            }

            try
            {
                var bitmap = RenderShadow(host, size, radius, scale);
                shadowSize = size;
                shadowDpi = scale;
                Application.Current.Resources[ShadowKey] = bitmap;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Fluent 11: failed to render the tile shadow.");
                shadowSize = size;
                shadowDpi = scale;
            }
        }

        private static BitmapSource RenderShadow(FrameworkElement host, Size size, double radius, double scale)
        {
            var effect = host.TryFindResource("TileShadowEffect") is DropShadowEffect source
                ? source.Clone()
                : new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.22, Color = Colors.Black };

            // Rasterise the bare tile shape, then blur and offset its alpha here, the way
            // DropShadowEffect does. No cut-out: like the original, the shadow also lies under the tile,
            // where the cover and placeholder hide it, so nothing depends on exact pixel alignment.
            var shape = new Border
            {
                Width = size.Width,
                Height = size.Height,
                CornerRadius = new CornerRadius(radius),
                Background = Brushes.Black,
            };
            // Whole device pixels, with the shape centred exactly in them. The theme centres the image
            // on the tile, so any rounding slack has to be split evenly; leaving it all on the right and
            // bottom shifted the shadow up and left by up to half a pixel.
            var pixelWidth = (int)Math.Ceiling((size.Width + ShadowPad * 2) * scale);
            var pixelHeight = (int)Math.Ceiling((size.Height + ShadowPad * 2) * scale);
            var full = new Size(pixelWidth / scale, pixelHeight / scale);
            var root = new Grid { Width = full.Width, Height = full.Height };
            shape.HorizontalAlignment = HorizontalAlignment.Center;
            shape.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(shape);

            root.Measure(full);
            root.Arrange(new Rect(full));
            root.UpdateLayout();

            var target = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            target.Render(root);

            var stride = pixelWidth * 4;
            var pixels = new byte[stride * pixelHeight];
            target.CopyPixels(pixels, stride, 0);

            var w = pixelWidth;
            var h = pixelHeight;
            var alpha = new double[w * h];
            for (var i = 0; i < alpha.Length; i++)
            {
                alpha[i] = pixels[i * 4 + 3] / 255.0;
            }

            // DropShadowEffect's BlurRadius is roughly three standard deviations of a Gaussian.
            var sigma = Math.Max(0.5, effect.BlurRadius * scale / 3);
            var kernel = GaussianKernel(sigma);
            alpha = BlurPass(alpha, w, h, kernel, horizontal: true);
            alpha = BlurPass(alpha, w, h, kernel, horizontal: false);

            var angle = effect.Direction * Math.PI / 180;
            var offsetX = (int)Math.Round(Math.Cos(angle) * effect.ShadowDepth * scale);
            var offsetY = (int)Math.Round(-Math.Sin(angle) * effect.ShadowDepth * scale);
            var color = effect.Color;
            var opacity = effect.Opacity * color.A / 255.0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var sx = x - offsetX;
                    var sy = y - offsetY;
                    var a = sx >= 0 && sx < w && sy >= 0 && sy < h ? alpha[sy * w + sx] * opacity : 0;
                    var p = (y * w + x) * 4;
                    pixels[p] = (byte)Math.Round(color.B * a);
                    pixels[p + 1] = (byte)Math.Round(color.G * a);
                    pixels[p + 2] = (byte)Math.Round(color.R * a);
                    pixels[p + 3] = (byte)Math.Round(255 * a);
                }
            }

            var result = BitmapSource.Create(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }

        private static double[] GaussianKernel(double sigma)
        {
            var radius = (int)Math.Ceiling(sigma * 3);
            var kernel = new double[radius * 2 + 1];
            var sum = 0.0;
            for (var i = -radius; i <= radius; i++)
            {
                sum += kernel[i + radius] = Math.Exp(-(i * i) / (2 * sigma * sigma));
            }

            for (var i = 0; i < kernel.Length; i++)
            {
                kernel[i] /= sum;
            }

            return kernel;
        }

        private static double[] BlurPass(double[] source, int w, int h, double[] kernel, bool horizontal)
        {
            var radius = kernel.Length / 2;
            var result = new double[source.Length];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var sum = 0.0;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var sx = horizontal ? x + k : x;
                        var sy = horizontal ? y : y + k;
                        if (sx >= 0 && sx < w && sy >= 0 && sy < h)
                        {
                            sum += source[sy * w + sx] * kernel[k + radius];
                        }
                    }

                    result[y * w + x] = sum;
                }
            }

            return result;
        }
    }
}
