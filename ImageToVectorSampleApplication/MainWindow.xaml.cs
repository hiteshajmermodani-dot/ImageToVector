using ImageVectorConverter;
using Microsoft.Win32;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageToVector
{
    [SupportedOSPlatform("windows")]
    public partial class MainWindow : Window
    {
        private volatile bool _allowBackgroundDraw = false;
        private double _baseFitScale = 1.0;

        // Result cache — switching Color↔B&W reuses the last computed result
        private VectorizationResult? _bwCache;
        private bool _bwMode = false;
        private VectorizationResult? _colorCache;
        private CancellationTokenSource? _cts;
        private BitmapSource? _lastBitmap;
        private (BitmapSource Bitmap, bool BwMode)? _lastKey;
        private VectorizationResult? _lastResult;
        private int _origWidth, _origHeight;
        private const double Tolerance = 1e-6;

        public MainWindow()
        {
            InitializeComponent();

            // Update size label while dragging, but only redraw when drag completes
            SizeSlider.AddHandler(
                                  Thumb.DragCompletedEvent,
                                  new DragCompletedEventHandler(async (_, _) =>
                                                                {
                                                                    if (_lastBitmap != null && _lastResult != null)
                                                                    {
                                                                        await RedrawPreviewsAsync();
                                                                    }
                                                                }));

            // Set up drag and drop
            AllowDrop = true;
            DragEnter += Window_DragEnter;
            DragOver += Window_DragOver;
            Drop += Window_Drop;
        }

        // -------------------------------------------------
        // LOAD / PASTE IMAGE
        // -------------------------------------------------

        private async void LoadImage_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
                                 {
                                     Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp"
                                 };

            if (dlg.ShowDialog() == true)
            {
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(dlg.FileName);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                ResetControls();
                OriginalImage.Source = bmp;
                _lastBitmap = bmp;
                await Vectorize(bmp);
                //await ShowAllPixelArtText(bmp);
            }
        }

        private async void PasteImage_Click(object? sender, RoutedEventArgs? e)
        {
            if (Clipboard.ContainsImage())
            {
                var raw = Clipboard.GetImage();

                if (raw == null)
                {
                    return;
                }

                // Clipboard bitmaps are InteropBitmaps — render them to a frozen Bgra32 copy
                // so they can be safely passed to background threads
                int w = raw.PixelWidth, h = raw.PixelHeight;
                var stride = w * 4;
                var px = new byte[h * stride];

                var cvt = raw.Format == PixelFormats.Bgra32
                              ? raw
                              : new FormatConvertedBitmap(raw, PixelFormats.Bgra32, null, 0);

                cvt.CopyPixels(px, stride, 0);

                var bmp = BitmapSource.Create(w, h, raw.DpiX, raw.DpiY,
                                              PixelFormats.Bgra32, null, px, stride);

                bmp.Freeze();

                ResetControls();
                OriginalImage.Source = bmp;
                _lastBitmap = bmp;
                await Vectorize(bmp);
                //await ShowAllPixelArtText(bmp);
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                PasteImage_Click(null, null);
            }
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;

            // Try file drop first
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);

                if (files != null && files.Length > 0)
                {
                    LoadImageFromPath(files[0]);

                    return;
                }
            }

            // Try image data from clipboard or other sources
            if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                var text = (string)e.Data.GetData(DataFormats.UnicodeText);

                if (!string.IsNullOrEmpty(text) && text.StartsWith("data:image/"))
                {
                    LoadImageFromBase64DataUri(text);
                }
            }
        }

        private async void LoadImageFromPath(string filePath)
        {
            if (!File.Exists(filePath))
            {
                MessageBox.Show("File not found.", "Error");

                return;
            }

            try
            {
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(filePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                ResetControls();
                OriginalImage.Source = bmp;
                _lastBitmap = bmp;
                await Vectorize(bmp);
                //await ShowAllPixelArtText(bmp);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load image: {ex.Message}", "Error");
            }
        }

        private async void LoadImageFromBase64DataUri(string dataUri)
        {
            try
            {
                // Extract base64 string from data URI (format: data:image/png;base64,...)
                var base64 = dataUri.Split(',')[1];
                var imageData = Convert.FromBase64String(base64);

                using (var mem = new MemoryStream(imageData))
                {
                    BitmapImage bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = mem;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();

                    ResetControls();
                    OriginalImage.Source = bmp;
                    _lastBitmap = bmp;
                    await Vectorize(bmp);
                    //await ShowAllPixelArtText(bmp);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load image from data URI: {ex.Message}", "Error");
            }
        }

        private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SizeLabel != null)
            {
                // Show effective percentage (fit scale + slider adjustment)
                double effectivePercent = (int)(_baseFitScale * SizeSlider.Value);
                SizeLabel.Text = $"{effectivePercent} %";
            }

            // Note: Preview is only redrawn when drag is completed (see Thumb.DragCompletedEvent handler)
        }

        private async void SizePlus_Click(object sender, RoutedEventArgs e)
        {
            SizeSlider.Value = Math.Min(SizeSlider.Value + 10, SizeSlider.Maximum);

            // Trigger redraw for button clicks
            if (_lastResult != null && _lastBitmap != null)
            {
                await RedrawPreviewsAsync();
            }
        }

        private async void SizeMinus_Click(object sender, RoutedEventArgs e)
        {
            SizeSlider.Value = Math.Max(SizeSlider.Value - 10, SizeSlider.Minimum);

            // Trigger redraw for button clicks
            if (_lastResult != null && _lastBitmap != null)
            {
                await RedrawPreviewsAsync();
            }
        }

        private void ResetControls()
        {
            SizeSlider.Value = 100;
            _colorCache = null;
            _bwCache = null;
            _lastKey = null;

            // Disallow background drawing until ApplyResult enables it
            _allowBackgroundDraw = false;

            // Immediately clear canvases and textboxes to avoid showing old data
            OriginalVectorCanvas.Children.Clear();
            SmoothVectorCanvas.Children.Clear();
            // Keep canvases hidden until rendering completes or the first batch is ready
            OriginalVectorCanvas.Visibility = Visibility.Collapsed;
            SmoothVectorCanvas.Visibility = Visibility.Collapsed;

            if (OriginalTextBox != null)
            {
                OriginalTextBox.Text = string.Empty;
            }

            if (SmoothTextBox != null)
            {
                SmoothTextBox.Text = string.Empty;
            }
        }

        private void ModeRadio_Changed(object sender, RoutedEventArgs e)
        {
            _bwMode = BwModeRadio?.IsChecked == true;

            if (_lastBitmap != null)
            {
                // Defer vectorization so the UI can update the radio button first
                Dispatcher.BeginInvoke(new Action(async () => await Vectorize(_lastBitmap)), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        // -------------------------------------------------
        // MAIN PIPELINE  (async — never blocks UI)
        // -------------------------------------------------

        private async Task Vectorize(BitmapSource bitmap)
        {
            bool bwMode = _bwMode;
            var key = (bitmap, bwMode);
            var cached = bwMode ? _bwCache : _colorCache;

            // Serve from cache when only the mode changed — no re-computation needed
            if (key == _lastKey && cached != null)
            {
                _lastResult = cached;
                await ApplyResult(cached);

                return;
            }

            if (_cts != null)
            {
                await _cts.CancelAsync();
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            SetBusy(true);

            var progress = new Progress<(int Pct, string Msg)>(p =>
                                                               {
                                                                   ConversionProgressBar.Value = p.Pct;
                                                                   StatusLabel.Text = p.Msg;
                                                               });

            try
            {
                var options = VectorizationOptions.Create(bwMode, 1.0);
                var result = await ImageVectorizer.VectorizeAsync(bitmap, options, progress, token);

                // Store in the appropriate cache slot
                _lastKey = key;
                _lastResult = result;

                if (bwMode)
                {
                    _bwCache = result;
                }
                else
                {
                    _colorCache = result;
                }

                await ApplyResult(result);
            }
            catch (OperationCanceledException)
            {
                // A newer Vectorize() call already called SetBusy(true); let it own the UI state.
            }
            finally
            {
                // Hide the progress bar only after the canvas has been fully drawn.
                if (!_cts.Token.IsCancellationRequested)
                {
                    SetBusy(false);
                }
            }
        }

        private async Task ApplyResult(VectorizationResult result)
        {
            _origWidth = result.OriginalWidth;
            _origHeight = result.OriginalHeight;

            // Calculate fit scale for available canvas space
            _baseFitScale = CalculateFitScale();

            // Reset slider to 100% (which will apply the fit scale)
            SizeSlider.ValueChanged -= SizeSlider_ValueChanged;
            SizeSlider.Value = 100;
            SizeSlider.ValueChanged += SizeSlider_ValueChanged;

            OriginalTextBox.Text = result.OriginalVectorXAML;
            SmoothTextBox.Text = result.SmoothedVectorXAML;

            // Hide/clear canvases so the vector text is the only visible content initially
            OriginalVectorCanvas.Visibility = Visibility.Collapsed;
            SmoothVectorCanvas.Visibility = Visibility.Collapsed;
            OriginalVectorCanvas.Children.Clear();
            SmoothVectorCanvas.Children.Clear();

            // Force a render pass so the text boxes are painted before we update status
            await Dispatcher.InvokeAsync(() =>
                                         {
                                             OriginalTextBox.UpdateLayout();
                                             SmoothTextBox.UpdateLayout();
                                         }, System.Windows.Threading.DispatcherPriority.Render).Task;

            // Ensure the rendered text is actually painted before changing status
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background).Task;

            // Now update progress/status so user sees 'Finishing…' after the text appears
            ConversionProgressBar.Value = 100;
            StatusLabel.Text = "Finishing…";

            // Short pause so the user can read the finishing status (reduced from 200ms)
            await Task.Delay(80);

            // Inform the user that image rendering will start in background shortly
            StatusLabel.Text = "Rendering image…";

            // Start progressive drawing after a shorter delay so the image build begins sooner
            _ = Task.Run(async () =>
                         {
                             await Task.Delay(200).ConfigureAwait(false);

                             await Dispatcher.InvokeAsync(() => { _allowBackgroundDraw = true; },
                                                          System.Windows.Threading.DispatcherPriority.Background);

                             // Start both draws and wait for them to finish
                             var t1 = Dispatcher
                                      .InvokeAsync(() => StartProgressiveDraw(OriginalVectorCanvas,
                                                                              result.OriginalGeometries, 50)).Task;

                             var t2 = Dispatcher
                                      .InvokeAsync(() => StartProgressiveDraw(SmoothVectorCanvas,
                                                                              result.SmoothedGeometries, 50)).Task;

                             await Task.WhenAll(t1, t2);

                             // Clear the status label after rendering is complete
                             await Dispatcher.InvokeAsync(() => StatusLabel.Text = string.Empty);
                         });
        }

        private void SetBusy(bool busy)
        {
            LoadImageButton.IsEnabled = !busy;
            PasteImageButton.IsEnabled = !busy;
            CopyOriginalButton.IsEnabled = !busy;
            CopySmoothButton.IsEnabled = !busy;

            ConversionProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

            if (busy)
            {
                ConversionProgressBar.Value = 0;
                StatusLabel.Text = "Starting…";
            }
        }

        private async Task RedrawPreviewsAsync()
        {
            if (_lastResult == null)
            {
                return;
            }
            
            if (!_allowBackgroundDraw)
            {
                return;
            }
         
            var t1 = StartProgressiveDraw(OriginalVectorCanvas, _lastResult.OriginalGeometries, 50);
            var t2 = StartProgressiveDraw(SmoothVectorCanvas, _lastResult.SmoothedGeometries, 50);
            await Task.WhenAll(t1, t2);
        }
     
        private async Task StartProgressiveDraw(Canvas canvas, List<(Color Color, Geometry Geometry)> items,
                                                int batchSize = 30)
        {
            // Clear immediately on UI thread
            canvas.Children.Clear();

            var sliderFactor = SizeSlider.Value / 100.0;
            var effectiveScale = _baseFitScale  * sliderFactor;
            var scaledW = _origWidth            * effectiveScale;
            var scaledH = _origHeight           * effectiveScale;

            canvas.Width = scaledW;
            canvas.Height = scaledH;

            ScaleTransform? cachedTransform = null;

            if (Math.Abs(effectiveScale - 1.0) > Tolerance)
            {
                cachedTransform = new ScaleTransform(effectiveScale, effectiveScale);
                cachedTransform.Freeze();
            }

            // Prepare data (freeze geometries) on background thread
            var itemData = await Task.Run(() =>
                                          {
                                              var list = items.Select(x => (x.Color, geom: x.Geometry)).ToList();

                                              foreach (var tuple in list)
                                              {
                                                  var g = tuple.geom;

                                                  if (g is { IsFrozen: false, CanFreeze: true })
                                                  {
                                                      try
                                                      {
                                                          g.Freeze();
                                                      }
                                                      catch
                                                      {
                                                          // ignore freeze failures
                                                      }
                                                  }
                                              }

                                              return list;
                                          });

            int total = itemData.Count;
            int index = 0;

            var tcs = new TaskCompletionSource<object?>();

            // Use a DispatcherTimer to schedule batches at intervals so the user clearly sees
            // the image build up chunk-by-chunk.
            var timer =
                new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(60)
                };

            timer.Tick += (s, e) =>
                          {
                              try
                              {
                                  // Reveal canvas when first batch is about to be added so the text is already visible
                                  if (index == 0)
                                  {
                                      canvas.Visibility = Visibility.Visible;
                                  }

                                  int count = Math.Min(batchSize, total - index);

                                  for (int i = 0; i < count; i++)
                                  {
                                      var (color, geom) = itemData[index + i];

                                      var brush = new SolidColorBrush(color);

                                      if (brush.CanFreeze)
                                      {
                                          brush.Freeze();
                                      }

                                      var path = new System.Windows.Shapes.Path
                                                 {
                                                     Fill = brush,
                                                     Data = geom
                                                 };

                                      if (cachedTransform != null)
                                      {
                                          path.RenderTransform = cachedTransform;
                                      }

                                      canvas.Children.Add(path);
                                  }

                                  index += count;

                                  if (index >= total)
                                  {
                                      timer.Stop();
                                      tcs.TrySetResult(null);
                                  }
                                  // otherwise wait for next tick
                              }
                              catch (Exception ex)
                              {
                                  timer.Stop();
                                  tcs.TrySetException(ex);
                              }
                          };

            // Start timer on UI thread
            canvas.Dispatcher.Invoke(() => timer.Start());

            await tcs.Task;
        }

        // -------------------------------------------------
        // PREVIEW
        // -------------------------------------------------

        private double CalculateFitScale()
        {
            // Get available space in the scrollviewer/canvas area
            // Estimate based on window size (accounts for UI elements)
            double availableWidth = OriginalVectorCanvas.ActualWidth > 0
                                        ? OriginalVectorCanvas.ActualWidth
                                        : ActualWidth * 0.3 - 50; // Fallback estimate

            double availableHeight = OriginalVectorCanvas.ActualHeight > 0
                                         ? OriginalVectorCanvas.ActualHeight
                                         : ActualHeight * 0.6 - 100; // Fallback estimate

            // Ensure we have positive dimensions
            if (availableWidth <= 0)
            {
                availableWidth = 400;
            }

            if (availableHeight <= 0)
            {
                availableHeight = 400;
            }

            // Calculate scale to fit within available space
            double scaleX = availableWidth  / _origWidth;
            double scaleY = availableHeight / _origHeight;

            // Use the smaller scale to maintain aspect ratio
            double fitScale = Math.Min(scaleX, scaleY);

            // Clamp between reasonable limits (at least 20% of original, max 200%)
            fitScale = Math.Max(0.2, Math.Min(fitScale, 2.0));

            return fitScale;
        }

        // -------------------------------------------------
        // COPY
        // -------------------------------------------------

        private void CopyOriginal_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(OriginalTextBox.Text))
            {
                Clipboard.SetText(OriginalTextBox.Text);
            }
        }

        private void CopySmooth_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(SmoothTextBox.Text))
            {
                Clipboard.SetText(SmoothTextBox.Text);
            }
        }

        private void CopyPixels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Use the API method
                if (_lastBitmap == null)
                {
                    MessageBox.Show("No image available to copy.", "Error");

                    return;
                }

                var asciiArt = ImageVectorizer.GetPixelArtText(_lastBitmap, true);
                Clipboard.SetText(asciiArt);

                MessageBox.Show($"✓ Copied pixel-art string\nOriginal: {_lastBitmap.PixelWidth}×{_lastBitmap.PixelHeight}px");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }
    }
}