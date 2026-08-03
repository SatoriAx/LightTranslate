using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace LightTranslate;

internal static class OcrModelStore
{
    private const string CacheVersion = "v5";
    private static readonly object SyncRoot = new();
    private static string? _verifiedDirectory;

    private static readonly ModelResource[] Models =
    [
        new(
            "ch_PP-OCRv5_mobile_det.onnx",
            "LightTranslate.OcrModels.ch_PP-OCRv5_mobile_det.onnx",
            "4D97C44A20D30A81AAD087D6A396B08F786C4635742AFC391F6621F5C6AE78AE"),
        new(
            "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
            "LightTranslate.OcrModels.ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
            "54379AE5174D026780215FC748A7F31910DEE36818E63D49E17DC598ECC82DF7"),
        new(
            "ch_PP-OCRv5_rec_mobile_infer.onnx",
            "LightTranslate.OcrModels.ch_PP-OCRv5_rec_mobile_infer.onnx",
            "0030C6B05FBE29B07A93701503938D637EFE7423325E2EFB2BD7C8F220D40A8D"),
        new(
            "ppocrv5_dict.txt",
            "LightTranslate.OcrModels.ppocrv5_dict.txt",
            "D1979E9F794C464C0D2E0B70A7FE14DD978E9DC644C0E71F14158CDF8342AF1B")
    ];

    public static string EnsureAvailable()
    {
        lock (SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(_verifiedDirectory) && Directory.Exists(_verifiedDirectory))
                return _verifiedDirectory;

            var modelDirectory = GetModelDirectory();
            Directory.CreateDirectory(modelDirectory);

            var assembly = Assembly.GetExecutingAssembly();
            foreach (var model in Models)
                EnsureModel(assembly, modelDirectory, model);

            _verifiedDirectory = modelDirectory;
            return modelDirectory;
        }
    }

    private static string GetModelDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("LIGHTTRANSLATE_OCR_CACHE_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Path.GetFullPath(overrideDirectory);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LightTranslate",
            "ocr-cache",
            CacheVersion);
    }

    private static void EnsureModel(Assembly assembly, string modelDirectory, ModelResource model)
    {
        var targetPath = Path.Combine(modelDirectory, model.FileName);
        if (HasExpectedHash(targetPath, model.Sha256))
            return;

        var temporaryPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using var resource = assembly.GetManifestResourceStream(model.ResourceName)
                                 ?? throw new InvalidOperationException($"程序内缺少 OCR 模型资源：{model.FileName}");
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       1024 * 128,
                       FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                resource.CopyTo(output);
                output.Flush(true);
            }

            if (!HasExpectedHash(temporaryPath, model.Sha256))
                throw new InvalidDataException($"OCR 模型释放后校验失败：{model.FileName}");

            File.Move(temporaryPath, targetPath, true);
            if (!HasExpectedHash(targetPath, model.Sha256))
                throw new InvalidDataException($"OCR 模型缓存校验失败：{model.FileName}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static bool HasExpectedHash(string path, string expectedHash)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 128,
                FileOptions.SequentialScan);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            return actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private sealed record ModelResource(string FileName, string ResourceName, string Sha256);
}
