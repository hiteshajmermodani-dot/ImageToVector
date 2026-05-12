using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;

namespace ImageVectorConverter
{
    /// <summary>
    /// Main API class for converting raster images to vector XAML paths.
    /// Provides synchronous and asynchronous methods with full customization options.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class ImageVectorizer
    {
        /// <summary>
        /// Vectorizes an image with all options.
        /// </summary>
        /// <param name="bitmap">Input bitmap source</param>
        /// <param name="options">Vectorization options (color mode, size multiplier)</param>
        /// <param name="progress">Optional progress callback (percentage, message)</param>
        /// <param name="cancellationToken">Cancellation token for async operations</param>
        /// <returns>Vectorization result with XAML and geometries</returns>
        public static VectorizationResult Vectorize(
            BitmapSource bitmap,
            VectorizationOptions? options = null,
            IProgress<(int Percentage, string Message)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            options ??= VectorizationOptions.Default;

            var origW = bitmap.PixelWidth;
            var origH = bitmap.PixelHeight;

            progress?.Report((5, "Reading pixels…"));
            var pixels = ReadPixels(bitmap, origW, origH);

            progress?.Report((25, "Building color masks…"));
            var colorMasks = BuildColorMasksWithPixels(pixels, origW, origH, options.BwMode);

            var origGeoms = new ConcurrentBag<(MediaColor, Geometry)>();
            var smoothGeoms = new ConcurrentBag<(MediaColor, Geometry)>();
            var origXaml = new StringBuilder($"<Canvas Width=\"{origW}\" Height=\"{origH}\">\n");
            var smoothXaml = new StringBuilder($"<Canvas Width=\"{origW}\" Height=\"{origH}\">\n");

            var total = colorMasks.Count;

            var colorMaskList = colorMasks.ToList();
            var completed = 0;

            Parallel.For(0, colorMaskList.Count, j =>
            {
                var (quantColor, tuple) = colorMaskList[j];
                var (colorMask, pixelsInRegion) = tuple;
                cancellationToken.ThrowIfCancellationRequested();

                int rSum = 0, gSum = 0, bSum = 0, lumSum = 0;
                int count = pixelsInRegion.Count;

                foreach (var px in pixelsInRegion)
                {
                    rSum += px.r;
                    gSum += px.g;
                    bSum += px.b;
                    lumSum += (px.r * 299 + px.g * 587 + px.b * 114) / 1000;
                }

                MediaColor avgColor;

                if (options.BwMode)
                {
                    byte lum = count > 0 ? (byte)(lumSum / count) : quantColor.R;
                    avgColor = MediaColor.FromRgb(lum, lum, lum);
                }
                else
                {
                    byte r = count > 0 ? (byte)(rSum / count) : quantColor.R;
                    byte g = count > 0 ? (byte)(gSum / count) : quantColor.G;
                    byte b = count > 0 ? (byte)(bSum / count) : quantColor.B;
                    avgColor = MediaColor.FromRgb(r, g, b);
                }

                var op = BuildFilledPath(colorMask, origW, origH);

                if (!string.IsNullOrWhiteSpace(op))
                {
                    var geom = Geometry.Parse(op);
                    geom.Freeze();
                    origGeoms.Add((avgColor, geom));

                    lock (origXaml)
                    {
                        origXaml
                            .AppendLine($"  <Path Fill=\"#{avgColor.R:X2}{avgColor.G:X2}{avgColor.B:X2}\" Data=\"{op}\" />");
                    }
                }

                var sp = BuildSmoothedPath(colorMask, origW, origH);

                if (!string.IsNullOrWhiteSpace(sp))
                {
                    var geom = Geometry.Parse(sp);
                    geom.Freeze();
                    smoothGeoms.Add((avgColor, geom));

                    lock (smoothXaml)
                    {
                        smoothXaml
                            .AppendLine($"  <Path Fill=\"#{avgColor.R:X2}{avgColor.G:X2}{avgColor.B:X2}\" Data=\"{sp}\" />");
                    }
                }

                // Progress reporting
                var done = Interlocked.Increment(ref completed);

                if (progress != null && done % 5 == 0)
                {
                    var percent =
                        25 + (int)(70.0 * done / total); // Between 25% and 95%

                    progress.Report((percent,
                                     $"Vectorizing color {done} of {total}…"));
                }
            });

            origXaml.Append("</Canvas>");
            smoothXaml.Append("</Canvas>");

            progress?.Report((98, "Finishing…"));

            return new VectorizationResult
            {
                OriginalWidth = origW,
                OriginalHeight = origH,
                OriginalVectorXAML = origXaml.ToString() ?? string.Empty,
                SmoothedVectorXAML = smoothXaml.ToString() ?? string.Empty,
                OriginalGeometries = origGeoms.ToList(),
                SmoothedGeometries = smoothGeoms.ToList(),
                ColorCount = origGeoms.Count
            };
        }

        /// <summary>
        /// Asynchronously vectorizes an image.
        /// </summary>
        public static async Task<VectorizationResult> VectorizeAsync(
            BitmapSource bitmap,
            VectorizationOptions? options = null,
            IProgress<(int Percentage, string Message)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(
                                  () => Vectorize(bitmap, options, progress, cancellationToken),
                                  cancellationToken);
        }

        /// <summary>
        /// Returns a direct pixel grid as text (no cropping, no contrast stretch).
        /// Each pixel is mapped to a character:
        /// - Transparent pixels => ' '
        /// - Grayscale pixels     => █▓▒░· (dark → light)
        /// </summary>
        /// <param name="bitmap">Input bitmap source</param>
        /// <param name="bwMode">If true, outputs grayscale blocks; otherwise, just opaque/transparent</param>
        /// <returns>String representation of the image as a pixel grid</returns>
        public static string GetPixelArtText(BitmapSource bitmap, bool bwMode = false)
        {
            int w = bitmap.PixelWidth;
            int h = bitmap.PixelHeight;
            byte[] pixels = ReadPixels(bitmap, w, h);

            var sb = new StringBuilder();
            int stride = w * 4;

            // Dark → Light (white now has a visible symbol)
            char[] bwPalette = { '█', '▓', '▒', '░', '·' };

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;

                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];
                    byte a = pixels[i + 3];

                    // Transparent pixel
                    if (a < 128)
                    {
                        sb.Append(' ');

                        continue;
                    }

                    if (!bwMode)
                    {
                        sb.Append('█');

                        continue;
                    }

                    // Perceptual luminance (0–255)
                    int lum = (r * 299 + g * 587 + b * 114) / 1000;

                    // Map luminance to palette (dark → light)
                    char c =
                        lum < 64 ? bwPalette[0] :
                        lum < 128 ? bwPalette[1] :
                        lum < 192 ? bwPalette[2] :
                        lum < 224 ? bwPalette[3] :
                        bwPalette[4];

                    sb.Append(c);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static Dictionary<MediaColor, (bool[,], List<(int x, int y, byte r, byte g, byte b)>)>
            BuildColorMasksWithPixels(
                byte[] pixels, int w, int h, bool bwMode)
        {
            var colorMasks = new Dictionary<MediaColor, (bool[,], List<(int, int, byte, byte, byte)>)>();
            var stride = w * 4;

            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var i = y * stride + x * 4;
                    var b = pixels[i];
                    var g = pixels[i + 1];
                    var r = pixels[i + 2];
                    var a = pixels[i + 3];

                    if (a < 128)
                    {
                        continue;
                    }

                    int dr = 255 - r, dg = 255 - g, db = 255 - b;

                    if (dr == 0 && dg == 0 && db == 0)
                    {
                        continue;
                    }

                    var qColor = bwMode ? QuantizeGray(r, g, b) : QuantizeColor(r, g, b);

                    if (!colorMasks.TryGetValue(qColor, out var tuple))
                    {
                        tuple = (new bool[w + 1, h + 1], new List<(int, int, byte, byte, byte)>());
                        colorMasks[qColor] = tuple;
                    }

                    tuple.Item1[x, y] = true;
                    tuple.Item2.Add((x, y, r, g, b));
                }
            }

            return colorMasks;
        }

        private static MediaColor QuantizeColor(byte r, byte g, byte b)
        {
            var step = (r + g + b) / 3 >= 128 ? 16 : 32;

            return MediaColor.FromRgb(
                                      (byte)Math.Min((int)Math.Round((double)r / step) * step, 255),
                                      (byte)Math.Min((int)Math.Round((double)g / step) * step, 255),
                                      (byte)Math.Min((int)Math.Round((double)b / step) * step, 255));
        }

        private static MediaColor QuantizeGray(byte r, byte g, byte b)
        {
            var lum = (r * 299 + g * 587 + b * 114) / 1000;
            var step = lum >= 128 ? 16 : 32;
            var ql = (byte)Math.Min((int)Math.Round((double)lum / step) * step, 255);

            return MediaColor.FromRgb(ql, ql, ql);
        }

        private static string BuildFilledPath(bool[,] colorMask, int w, int h)
        {
            var sb = new StringBuilder();

            for (var y = 0; y < h; y++)
            {
                var x = 0;

                while (x < w)
                {
                    if (!colorMask[x, y])
                    {
                        x++;

                        continue;
                    }

                    var start = x;

                    while (x < w && colorMask[x, y])
                    {
                        x++;
                    }

                    sb.Append($"M{start:F2},{y:F2} H{x:F2} V{y + 1:F2} H{start:F2} Z ");
                }
            }

            return sb.ToString();
        }

        private static string BuildSmoothedPath(bool[,] colorMask, int w, int h)
        {
            var outEdges = new Dictionary<(int, int), List<(int, int)>>();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!colorMask[x, y])
                    {
                        continue;
                    }

                    if (y == 0 || !colorMask[x, y - 1])
                    {
                        AddEdge((x, y), (x + 1, y));
                    }

                    if (x == w - 1 || !colorMask[x + 1, y])
                    {
                        AddEdge((x + 1, y), (x + 1, y + 1));
                    }

                    if (y == h - 1 || !colorMask[x, y + 1])
                    {
                        AddEdge((x + 1, y + 1), (x, y + 1));
                    }

                    if (x == 0 || !colorMask[x - 1, y])
                    {
                        AddEdge((x, y + 1), (x, y));
                    }
                }
            }

            var usedEdges = new HashSet<((int, int), (int, int))>();
            var sb = new StringBuilder();

            foreach (var startVertex in outEdges.Keys.OrderBy(v => v.Item2).ThenBy(v => v.Item1).ToList())
            {
                if (!outEdges.TryGetValue(startVertex, out var startNeighbors))
                {
                    continue;
                }

                foreach (var firstNext in startNeighbors.ToList())
                {
                    if (usedEdges.Contains((startVertex, firstNext)))
                    {
                        continue;
                    }

                    var polygon = WalkPolygon(startVertex, firstNext, outEdges, usedEdges);

                    if (polygon.Count < 3 || PolygonArea(polygon) < 0.5)
                    {
                        continue;
                    }

                    var pts = polygon.Select(p => ((double)p.Item1, (double)p.Item2)).ToList();
                    var smoothed = ChaikinSmooth(pts, 3);

                    sb.Append($"M{smoothed[0].Item1:F2},{smoothed[0].Item2:F2} ");

                    for (var i = 1; i < smoothed.Count; i++)
                    {
                        sb.Append($"L{smoothed[i].Item1:F2},{smoothed[i].Item2:F2} ");
                    }

                    sb.Append("Z ");
                }
            }

            return sb.ToString();

            void AddEdge((int, int) from, (int, int) to)
            {
                if (!outEdges.TryGetValue(from, out var list))
                {
                    outEdges[from] = list = new List<(int, int)>();
                }

                list.Add(to);
            }
        }

        private static List<(int, int)> WalkPolygon(
            (int, int) start, (int, int) firstNext,
            Dictionary<(int, int), List<(int, int)>> outEdges,
            HashSet<((int, int), (int, int))> usedEdges)
        {
            var polygon = new List<(int, int)>();
            var current = start;
            var next = firstNext;
            var prev = start;

            for (var safety = 0; safety < 2_000_000; safety++)
            {
                if (usedEdges.Contains((current, next)))
                {
                    break;
                }

                usedEdges.Add((current, next));
                polygon.Add(current);

                if (next == start)
                {
                    break;
                }

                prev = current;
                current = next;

                if (!outEdges.TryGetValue(current, out var neighbors))
                {
                    break;
                }

                next = PickNextEdge(neighbors, current, prev, usedEdges);

                if (next == (-1, -1))
                {
                    break;
                }
            }

            return polygon;
        }

        private static (int, int) PickNextEdge(
            List<(int, int)> candidates, (int, int) current, (int, int) prev,
            HashSet<((int, int), (int, int))> usedEdges)
        {
            var inDx = current.Item1 - prev.Item1;
            var inDy = current.Item2 - prev.Item2;

            var unused = candidates.Where(c => !usedEdges.Contains((current, c))).ToList();

            if (unused.Count == 0)
            {
                return (-1, -1);
            }

            if (unused.Count == 1)
            {
                return unused[0];
            }

            return unused.MinBy(n =>
            {
                int outDx = n.Item1 - current.Item1;
                int outDy = n.Item2 - current.Item2;

                return Math.Atan2(inDx * outDy - inDy * outDx, inDx * outDx + inDy * outDy);
            })!;
        }

        private static double PolygonArea(List<(int, int)> pts)
        {
            double area = 0;
            var n = pts.Count;

            for (var i = 0; i < n; i++)
            {
                var (x1, y1) = pts[i];
                var (x2, y2) = pts[(i + 1) % n];
                area += (double)x1 * y2 - (double)x2 * y1;
            }

            return Math.Abs(area) * 0.5;
        }

        private static List<(double, double)> ChaikinSmooth(List<(double, double)> pts, int iterations)
        {
            for (var iter = 0; iter < iterations; iter++)
            {
                var next = new List<(double, double)>(pts.Count * 2);
                var n = pts.Count;

                for (var j = 0; j < n; j++)
                {
                    var p0 = pts[j];
                    var p1 = pts[(j + 1) % n];
                    next.Add((0.75 * p0.Item1 + 0.25 * p1.Item1, 0.75 * p0.Item2 + 0.25 * p1.Item2));
                    next.Add((0.25 * p0.Item1 + 0.75 * p1.Item1, 0.25 * p0.Item2 + 0.75 * p1.Item2));
                }

                pts = next;
            }

            return pts;
        }

        // ===== Helper =====

        private static byte[] ReadPixels(BitmapSource bitmap, int w, int h)
        {
            if (bitmap.Format != PixelFormats.Bgra32)
            {
                bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            }

            int stride = w * 4;
            var pixels = new byte[h * stride];
            bitmap.CopyPixels(pixels, stride, 0);

            return pixels;
        }
    }

    /// <summary>
    /// Vectorization options and configuration.
    /// </summary>
    public class VectorizationOptions
    {
        /// <summary>
        /// Convert to black and white instead of color.
        /// </summary>
        public bool BwMode { get; set; } = false;

        /// <summary>
        /// Size multiplier for output (1.0 = 100%).
        /// </summary>
        public double SizeMultiplier { get; set; } = 1.0;

        /// <summary>
        /// Gets default options.
        /// </summary>
        public static VectorizationOptions Default => new();

        /// <summary>
        /// Gets black and white mode options.
        /// </summary>
        public static VectorizationOptions BlackAndWhite => new() { BwMode = true };

        /// <summary>
        /// Creates options with specified mode and size.
        /// </summary>
        /// <param name="bwMode">If true, converts image to black and white instead of color.</param>
        /// <param name="sizeMultiplier">Size multiplier for output (1.0 = 100%).</param>
        /// <returns>A new VectorizationOptions instance with the specified settings.</returns>
        public static VectorizationOptions Create(bool bwMode = false, double sizeMultiplier = 1.0)
        {
            return new VectorizationOptions { BwMode = bwMode, SizeMultiplier = sizeMultiplier };
        }
    }

    /// <summary>
    /// Result of image vectorization containing both original and smoothed vectors.
    /// </summary>
    public class VectorizationResult
    {
        /// <summary>
        /// Original image width in pixels.
        /// </summary>
        public int OriginalWidth { get; init; }

        /// <summary>
        /// Original image height in pixels.
        /// </summary>
        public int OriginalHeight { get; init; }

        /// <summary>
        /// Original vector XAML (pixel-accurate rectangles).
        /// </summary>
        public string OriginalVectorXAML { get; init; } = string.Empty;

        /// <summary>
        /// Smoothed vector XAML (contour-traced + Chaikin smoothing).
        /// </summary>
        public string SmoothedVectorXAML { get; init; } = string.Empty;

        /// <summary>
        /// Original vector geometries for direct WPF use.
        /// </summary>
        public List<(MediaColor Color, Geometry Geometry)> OriginalGeometries { get; init; } = new();

        /// <summary>
        /// Smoothed vector geometries for direct WPF use.
        /// </summary>
        public List<(MediaColor Color, Geometry Geometry)> SmoothedGeometries { get; init; } = new();

        /// <summary>
        /// Number of colors detected in the image.
        /// </summary>
        public int ColorCount { get; init; }
    }
}