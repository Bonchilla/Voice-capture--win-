using System.Text.Json;
using VoiceCapture.Core;

namespace VoiceCapture.Tests;

public class HardwareProfileTests
{
    [Theory]
    [InlineData("Intel(R) UHD Graphics 630")]
    [InlineData("Intel(R) UHD Graphics 770")]
    [InlineData("Intel UHD Graphics")]
    [InlineData("NVIDIA GeForce RTX 4060")]
    [InlineData("AMD Radeon RX 7600")]
    [InlineData("Intel Arc A770")]
    public void ParakeetUsesCpuRegardlessOfGraphics(string adapter)
    {
        var profile = new HardwareProfile([adapter]);
        Assert.False(profile.ShouldUseGpu(AccelerationMode.Auto));
        Assert.False(profile.ShouldUseGpu(AccelerationMode.Cpu));
        Assert.Throws<ArgumentOutOfRangeException>(() => profile.ShouldUseGpu(AccelerationMode.Vulkan));
        Assert.Equal(ModelCatalog.ParakeetId, profile.RecommendedModel);
    }

    [Fact]
    public void MixedAndUnknownAdaptersDoNotEnableGpu()
    {
        Assert.False(new HardwareProfile([]).ShouldUseGpu(AccelerationMode.Auto));
        var mixed = new HardwareProfile(["Intel UHD 630", "NVIDIA RTX 4060"]);
        Assert.True(mixed.HasIntelUhd); Assert.False(mixed.IntelUhdOnly);
        Assert.False(mixed.ShouldUseGpu(AccelerationMode.Auto));
        Assert.False(new HardwareProfile(["Intel Arc A770"]).HasIntelUhd);
    }

    [Fact]
    public void RecommendationPreservesPrivacyAndUserPreferences()
    {
        var settings = new AppSettings { Mode = RecognitionMode.Local, CorrectionEnabled = false,
            CloudConsent = false, Vocabulary = "custom", LocalDelayMilliseconds = 1234, Acceleration = AccelerationMode.Cpu };
        var result = new HardwareProfile(["Intel UHD 630"]).ApplyRecommendation(settings);
        Assert.Equal(settings with { Acceleration = AccelerationMode.Auto }, result);
    }

    [Theory]
    [InlineData("ggml-base.bin", 0)]
    [InlineData("ggml-small.bin", 1)]
    [InlineData("ggml-large-v3-turbo.bin", 2)]
    public void LegacySettingsMigrateOnlyLocalEngineWithoutWritingFile(string oldModel, int acceleration)
    {
        string directory = Path.Combine(Path.GetTempPath(), "VoiceCaptureMigration-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(directory);
            var old = new AppSettings { SchemaVersion = 1, LocalModel = oldModel, Acceleration = (AccelerationMode)acceleration,
                Mode = RecognitionMode.Local, CloudConsent = false, LocalCloudCorrection = false, Language = "ru",
                Vocabulary = "Термины", MicrophoneId = "chosen", HotkeyVirtualKey = 0x71 };
            string json = JsonSerializer.Serialize(old);
            File.WriteAllText(store.SettingsPath, json);
            var current = store.Load();
            Assert.Equal(old with { SchemaVersion = 2, LocalModel = ModelCatalog.ParakeetId, Acceleration = AccelerationMode.Cpu }, current);
            Assert.NotNull(store.LoadWarning);
            Assert.Equal(json, File.ReadAllText(store.SettingsPath));
            store.Save(current);
            Assert.Equal(current, store.Load());
            Assert.Null(store.LoadWarning);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ExistingCurrentSettingsAreNotOverriddenByHardwareDefaults()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VoiceCaptureMigration-" + Guid.NewGuid());
        try
        {
            var store = new SettingsStore(directory);
            var first = new AppSettings { Mode = RecognitionMode.Local };
            Assert.Equal(first, store.Load(first));
            var existing = new AppSettings { Language = "en", Acceleration = AccelerationMode.Cpu };
            store.Save(existing);
            Assert.Equal(existing, store.Load(first));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FutureSchemaAndUnsupportedAccelerationAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new AppSettings { SchemaVersion = 99 }.Validate());
        Assert.Throws<ArgumentException>(() => new AppSettings { Acceleration = AccelerationMode.Vulkan }.Validate());
    }
}
