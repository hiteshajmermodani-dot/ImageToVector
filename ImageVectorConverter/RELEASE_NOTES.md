# Release Notes - ImageVectorConverter

## [1.0.0] - 2024-01-XX

### Added
- Initial release of ImageVectorConverter
- Support for converting PNG, JPG, and BMP images to vector graphics
- Color and grayscale vectorization modes
- XAML PathGeometry output for WPF/UWP applications
- Path smoothing and optimization algorithms
- Async/await support for non-blocking operations
- Progress reporting during vectorization
- Cancellation token support
- Sample WPF application demonstrating all features


## Packaging
A NuGet package `ImageVectorConverter.1.0.0.nupkg` is generated from the `ImageVectorConverter` project. Symbols package `ImageVectorConverter.1.0.0.snupkg` is also produced when building with symbols enabled.

## Notes
- Project targets .NET 9 (net9.0-windows) and requires Windows/WPF runtime.
- License: MIT
- Repository: https://github.com/yourusername/ImageVectorConverter
