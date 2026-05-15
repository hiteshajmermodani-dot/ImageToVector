# ImageVectorConverter

ImageVectorConverter converts raster images (PNG, JPG, BMP) into vector graphics (XAML PathGeometry) for WPF/UWP applications.

Package: `ImageVectorConverter`
Version: `1.0.0`

## Installation

Install from NuGet Package Manager:

```powershell
Install-Package ImageVectorConverter
```

Or via .NET CLI:

```bash
dotnet add package ImageVectorConverter
```

## Quick start

```csharp
using ImageVectorConverter;
using System.Windows.Media.Imaging;

var bitmap = new BitmapImage(new Uri("path/to/image.png"));
var options = VectorizationOptions.Create(bwMode: false, sizeMultiplier: 1.0);
var progress = new Progress<(int Percentage, string Message)>(p => Console.WriteLine($"{p.Percentage}% - {p.Message}"));

var result = await ImageVectorizer.VectorizeAsync(bitmap, options, progress);

// XAML output
var xaml = result.OriginalVectorXAML;
// Geometries for direct WPF use
var geoms = result.OriginalGeometries;
```

## Notes
- Targets: .NET 9 (net9.0-windows) with WPF support.
- License: MIT
- Repository: [https://github.com/hiteshajmermodani-dot/ImageToVector]
- See `RELEASE_NOTES.md` for release details.
