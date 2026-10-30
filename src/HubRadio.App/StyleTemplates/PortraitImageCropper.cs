using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HubRadio.App.StyleTemplates;

/// <summary>FR-140: a pixel range of an image (left, top, width, height) the user picked to cut one character out.</summary>
public readonly record struct PortraitCropRect(int X, int Y, int Width, int Height)
{
    public const int MinimumSide = 8;

    /// <summary>True when the range lies inside an image of the given size and is not smaller than <see cref="MinimumSide"/>.</summary>
    public bool IsInside(int imageWidth, int imageHeight) =>
        X >= 0 && Y >= 0 && Width >= MinimumSide && Height >= MinimumSide && X + Width <= imageWidth && Y + Height <= imageHeight;
}

/// <summary>FR-140: reads the size of an image and writes a cut-out of it as a PNG. The source image is only read, never changed.</summary>
public static class PortraitImageCropper
{
    public static (int Width, int Height) Size(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        using FileStream file = File.OpenRead(imagePath);
        using IRandomAccessStream stream = file.AsRandomAccessStream();
        BitmapDecoder decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    /// <summary>The mean color (#RRGGBB) of the visible pixels of an image; used to tell a standing picture apart in a frame.</summary>
    public static string? MeanColor(string imagePath)
    {
        try
        {
            using FileStream file = File.OpenRead(imagePath);
            using IRandomAccessStream stream = file.AsRandomAccessStream();
            BitmapDecoder decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            PixelDataProvider data = decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
            byte[] pixels = data.DetachPixelData();
            long r = 0, g = 0, b = 0, n = 0;
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                if (pixels[i + 3] >= 128)
                {
                    b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; n++;
                }
            }

            return n == 0 ? null : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"#{r / n:X2}{g / n:X2}{b / n:X2}");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>Writes the pixels of <paramref name="rect"/> of the source to <paramref name="destinationPng"/> (a new file).</summary>
    public static void CropToPng(string sourcePath, string destinationPng, PortraitCropRect rect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPng);
        using FileStream file = File.OpenRead(sourcePath);
        using IRandomAccessStream input = file.AsRandomAccessStream();
        BitmapDecoder decoder = BitmapDecoder.CreateAsync(input).AsTask().GetAwaiter().GetResult();
        if (!rect.IsInside((int)decoder.PixelWidth, (int)decoder.PixelHeight))
        {
            throw new ArgumentOutOfRangeException(nameof(rect), "The range must lie inside the image.");
        }

        var transform = new BitmapTransform
        {
            Bounds = new BitmapBounds { X = (uint)rect.X, Y = (uint)rect.Y, Width = (uint)rect.Width, Height = (uint)rect.Height },
        };
        PixelDataProvider pixels = decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, (uint)rect.Width, (uint)rect.Height, 96, 96,
            pixels.DetachPixelData());
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();
        output.Seek(0);
        byte[] bytes = new byte[output.Size];
        using (DataReader reader = new(output))
        {
            _ = reader.LoadAsync((uint)output.Size).AsTask().GetAwaiter().GetResult();
            reader.ReadBytes(bytes);
        }

        File.WriteAllBytes(destinationPng, bytes);
    }
}
