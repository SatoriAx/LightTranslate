using RapidOcrNet;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LightTranslate;

public sealed class OcrService : IDisposable
{
    private const double EnhancementConfidenceThreshold = 0.86;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RapidOcr? _engine;
    private bool _disposed;

    public async Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OcrService));
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("截图文件不存在", imagePath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? preparedPath = null;
        try
        {
            _engine ??= await Task.Run(CreateEngine, cancellationToken).ConfigureAwait(false);
            var originalResult = await DetectAsync(imagePath, cancellationToken).ConfigureAwait(false);
            var selectedResult = originalResult;

            var settings = SettingsStore.Load();
            if (settings.EnhanceSmallText && ShouldTryEnhancedPass(originalResult))
            {
                preparedPath = await Task.Run(
                    () => PrepareSmallTextImage(imagePath),
                    cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(preparedPath))
                {
                    var enhancedResult = await DetectAsync(preparedPath, cancellationToken).ConfigureAwait(false);
                    if (IsEnhancedResultBetter(originalResult, enhancedResult))
                        selectedResult = enhancedResult;
                }
            }

            var text = ReconstructText(selectedResult);
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("没有在选区中识别到文字");

            return text;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(preparedPath))
                TryDelete(preparedPath);
            _gate.Release();
        }
    }

    private Task<OcrResult> DetectAsync(string imagePath, CancellationToken cancellationToken)
    {
        var options = RapidOcrOptions.Default with
        {
            ReturnWordBox = true,
            TextScore = 0.52f,
            DoAngle = true
        };

        return Task.Run(() => _engine!.Detect(imagePath, options), cancellationToken);
    }

    private static RapidOcr CreateEngine()
    {
        var modelDirectory = OcrModelStore.EnsureAvailable();
        var engine = new RapidOcr();
        engine.InitModels(
            detPath: Path.Combine(modelDirectory, "ch_PP-OCRv5_mobile_det.onnx"),
            clsPath: Path.Combine(modelDirectory, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
            recPath: Path.Combine(modelDirectory, "ch_PP-OCRv5_rec_mobile_infer.onnx"),
            keysPath: Path.Combine(modelDirectory, "ppocrv5_dict.txt"));
        return engine;
    }

    private static bool ShouldTryEnhancedPass(OcrResult? result)
    {
        return CountRecognizedCharacters(result) < 3 ||
               CalculateConfidence(result) < EnhancementConfidenceThreshold;
    }

    private static bool IsEnhancedResultBetter(OcrResult? original, OcrResult? enhanced)
    {
        var originalCharacters = CountRecognizedCharacters(original);
        var enhancedCharacters = CountRecognizedCharacters(enhanced);
        if (originalCharacters == 0)
            return enhancedCharacters > 0;
        if (enhancedCharacters == 0)
            return false;

        var originalConfidence = CalculateConfidence(original);
        var enhancedConfidence = CalculateConfidence(enhanced);
        if (enhancedConfidence >= originalConfidence + 0.015)
            return true;

        return enhancedCharacters >= originalCharacters * 1.2 &&
               enhancedConfidence >= originalConfidence - 0.02;
    }

    private static double CalculateConfidence(OcrResult? result)
    {
        var scores = result?.TextBlocks?
            .SelectMany(block => block.CharScores ?? [])
            .Where(score => !float.IsNaN(score) && !float.IsInfinity(score))
            .Select(score => (double)score)
            .ToList();
        return scores is { Count: > 0 } ? scores.Average() : 0;
    }

    private static int CountRecognizedCharacters(OcrResult? result)
    {
        return result?.TextBlocks?
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .Sum(block => block.Text.Count(character => !char.IsWhiteSpace(character))) ?? 0;
    }

    private static string? PrepareSmallTextImage(string imagePath)
    {
        using var source = new System.Drawing.Bitmap(imagePath);
        var longest = Math.Max(source.Width, source.Height);
        var shortSide = Math.Min(source.Width, source.Height);

        if (longest >= 2600 || (longest >= 1500 && shortSide >= 650))
            return null;

        var scale = Math.Min(2.0, 2800.0 / Math.Max(1, longest));
        if (scale < 1.15)
            return null;

        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var enhanced = new System.Drawing.Bitmap(
            width,
            height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(enhanced))
        {
            graphics.Clear(System.Drawing.Color.White);
            graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, width, height));
        }

        var path = ScreenCaptureService.CreateTemporaryPath();
        enhanced.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    private static string ReconstructText(OcrResult? result)
    {
        if (result?.TextBlocks is not { Length: > 0 } blocks)
            return NormalizeOcrText(result?.StrRes ?? string.Empty);

        var items = blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .Select(block => new OcrLine(
                block.Text.Trim(),
                block.BoxPoints.Min(point => point.X),
                block.BoxPoints.Min(point => point.Y),
                block.BoxPoints.Max(point => point.X),
                block.BoxPoints.Max(point => point.Y)))
            .ToList();

        if (items.Count == 0)
            return string.Empty;

        var cjkCount = items.Count(item => ContainsCjk(item.Text));
        var verticalCount = items.Count(item => item.Height > item.Width * 1.28f);
        var verticalLayout = cjkCount > 0 && verticalCount >= Math.Max(1, items.Count / 2);

        IEnumerable<OcrLine> ordered = verticalLayout
            ? items.OrderByDescending(item => item.CenterX).ThenBy(item => item.Top)
            : items.OrderBy(item => item.CenterY).ThenBy(item => item.Left);

        return NormalizeOcrLines(ordered.Select(item => item.Text), verticalLayout);
    }

    private static string NormalizeOcrLines(IEnumerable<string> sourceLines, bool verticalLayout)
    {
        var lines = sourceLines
            .Select(line => Regex.Replace(line.Trim(), @"[\t ]+", " "))
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
            return string.Empty;
        if (verticalLayout)
        {
            var columns = new List<string>();
            foreach (var line in lines)
            {
                if (line == "0" && columns.Count > 0 && ContainsCjk(columns[^1]))
                    columns[^1] += "。";
                else
                    columns.Add(line);
            }

            return string.Join("\n", columns).Trim();
        }

        var builder = new StringBuilder(lines[0]);
        for (var index = 1; index < lines.Count; index++)
        {
            var previous = builder.Length > 0 ? builder[^1] : '\0';
            var current = lines[index];

            if (previous == '-' && StartsWithLatinLetter(current))
            {
                builder.Length--;
                builder.Append(current);
            }
            else if (ShouldJoinAsWrappedLine(builder.ToString(), current))
            {
                if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
                    builder.Append(' ');
                builder.Append(current);
            }
            else
            {
                builder.Append('\n').Append(current);
            }
        }

        return NormalizeOcrText(builder.ToString());
    }

    private static bool ShouldJoinAsWrappedLine(string previousText, string current)
    {
        if (string.IsNullOrWhiteSpace(previousText) || string.IsNullOrWhiteSpace(current))
            return false;

        var previous = previousText.TrimEnd();
        var last = previous[^1];
        if (ContainsCjk(previous) || ContainsCjk(current))
            return !"。！？；：.!?;:".Contains(last);

        if (".!?;:)]}".Contains(last))
            return false;

        return StartsWithLatinLetter(current) || char.IsDigit(current[0]) || "([\"'".Contains(current[0]);
    }

    private static bool StartsWithLatinLetter(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        var character = value[0];
        return character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }

    private static bool ContainsCjk(string value)
    {
        foreach (var character in value)
        {
            if (character is >= '\u3040' and <= '\u30ff' or
                >= '\u3400' and <= '\u9fff' or
                >= '\uf900' and <= '\ufaff')
                return true;
        }

        return false;
    }

    private static string NormalizeOcrText(string text)
    {
        var normalized = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('\u00a0', ' ');
        normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return normalized.Trim();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _engine?.Dispose();
        _gate.Dispose();
        _disposed = true;
    }

    private sealed record OcrLine(string Text, float Left, float Top, float Right, float Bottom)
    {
        public float Width => Right - Left;
        public float Height => Bottom - Top;
        public float CenterX => (Left + Right) / 2;
        public float CenterY => (Top + Bottom) / 2;
    }
}
