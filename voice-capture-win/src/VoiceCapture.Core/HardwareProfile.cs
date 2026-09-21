namespace VoiceCapture.Core;

/// <summary>Display detection only; Parakeet uses the shipped ONNX Runtime CPU provider.</summary>
public sealed record HardwareProfile(IReadOnlyList<string> Adapters)
{
    public bool HasIntelUhd => Adapters.Any(IsIntelUhd);
    public bool IntelUhdOnly => HasIntelUhd && Adapters.All(IsIntelUhd);
    public bool PreferCpu => true;
    public string RecommendedModel => ModelCatalog.ParakeetId;
    public string Description => Adapters.Count == 0 ? "Графический адаптер не определён" : string.Join("; ", Adapters);
    public string Recommendation => "Parakeet TDT v3 INT8 работает на CPU через sherpa-onnx. Видеокарта NVIDIA не требуется; Intel UHD/AMD не используются для вычислений. CUDA/Vulkan в этой поставке отсутствуют.";

    public bool ShouldUseGpu(AccelerationMode mode) => mode switch
    {
        AccelerationMode.Auto or AccelerationMode.Cpu => false,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), "Этот режим не поддерживается Parakeet CPU.")
    };

    public AppSettings ApplyRecommendation(AppSettings settings) => settings with
    {
        LocalModel = RecommendedModel,
        Acceleration = AccelerationMode.Auto
    };

    private static bool IsIntelUhd(string name) => name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
        && name.Contains("UHD", StringComparison.OrdinalIgnoreCase);
}
