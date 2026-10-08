using LocalAudiobookStudio.Core.Settings;

namespace LocalAudiobookStudio.Tests;

/// <summary>Data root resolution. Runs in the default user-profile mode (the mode is process-wide).</summary>
public sealed class SettingsStoreTests
{
    [Fact]
    public void No_data_root_setting_means_the_default()
    {
        Assert.Equal(SettingsStore.DefaultDataRoot, SettingsStore.ResolveDataRoot(new AppSettings()));
        Assert.Null(SettingsStore.ToStoredDataRoot(SettingsStore.DefaultDataRoot));
        Assert.Null(SettingsStore.ToStoredDataRoot(SettingsStore.DefaultDataRoot + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_relative_data_root_is_relative_to_the_app_folder()
    {
        var resolved = SettingsStore.ResolveDataRoot(new AppSettings { DataRoot = Path.Combine("big", "models") });
        Assert.Equal(Path.GetFullPath(Path.Combine(SettingsStore.AppDir, "big", "models")), resolved);
    }

    [Fact]
    public void Outside_portable_mode_a_chosen_data_root_is_stored_absolute()
    {
        var folder = Path.Combine(SettingsStore.AppDir, "elsewhere");
        Assert.Equal(StorageMode.UserProfile, SettingsStore.Mode);
        Assert.Equal(Path.GetFullPath(folder), SettingsStore.ToStoredDataRoot(folder));
    }
}
