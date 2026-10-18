using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DragShift.Core;

/// <summary>"Remove BG": salient-object segmentation with the U²-Net (small) model, run locally on the CPU.</summary>
public static class BackgroundRemover
{
    const int Size = 320;
    static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    static readonly Lazy<InferenceSession> Session = new(() =>
    {
        using var s = typeof(BackgroundRemover).Assembly.GetManifestResourceStream("DragShift.u2netp.onnx")
            ?? throw new InvalidOperationException("Background model is missing from the build.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new InferenceSession(ms.ToArray());
    });

    /// <summary>Makes the background of <paramref name="img"/> transparent, in place.</summary>
    public static void Apply(MagickImage img)
    {
        using var mask = Mask(img);
        img.Alpha(AlphaOption.Set);
        img.Composite(mask, CompositeOperator.CopyAlpha);
    }

    /// <summary>Grayscale mask the size of the image: white = foreground. The caller owns the result.</summary>
    public static MagickImage Mask(MagickImage img)
    {
        using var small = (MagickImage)img.Clone();
        small.Alpha(AlphaOption.Remove);
        small.Resize(new MagickGeometry(Size, Size) { IgnoreAspectRatio = true });
        var rgb = small.GetPixels().ToByteArray(PixelMapping.RGB)
            ?? throw new InvalidOperationException("Couldn't read pixels.");

        var max = 1e-6f;
        foreach (var v in rgb) max = Math.Max(max, v);
        var input = new DenseTensor<float>([1, 3, Size, Size]);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var p = (y * Size + x) * 3;
            for (var c = 0; c < 3; c++) input[0, c, y, x] = (rgb[p + c] / max - Mean[c]) / Std[c];
        }

        var session = Session.Value;
        var inputName = session.InputMetadata.Keys.First();
        float[] pred;
        lock (session)
        {
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
            pred = results.First().AsEnumerable<float>().ToArray();
        }

        float lo = pred.Min(), hi = pred.Max(), range = Math.Max(1e-6f, hi - lo);
        var bytes = new byte[Size * Size];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)Math.Round(Math.Clamp((pred[i] - lo) / range, 0, 1) * 255);

        var mask = new MagickImage();
        mask.ReadPixels(bytes, new PixelReadSettings(Size, Size, StorageType.Char, "I"));
        mask.ColorType = ColorType.Grayscale;
        mask.FilterType = FilterType.Lanczos;
        mask.Resize(new MagickGeometry(img.Width, img.Height) { IgnoreAspectRatio = true });
        return mask;
    }
}
