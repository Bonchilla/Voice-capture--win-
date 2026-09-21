using System.Text.Json;

namespace VoiceCapture.Core;

public enum RecognitionMode { OpenAI, Local, Hybrid }
// Numeric values 0/1 are retained for settings from 0.1.0.
public enum AccelerationMode { Auto = 0, Cpu = 1, Vulkan = 2 }

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 2;
    public RecognitionMode Mode { get; init; } = RecognitionMode.Hybrid;
    public string MicrophoneId { get; init; } = "";
    public string Language { get; init; } = "auto";
    public string TranscriptionModel { get; init; } = "gpt-4o-mini-transcribe";
    public string CorrectionModel { get; init; } = "gpt-4.1-mini";
    public string LocalModel { get; init; } = ModelCatalog.ParakeetId;
    public string Vocabulary { get; init; } = "";
    public bool CorrectionEnabled { get; init; } = true;
    public bool LocalCloudCorrection { get; init; }
    public bool AutoPaste { get; init; } = true;
    public bool CloudConsent { get; init; }
    public bool StartWithWindows { get; init; }
    public AccelerationMode Acceleration { get; init; } = AccelerationMode.Auto;
    public int LocalDelayMilliseconds { get; init; } = 500;
    public int CorrectionTimeoutSeconds { get; init; } = 3;
    public int ProcessingTimeoutSeconds { get; init; } = 90;
    public int MaxRecordingSeconds { get; init; } = 300;
    public int HotkeyVirtualKey { get; init; } = 0x20;
    public bool HotkeyControl { get; init; } = true;
    public bool HotkeyAlt { get; init; } = true;
    public bool HotkeyShift { get; init; }

    public bool ShouldCorrect => CorrectionEnabled && (Mode != RecognitionMode.Local || LocalCloudCorrection);
    public string HotkeyDisplay => string.Join(" + ", new[]
    {
        HotkeyControl ? "Левый Ctrl" : null, HotkeyAlt ? "Левый Alt" : null,
        HotkeyShift ? "Левый Shift" : null,
        HotkeyVirtualKey == 0x20 ? "Space" : HotkeyVirtualKey is >= 0x70 and <= 0x7B
            ? $"F{HotkeyVirtualKey - 0x6F}" : ((char)HotkeyVirtualKey).ToString()
    }.Where(s => s is not null));

    public void Validate()
    {
        if (SchemaVersion != 2) throw new ArgumentException("Неизвестная версия настроек.");
        if (!Enum.IsDefined(Mode) || Acceleration is not (AccelerationMode.Auto or AccelerationMode.Cpu))
            throw new ArgumentException("Parakeet в этой поставке поддерживает только CPU.");
        if (Language is not ("auto" or "ru" or "en")) throw new ArgumentException("Неверный язык.");
        if (!ModelCatalog.All.Any(m => m.FileName == LocalModel)) throw new ArgumentException("Неизвестная локальная модель.");
        if (string.IsNullOrWhiteSpace(TranscriptionModel) || string.IsNullOrWhiteSpace(CorrectionModel))
            throw new ArgumentException("Укажите модели OpenAI.");
        if (TranscriptionModel.Length > 100 || CorrectionModel.Length > 100 || Vocabulary.Length > 2000)
            throw new ArgumentException("Слишком длинное значение настройки.");
        if (LocalDelayMilliseconds is < 0 or > 10000 || CorrectionTimeoutSeconds is < 1 or > 60 ||
            ProcessingTimeoutSeconds is < 10 or > 600 || MaxRecordingSeconds is < 1 or > 300)
            throw new ArgumentException("Значение тайм-аута вне допустимого диапазона.");
        if (!HotkeyControl && !HotkeyAlt && !HotkeyShift) throw new ArgumentException("Хоткей должен содержать модификатор.");
        if (!(HotkeyVirtualKey == 0x20 || HotkeyVirtualKey is >= 0x41 and <= 0x5A || HotkeyVirtualKey is >= 0x70 and <= 0x7B))
            throw new ArgumentException("Для хоткея используйте Space, A–Z или F1–F12.");
    }
}

public sealed class SettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string DirectoryPath { get; } = directory;
    public string ModelsDirectory => Path.Combine(DirectoryPath, "Models");
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public string? LoadWarning { get; private set; }

    public AppSettings Load(AppSettings? firstRunDefaults = null)
    {
        LoadWarning = null;
        if (!File.Exists(SettingsPath)) return firstRunDefaults ?? new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
            if (settings.SchemaVersion == 1 || settings.LocalModel is "ggml-base.bin" or "ggml-small.bin" or "ggml-large-v3-turbo.bin")
            {
                settings = settings with { SchemaVersion = 2, LocalModel = ModelCatalog.ParakeetId, Acceleration = AccelerationMode.Cpu };
                LoadWarning = "Локальный движок заменён на sherpa-onnx / Parakeet TDT v3 (CPU). Скачайте новую модель. Остальные настройки сохранены; старые модели не удалены. Файл обновится после сохранения.";
            }
            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            LoadWarning = "Не удалось прочитать настройки. Используются стандартные значения; исходный файл не изменён.";
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(DirectoryPath);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, SettingsPath, true);
    }
}

public sealed record ModelFile(string Name, long Size, string Sha256);
public sealed record ModelInfo(string FileName, string Title, string Repository, string Revision, IReadOnlyList<ModelFile> Files)
{
    public long TotalSize => Files.Sum(f => f.Size);
    public string DirectoryName => FileName + "-" + Revision;
    public string GetDirectory(string root) => Path.Combine(root, DirectoryName);
    public bool IsInstalled(string root) => Files.All(f =>
    {
        var file = new FileInfo(Path.Combine(GetDirectory(root), f.Name));
        return file.Exists && file.Length == f.Size;
    });
}
public static class ModelCatalog
{
    public const string ParakeetId = "parakeet-tdt-0.6b-v3-int8";
    // Pinned export from the sherpa-onnx maintainer; no mutable main-branch model downloads.
    public static IReadOnlyList<ModelInfo> All { get; } = Array.AsReadOnly(new[]
    {
        new ModelInfo(ParakeetId, "NVIDIA Parakeet TDT v3 · 0,6B · INT8 · 640 МиБ · CPU",
            "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8",
            "2bda32ec70b097a55adaa07d9a7173915b43cc78", Array.AsReadOnly(new[]
            {
                new ModelFile("encoder.int8.onnx", 652184281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
                new ModelFile("decoder.int8.onnx", 11845275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
                new ModelFile("joiner.int8.onnx", 6355277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
                new ModelFile("tokens.txt", 93939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d")
            }))
    });
    public static ModelInfo Get(string id) => All.First(m => m.FileName == id);
}
