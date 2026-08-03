using System.IO;

namespace LightTranslate;

public sealed class CaptureRegion
{
    public string ScreenDeviceName { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool IsUsable => Width >= 8 && Height >= 8;
}

public sealed record CaptureSelectionResult(string ImagePath, CaptureRegion Region);

public static class ScreenCaptureService
{
    public static CaptureSelectionResult CaptureRegion(CaptureRegion region)
    {
        if (!region.IsUsable)
            throw new InvalidOperationException("还没有可重复使用的截图区域");

        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(candidate =>
                         candidate.DeviceName.Equals(region.ScreenDeviceName, StringComparison.OrdinalIgnoreCase))
                     ?? System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(region.X, region.Y));

        var requested = new System.Drawing.Rectangle(region.X, region.Y, region.Width, region.Height);
        var clipped = System.Drawing.Rectangle.Intersect(requested, screen.Bounds);
        if (clipped.Width < 8 || clipped.Height < 8)
            throw new InvalidOperationException("上次选区已不在当前屏幕范围内，请重新框选");

        using var bitmap = new System.Drawing.Bitmap(
            clipped.Width,
            clipped.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                clipped.Left,
                clipped.Top,
                0,
                0,
                clipped.Size,
                System.Drawing.CopyPixelOperation.SourceCopy);
        }

        var path = CreateTemporaryPath();
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);

        return new CaptureSelectionResult(path, new CaptureRegion
        {
            ScreenDeviceName = screen.DeviceName,
            X = clipped.X,
            Y = clipped.Y,
            Width = clipped.Width,
            Height = clipped.Height
        });
    }

    public static string CreateTemporaryPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LightTranslate");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
    }
}
