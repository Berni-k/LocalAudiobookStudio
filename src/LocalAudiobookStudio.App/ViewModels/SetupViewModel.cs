using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Settings;
using LocalAudiobookStudio.Workers.Environments;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// The setup window, shown before the main window on a first start (or until setup was completed): where
/// settings and data live (portable or user profile), where environments and models go, a check of what
/// the machine has, and an offer to install Kokoro. Runs before <see cref="AppHost.Initialize"/>, so nothing
/// is written until Continue.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private bool _dataRootEdited;
    private bool _settingDefault;

    /// <param name="detected">The storage mode found on disk, or null on a true first start (the user chooses).</param>
    /// <param name="settings">The settings found there (a new instance on a first start).</param>
    public SetupViewModel(StorageMode? detected, AppSettings settings)
    {
        Settings = settings;
        IsModeLocked = detected != null;
        PortableAvailable = detected == StorageMode.Portable || SettingsStore.AppDirWritable();
        // An unzipped copy is the usual case: suggest keeping it self-contained when the folder allows it.
        _isPortable = detected == StorageMode.Portable || (detected == null && PortableAvailable);
        _dataRoot = string.IsNullOrWhiteSpace(settings.DataRoot) ? DefaultDataRootFor(Mode) : SettingsStore.ResolveDataRoot(settings);
        _dataRootEdited = !string.IsNullOrWhiteSpace(settings.DataRoot);
        _canInstallKokoro = EnvironmentManager.FindHartsyWorker() != null;
        _installKokoro = _canInstallKokoro;
        Disk = SystemChecks.DiskCheck(_dataRoot);
        ExampleProject = FindExampleProject();
        _openExample = ExampleProject != null;
        _ = RunChecksAsync();
    }

    public AppSettings Settings { get; }

    // ------------------------------------------------------------------ where things go

    /// <summary>The mode was found on disk (a "data" folder or a profile settings file): shown, not chosen.</summary>
    public bool IsModeLocked { get; }
    public bool CanChooseMode => !IsModeLocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUserProfile), nameof(Mode))]
    private bool _isPortable;

    public bool IsUserProfile
    {
        get => !IsPortable;
        set => IsPortable = !value;
    }

    public StorageMode Mode => IsPortable && PortableAvailable ? StorageMode.Portable : StorageMode.UserProfile;

    public bool PortableAvailable { get; }
    public string PortablePath => SettingsStore.PortableDir;
    public string PortableNote => PortableAvailable
        ? "Everything stays in this folder: move, copy or delete the folder and the app goes with it."
        : "Not available: the app folder can't be written to (for example under Program Files).";
    public string UserProfilePath => SettingsStore.UserDataRoot;
    public string LockedNote => SettingsStore.Mode == StorageMode.Portable
        ? "Portable: found the 'data' folder next to the app."
        : "User profile: settings found in";
    public string LockedPath => SettingsStore.Mode == StorageMode.Portable ? SettingsStore.PortableDir : SettingsStore.SettingsFile;

    /// <summary>Environments, models and caches; follows the mode until the user edits it.</summary>
    [ObservableProperty] private string _dataRoot;

    partial void OnIsPortableChanged(bool value)
    {
        OnPropertyChanged(nameof(DriveWarning));
        if (_dataRootEdited) return;
        _settingDefault = true;
        try { DataRoot = DefaultDataRootFor(Mode); }
        finally { _settingDefault = false; }
    }

    partial void OnDataRootChanged(string value)
    {
        if (!_settingDefault) _dataRootEdited = true;
        Disk = SystemChecks.DiskCheck(value);
        OnPropertyChanged(nameof(DriveWarning));
    }

    /// <summary>Set when the folder is on another drive than the settings (the app folder when portable).</summary>
    public string? DriveWarning
    {
        get
        {
            var settingsDir = Mode == StorageMode.Portable ? SettingsStore.AppDir : SettingsStore.UserDataRoot;
            try
            {
                if (string.IsNullOrWhiteSpace(DataRoot) || DataRootPaths.SameVolume(DataRoot, settingsDir)) return null;
            }
            catch
            {
                return null; // not a valid path yet (still typing)
            }
            return Mode == StorageMode.Portable
                ? "On another drive than the app: moving or copying the app folder won't take the environments and models along."
                : $"On another drive than the settings ({Path.GetPathRoot(settingsDir)}): keep that drive connected when you use the app.";
        }
    }

    private static string DefaultDataRootFor(StorageMode mode) =>
        mode == StorageMode.Portable ? SettingsStore.PortableDir : SettingsStore.UserDataRoot;

    [RelayCommand]
    private async Task Browse()
    {
        var f = await Dialogs.PickFolder("Environments, models and caches");
        if (f != null) DataRoot = f;
    }

    // ------------------------------------------------------------------ system check

    public ObservableCollection<SystemCheckRow> Checks { get; } = new();
    [ObservableProperty] private SystemCheck _disk;
    [ObservableProperty] private bool _isChecking;

    [RelayCommand]
    private async Task RunChecksAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        try
        {
            var results = await SystemChecks.RunAsync(Settings.FfmpegPath, DataRoot, Devices.DeviceChecks.Audio);
            Checks.Clear();
            foreach (var c in results) Checks.Add(new SystemCheckRow(c, () => DataRoot, OnFixedAsync));
            Disk = SystemChecks.DiskCheck(DataRoot);
        }
        finally
        {
            IsChecking = false;
        }
    }

    // ------------------------------------------------------------------ quick test

    [ObservableProperty] private bool _installKokoro;
    /// <summary>
    /// Kokoro can be installed: the bundled HartsyInference worker is there (Kokoro's default runtime, so the quick
    /// test needs neither uv nor Python).
    /// </summary>
    [ObservableProperty] private bool _canInstallKokoro;

    /// <summary>After a download (into the chosen data folder): the checks run again.</summary>
    private Task OnFixedAsync(CheckFixKind kind, string path) => RunChecksAsync();

    // ------------------------------------------------------------------ example project

    /// <summary>The first bundled example (alphabetically) in the app's examples folder, or null when there is none.</summary>
    public string? ExampleProject { get; }
    public bool HasExampleProject => ExampleProject != null;
    public string OpenExampleText => $"Open the example project ({Path.GetFileNameWithoutExtension(ExampleProject)}) after setup";
    [ObservableProperty] private bool _openExample;

    private static string? FindExampleProject()
    {
        var dir = Path.Combine(SettingsStore.AppDir, "examples");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault(ProjectStore.IsProjectFolder);
    }

    /// <summary>Applies the choices: switches to the chosen mode and saves the settings there.</summary>
    public void Apply()
    {
        if (!IsModeLocked) SettingsStore.UseMode(Mode);
        Settings.DataRoot = SettingsStore.ToStoredDataRoot(DataRoot);
        Settings.FirstRunCompleted = true;
        SettingsStore.Save(Settings);
    }
}
