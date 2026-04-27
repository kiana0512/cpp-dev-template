using System.IO;
using System.Text;
using VhSenderGui.Models;

namespace VhSenderGui.Services;

public static class AiRuntimeModelCheckService
{
    private static readonly string[] DangerousRemotePatterns =
    [
        "huggingface.co",
        "hf_hub_download",
        "snapshot_download",
        "facebook/w2v-bert-2.0",
        "amphion/MaskGCT",
        "from_pretrained(\"facebook/",
        "from_pretrained('facebook/",
        "from_pretrained(\"amphion/",
        "from_pretrained('amphion/",
        "cas-bridge.xethub.hf.co",
    ];

    public static AiModelCheckResult Check(string modelDir, string configPath, string? indexTtsRepo = null, string textNormalizer = "fallback")
    {
        var result = new AiModelCheckResult();
        result.TextNormalizerMode = NormalizeTextNormalizerMode(textNormalizer);
        if (result.TextNormalizerMode == "fallback")
        {
            result.WeTextImportStatus = "skipped";
            result.KaldiFstImportStatus = "skipped";
        }
        result.ModelDirExists = Directory.Exists(modelDir);
        result.ConfigExists = File.Exists(configPath);
        if (!result.ModelDirExists)
            result.Warnings.Add("model_dir does not exist: " + modelDir);
        if (!result.ConfigExists)
        {
            result.Warnings.Add("config.yaml is missing: " + configPath);
            result.MissingOrSuspiciousFiles.Add("config.yaml");
        }
        else
            result.FoundFiles.Add(configPath);

        if (result.ModelDirExists)
        {
            var mainOk = true;
            mainOk &= CheckExact(result, modelDir, "config.yaml");
            mainOk &= CheckExact(result, modelDir, "gpt.pth");
            mainOk &= CheckExact(result, modelDir, "s2mel.pth");
            mainOk &= CheckExact(result, modelDir, "bpe.model");
            mainOk &= CheckExact(result, modelDir, "wav2vec2bert_stats.pt");
            mainOk &= CheckExact(result, modelDir, "qwen0.6bemo4-merge");
            mainOk &= CheckExact(result, modelDir, Path.Combine("qwen0.6bemo4-merge", "config.json"));
            mainOk &= CheckAnyExact(result, modelDir, "qwen0.6bemo4-merge weights",
                [Path.Combine("qwen0.6bemo4-merge", "model.safetensors"), Path.Combine("qwen0.6bemo4-merge", "pytorch_model.bin")]);
            mainOk &= CheckAnyExact(result, modelDir, "qwen0.6bemo4-merge tokenizer",
                [Path.Combine("qwen0.6bemo4-merge", "tokenizer.json"), Path.Combine("qwen0.6bemo4-merge", "tokenizer.model")]);
            result.IndexTts2Ok = mainOk;

            result.W2vBertOk =
                CheckExact(result, modelDir, "w2v-bert-2.0") &
                CheckExact(result, modelDir, Path.Combine("w2v-bert-2.0", "preprocessor_config.json"));

            result.MaskGctOk =
                CheckExact(result, modelDir, Path.Combine("MaskGCT", "semantic_codec", "model.safetensors")) &
                CheckExact(result, modelDir, Path.Combine("MaskGCT", "t2s_model", "model.safetensors")) &
                CheckExact(result, modelDir, Path.Combine("MaskGCT", "s2a_model", "s2a_model_1layer", "model.safetensors")) &
                CheckExact(result, modelDir, Path.Combine("MaskGCT", "s2a_model", "s2a_model_full", "model.safetensors"));

            result.CampplusOk =
                CheckExact(result, modelDir, "campplus") &
                CheckExact(result, modelDir, Path.Combine("campplus", "campplus_cn_common.bin"));

            result.BigVganOk =
                CheckExact(result, modelDir, Path.Combine("BigVGAN", "config.json")) &&
                CheckBigVganNumMels(result, Path.Combine(modelDir, "BigVGAN", "config.json"));
        }

        if (result.ConfigExists)
        {
            ScanFileForRemoteRefs(result, configPath, Directory.GetCurrentDirectory());
        }
        if (!string.IsNullOrWhiteSpace(indexTtsRepo))
            ScanRepoForRemoteRefs(result, indexTtsRepo);

        if (result.RemoteReferences.Count > 0)
        {
            result.RemoteRefsOk = false;
            result.Warnings.Add("Remote references detected: " + string.Join(" | ", result.RemoteReferences.Take(8)));
            result.Recommendations.Add("IndexTTS2 source/config still contains remote HF references. Run: .\\scripts\\patch_indextts2_local_paths.ps1");
        }
        else
        {
            result.RemoteRefsOk = true;
        }

        result.LooksComplete = result.ModelDirExists && result.ConfigExists && result.IndexTts2Ok;
        result.LooksLocalComplete = result.LooksComplete && result.W2vBertOk && result.MaskGctOk && result.CampplusOk && result.BigVganOk && result.RemoteRefsOk;
        result.CanRunOffline = result.LooksLocalComplete;
        result.Recommendation = "If config.yaml is missing, wait for download or run: modelscope download --model IndexTeam/IndexTTS-2 --local_dir .\\models\\IndexTTS-2";
        result.Recommendations.Add(result.Recommendation);
        if (!result.W2vBertOk)
            result.Recommendations.Add("Local dependency missing: w2v-bert-2.0. Run: modelscope download --model AI-ModelScope/w2v-bert-2.0 --local_dir .\\models\\IndexTTS-2\\w2v-bert-2.0");
        if (!result.MaskGctOk)
            result.Recommendations.Add("Local dependency missing: MaskGCT. Run: modelscope download --model amphion/MaskGCT --local_dir .\\models\\IndexTTS-2\\MaskGCT");
        if (!result.CampplusOk)
            result.Recommendations.Add("Local dependency missing: campplus. Verify .\\models\\IndexTTS-2\\campplus\\campplus_cn_common.bin");
        if (!result.BigVganOk)
            result.Recommendations.Add("BigVGAN must be 80-band. Verify .\\models\\IndexTTS-2\\BigVGAN\\config.json has num_mels=80.");
        return result;
    }

    private static string NormalizeTextNormalizerMode(string? value)
    {
        var mode = string.IsNullOrWhiteSpace(value) ? "fallback" : value.Trim().ToLowerInvariant();
        return mode is "auto" or "fallback" or "wetext" ? mode : "fallback";
    }

    private static bool CheckExact(AiModelCheckResult result, string modelDir, string relativePath)
    {
        var p = Path.Combine(modelDir, relativePath);
        if (File.Exists(p) || Directory.Exists(p))
        {
            result.FoundFiles.Add(p);
            return true;
        }
        result.MissingOrSuspiciousFiles.Add(relativePath);
        result.Warnings.Add("Missing local file or directory: " + relativePath);
        return false;
    }

    private static bool CheckAnyExact(AiModelCheckResult result, string modelDir, string label, string[] relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            var p = Path.Combine(modelDir, rel);
            if (File.Exists(p) || Directory.Exists(p))
            {
                result.FoundFiles.Add(p);
                return true;
            }
        }
        result.MissingOrSuspiciousFiles.Add(label);
        result.Warnings.Add("Missing local file group: " + label);
        return false;
    }

    private static bool CheckBigVganNumMels(AiModelCheckResult result, string configPath)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath, Encoding.UTF8));
            if (doc.RootElement.TryGetProperty("num_mels", out var numMels) && numMels.GetInt32() == 80)
                return true;
            var value = doc.RootElement.TryGetProperty("num_mels", out numMels) ? numMels.ToString() : "missing";
            result.MissingOrSuspiciousFiles.Add("BigVGAN/config.json num_mels=" + value + " expected 80");
            result.Warnings.Add("BigVGAN must be 80-band; current num_mels=" + value);
        }
        catch (Exception ex)
        {
            result.MissingOrSuspiciousFiles.Add("BigVGAN/config.json unreadable");
            result.Warnings.Add("Could not verify BigVGAN num_mels: " + ex.Message);
        }
        return false;
    }

    private static void ScanRepoForRemoteRefs(AiModelCheckResult result, string indexTtsRepo)
    {
        var root = Path.Combine(indexTtsRepo, "indextts");
        if (!Directory.Exists(root))
            return;
        foreach (var file in Directory.EnumerateFiles(root, "*.py", SearchOption.AllDirectories))
            ScanFileForRemoteRefs(result, file, indexTtsRepo);
    }

    private static void ScanFileForRemoteRefs(AiModelCheckResult result, string path, string root)
    {
        if (!File.Exists(path))
            return;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch
        {
            lines = File.ReadAllLines(path, Encoding.Default);
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var stripped = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(stripped) || stripped.StartsWith("#", StringComparison.Ordinal))
                continue;
            foreach (var pattern in DangerousRemotePatterns)
            {
                if (pattern.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) &&
                    path.Replace('\\', '/').Contains("/indextts/gpt/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!stripped.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    continue;
                var rel = MakeRelative(path, root);
                result.RemoteReferences.Add($"{rel}:{i + 1}: {pattern}: {stripped}");
            }
        }
    }

    private static string MakeRelative(string path, string root)
    {
        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch
        {
            return path;
        }
    }
}
