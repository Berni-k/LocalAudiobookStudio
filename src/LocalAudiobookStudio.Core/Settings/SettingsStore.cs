using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Serialization;

namespace LocalAudiobookStudio.Core.Settings;

/// <summary>Where the settings and the data root live.</summary>
public enum StorageMode
{
    /// <summary>Everything in a <c>data</c> folder next to the executable (an unzipped copy of the app).</summary>
    Portable,
    /// <summary>The user's profile: settings and data together in %LOCALAPPDATA% (~/.local/share on Linux).</summary>
    UserProfile,
}

/// <summary>Fixed per-machine locations and loading/saving of <see cref="AppSettings"/>.</summary>
public static class SettingsStore
{
    public const string AppFolderName = "LocalAudiobookStudio";

    /// <summary>The portable data folder's name, next to the executable (VS Code's portable mode uses the same).</summary>
    public const string PortableFolderName = "data";

    /// <summary>The folder holding the executable.</summary>
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>Portable mode's folder: settings.json and the default data root.</summary>
    public static string PortableDir => Path.Combine(AppDir, PortableFolderName);

    /// <summary>Chosen at startup (<see cref="DetectMode"/>, <see cref="UseMode"/>); the user profile until then.</summary>
    public static StorageMode Mode { get; private set; } = StorageMode.UserProfile;

    /// <summary>
    /// Portable when a <c>data</c> folder sits next to the executable, the user profile when its settings file
    /// exists, otherwise null: a first start, where the user is asked.
    /// </summary>
    public static StorageMode? DetectMode() =>
        Directory.Exists(PortableDir) ? StorageMode.Portable
        : File.Exists(Path.Combine(UserDataRoot, SettingsFileName)) || File.Exists(LegacySettingsFile) ? StorageMode.UserProfile
        : null;

    /// <summary>Switches to a mode; portable creates the <c>data</c> folder, which is what marks the mode on later starts.</summary>
    public static void UseMode(StorageMode mode)
    {
        Mode = mode;
        if (mode == StorageMode.Portable) Directory.CreateDirectory(PortableDir);
        else MoveLegacySettings();
    }

    /// <summary>Before 2026-10-04 the settings were in %APPDATA% (~/.config); they now sit with the data.</summary>
    private static string LegacySettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName, SettingsFileName);

    private static void MoveLegacySettings()
    {
        try
        {
            var target = Path.Combine(UserDataRoot, SettingsFileName);
            if (!File.Exists(LegacySettingsFile) || File.Exists(target)) return;
            Directory.CreateDirectory(UserDataRoot);
            File.Move(LegacySettingsFile, target);
            var oldDir = Path.GetDirectoryName(LegacySettingsFile)!;
            if (!Directory.EnumerateFileSystemEntries(oldDir).Any()) Directory.Delete(oldDir);
        }
        catch
        {
            // Left where it is: the app starts with default settings rather than not at all.
        }
    }

    /// <summary>True when the app folder can be written to (portable mode needs it; Program Files can't).</summary>
    public static bool AppDirWritable()
    {
        try
        {
            var probe = Path.Combine(AppDir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>%LOCALAPPDATA%\LocalAudiobookStudio on Windows, ~/.local/share/LocalAudiobookStudio on Linux.</summary>
    public static string UserDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    /// <summary>The folder holding settings.json in the current mode.</summary>
    public static string ConfigDir => Mode == StorageMode.Portable ? PortableDir : UserDataRoot;

    /// <summary>The data root when the settings don't name another one.</summary>
    public static string DefaultDataRoot => Mode == StorageMode.Portable ? PortableDir : UserDataRoot;

    private const string SettingsFileName = "settings.json";
    public static string SettingsFile => Path.Combine(ConfigDir, SettingsFileName);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                return CoreJson.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
            }
        }
        catch
        {
            // A corrupt settings file must never prevent the app from starting; keep a copy for inspection.
            try { File.Copy(SettingsFile, SettingsFile + ".bad", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings) =>
        AtomicFile.WriteAllBytes(SettingsFile, CoreJson.SerializeToUtf8(settings));

    /// <summary>The data root in use. A relative setting is relative to the app folder (portable copies stay movable).</summary>
    public static string ResolveDataRoot(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DataRoot) ? DefaultDataRoot
        : Path.IsPathRooted(settings.DataRoot) ? settings.DataRoot!
        : Path.GetFullPath(Path.Combine(AppDir, settings.DataRoot!));

    /// <summary>
    /// How to store a chosen data root: null for the default; in portable mode, relative when it is inside the
    /// app folder, so the folder still works after being moved or copied to another drive.
    /// </summary>
    public static string? ToStoredDataRoot(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), DefaultDataRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return null;
        if (Mode == StorageMode.Portable)
        {
            var relative = Path.GetRelativePath(AppDir, full);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)) return relative;
        }
        return full;
    }
}
