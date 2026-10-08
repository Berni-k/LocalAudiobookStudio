using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Timeline;
using LocalAudiobookStudio.Engine.Export;
using LocalAudiobookStudio.Workers.Manifest;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// Assets panel: the files in the project's assets/ folder and its subfolders. Audio files are ready to insert
/// as clips; ComfyUI workflows (*.comfy.json) are picked in a speaker's settings. The folder tree is shown as a
/// flat list (folder rows expand and collapse, their contents indented), so the list keeps its look and its
/// drag and drop.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The rows shown: folders, and the files of expanded folders.</summary>
    public ObservableCollection<AssetViewModel> Assets { get; } = new();

    /// <summary>Every file, whether its folder is expanded or not (the Speakers dialog's reference clips).</summary>
    public ObservableCollection<AssetViewModel> AssetFiles { get; } = new();

    /// <summary>All rows in tree order; <see cref="Assets"/> is this without the contents of collapsed folders.</summary>
    private List<AssetViewModel> _assetRows = [];

    /// <summary>Collapsed folders by path, kept across rescans while the project is open.</summary>
    private readonly HashSet<string> _collapsedAssetFolders = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _hasAssets;

    /// <summary>Rescans assets/ (durations are analyzed in the background on first sight).</summary>
    public async Task RefreshAssetsAsync()
    {
        if (Session is not { } s)
        {
            _assetRows = [];
            Assets.Clear();
            AssetFiles.Clear();
            HasAssets = false;
            return;
        }
        var root = s.Paths.AssetsDir;
        var rows = await Task.Run(() =>
        {
            var list = new List<AssetViewModel>();
            if (Directory.Exists(root)) Scan(root, 0);
            return list;

            // Per folder: subfolders, then audio, then workflows, each by name.
            void Scan(string dir, int depth)
            {
                foreach (var sub in Directory.EnumerateDirectories(dir).Order(StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(new AssetViewModel(s.Paths.MakeRelative(sub), AssetKind.Folder, depth));
                    Scan(sub, depth + 1);
                }
                var files = Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var f in files.Where(f => ProjectSession.AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
                {
                    var rel = s.Paths.MakeRelative(f);
                    list.Add(new AssetViewModel(rel, AssetKind.Audio, depth, (s.Takes.Clip(rel) ?? s.Takes.AnalyzeClip(rel))?.DurationMs ?? 0));
                }
                foreach (var f in files.Where(f => f.EndsWith(ComfyWorkflow.Extension, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new AssetViewModel(s.Paths.MakeRelative(f), AssetKind.Workflow, depth));
            }
        });
        if (!ReferenceEquals(Session, s)) return;
        foreach (var folder in rows.Where(r => r.IsFolder)) folder.IsExpanded = !_collapsedAssetFolders.Contains(folder.RelativePath);
        _assetRows = rows;
        AssetFiles.Clear();
        foreach (var f in rows.Where(r => !r.IsFolder)) AssetFiles.Add(f);
        ShowAssetRows();
        UpdateAssetUsage();
    }

    /// <summary>Fills <see cref="Assets"/> from all rows, leaving out what collapsed folders contain.</summary>
    private void ShowAssetRows()
    {
        Assets.Clear();
        int? hiddenBelow = null; // depth of the collapsed folder whose contents are being skipped
        foreach (var row in _assetRows)
        {
            if (hiddenBelow is int d && row.Depth > d) continue;
            hiddenBelow = row.IsFolder && !row.IsExpanded ? row.Depth : null;
            Assets.Add(row);
        }
        HasAssets = _assetRows.Count > 0;
    }

    [RelayCommand]
    private void ToggleAssetFolder(AssetViewModel? folder)
    {
        if (folder is not { IsFolder: true }) return;
        folder.IsExpanded = !folder.IsExpanded;
        if (folder.IsExpanded) _collapsedAssetFolders.Remove(folder.RelativePath);
        else _collapsedAssetFolders.Add(folder.RelativePath);
        ShowAssetRows();
    }

    /// <summary>
    /// Recounts how often each asset is used: by clip segments, and by speaker settings and segment overrides
    /// that name it (a ComfyUI workflow, or an audio file for one of its nodes).
    /// </summary>
    private void UpdateAssetUsage()
    {
        if (Session == null || AssetFiles.Count == 0) return;
        var items = Session.Project.AllItems().ToList();
        var uses = items
            .Where(i => i.Kind == ItemKind.Clip && i.ClipFile != null)
            .Select(i => i.ClipFile!)
            .Concat(Session.Project.Speakers.Values.SelectMany(sp => AssetsIn(sp.Settings)))
            .Concat(items.SelectMany(i => AssetsIn(i.SettingsOverride)))
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var a in AssetFiles) a.UseCount = uses.GetValueOrDefault(a.RelativePath);

        static IEnumerable<string> AssetsIn(JsonObject? settings) =>
            settings?.Select(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => s != null && s.StartsWith(ProjectPaths.AssetsFolder + "/", StringComparison.OrdinalIgnoreCase)).Cast<string>() ?? [];
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private Task ImportAssets() => ImportAssetsIntoAsync(null);

    /// <summary>Asks for files and adds them to an assets folder (null: assets/ itself).</summary>
    public async Task ImportAssetsIntoAsync(AssetViewModel? folder)
    {
        var audio = ProjectSession.AudioExtensions.Select(e => "*" + e).ToArray();
        var files = await Dialogs.OpenFiles("Add assets", true,
            new FileFilter("Audio and ComfyUI workflows", [.. audio, "*.json"]),
            new FileFilter("Audio", audio),
            new FileFilter("ComfyUI workflows (API format)", "*.json"));
        await ImportAssetFilesAsync(files, folder);
    }

    /// <summary>True for the files the Assets panel takes: audio, and ComfyUI workflows (any .json).</summary>
    public static bool IsAssetFile(string path) =>
        ProjectSession.AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())
        || Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies audio files and ComfyUI workflows into an assets folder (null: assets/ itself). Also used when files
    /// are dropped on the Assets panel.
    /// </summary>
    public async Task ImportAssetFilesAsync(IReadOnlyList<string> files, AssetViewModel? folder = null)
    {
        if (Session == null || files.Count == 0) return;
        var added = 0;
        foreach (var f in files)
        {
            try
            {
                StatusText = $"Importing {Path.GetFileName(f)}…";
                await Session.ImportAssetAsync(f, folder?.FolderUnderAssets);
                added++;
            }
            catch (Exception ex)
            {
                await Dialogs.ShowError("Cannot add asset", $"{f}\n\n{ex.Message}");
            }
        }
        if (folder != null) _collapsedAssetFolders.Remove(folder.RelativePath);
        await RefreshAssetsAsync();
        StatusText = $"Added {added} asset(s)";
    }

    /// <summary>A folder row itself, or the folder row a file is in (null: directly in assets/).</summary>
    public AssetViewModel? AssetFolderOf(AssetViewModel row)
    {
        if (row.IsFolder) return row;
        var parent = Path.GetDirectoryName(row.RelativePath)?.Replace('\\', '/');
        return _assetRows.FirstOrDefault(r => r.IsFolder && string.Equals(r.RelativePath, parent, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Creates a folder in assets/ or in <paramref name="parent"/>.</summary>
    public async Task NewAssetFolderAsync(AssetViewModel? parent)
    {
        if (Session is not { } s) return;
        var name = await Dialogs.Prompt("New folder", "Folder name");
        if (string.IsNullOrWhiteSpace(name)) return;
        var safe = AudiobookExporter.SafeFileName(name.Trim());
        var dir = Path.Combine(parent == null ? s.Paths.AssetsDir : s.Paths.Resolve(parent.RelativePath), safe);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("New folder", "The folder could not be created:\n" + ex.Message);
            return;
        }
        if (parent != null) _collapsedAssetFolders.Remove(parent.RelativePath);
        await RefreshAssetsAsync();
    }

    /// <summary>Inserts an asset as a clip segment at a position in a chapter.</summary>
    public void InsertAsset(AssetViewModel asset, ChapterViewModel chapter, int index)
    {
        if (Session == null || !asset.IsAudio) return;
        var model = new TimelineItem { Kind = ItemKind.Clip, ClipFile = asset.RelativePath, Label = asset.Name };
        Session.InsertItems(chapter, index, [model], "Insert clip");
        if (Session.ItemVm(model.Id) is { } vm) SelectAndReveal(vm);
    }

    /// <summary>Inserts an asset below the selection (or at the end of the chapter).</summary>
    public void InsertAssetAtSelection(AssetViewModel asset)
    {
        if (InsertionPoint(true) is { } p) InsertAsset(asset, p.chapter, p.index);
    }

    public async Task DeleteAssetAsync(AssetViewModel asset)
    {
        if (Session is not { } s) return;
        if (asset.IsFolder)
        {
            await DeleteAssetFolderAsync(asset);
            return;
        }
        UpdateAssetUsage(); // speaker edits don't recount, so count now
        if (asset.UseCount > 0)
        {
            await Dialogs.ShowError("Asset in use",
                $"\"{asset.Name}\" is used {asset.UseCount} time(s), by clip segments or speaker settings. " +
                "Delete those segments or choose another file in those settings first.");
            return;
        }
        if (!await Dialogs.Confirm("Delete asset", $"Delete \"{asset.Name}\" from the project?\n\nThe file is removed from disk. This can't be undone.", "Delete"))
            return;
        try
        {
            File.Delete(s.Paths.Resolve(asset.RelativePath));
            try { File.Delete(s.Paths.ClipPeaks(asset.RelativePath)); } catch (IOException) { /* rebuildable cache */ }
            _assetRows.Remove(asset);
            AssetFiles.Remove(asset);
            ShowAssetRows();
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Delete asset", "The file could not be deleted:\n" + ex.Message);
        }
    }

    /// <summary>Deletes a folder that holds no files (empty subfolders go with it).</summary>
    private async Task DeleteAssetFolderAsync(AssetViewModel folder)
    {
        if (Session is not { } s) return;
        var dir = s.Paths.Resolve(folder.RelativePath);
        if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
        {
            await Dialogs.ShowError("Folder not empty", $"\"{folder.Name}\" still holds files. Delete them first.");
            return;
        }
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Delete folder", "The folder could not be deleted:\n" + ex.Message);
        }
        await RefreshAssetsAsync();
    }

    // ------------------------------------------------------------------ preview playback

    private AssetViewModel? _playingAsset;

    /// <summary>The asset's play button: plays the file on its own, or stops it if it is playing.</summary>
    [RelayCommand]
    private async Task ToggleAssetPlayback(AssetViewModel? asset)
    {
        if (asset == null || Session == null || !asset.IsAudio) return;
        if (asset.IsPlaying)
        {
            StopPlayback();
            return;
        }
        await PlayFileAsync(Session.Paths.Resolve(asset.RelativePath));
        if (!IsPlaying) return;
        _playingAsset = asset;
        asset.IsPlaying = true;
    }

    /// <summary>Called whenever playback stops, pauses, ends or switches to something else.</summary>
    private void ClearAssetPlayback()
    {
        if (_playingAsset == null) return;
        _playingAsset.IsPlaying = false;
        _playingAsset = null;
    }

    public void RevealAsset(AssetViewModel asset)
    {
        if (Session != null) Dialogs.Reveal(Session.Paths.Resolve(asset.RelativePath));
    }
}

public enum AssetKind { Folder, Audio, Workflow }

/// <summary>One row of the Assets panel: a folder, an audio file or a ComfyUI workflow.</summary>
public sealed partial class AssetViewModel(string relativePath, AssetKind kind, int depth, double durationMs = 0) : ObservableObject
{
    /// <summary>Project-relative, with forward slashes ("assets/Voice Reference/narrator.wav").</summary>
    public string RelativePath { get; } = relativePath;
    public AssetKind Kind { get; } = kind;
    public bool IsFolder => Kind == AssetKind.Folder;
    public bool IsAudio => Kind == AssetKind.Audio;
    public bool IsWorkflow => Kind == AssetKind.Workflow;

    /// <summary>Nesting level below assets/ (0 = directly in it).</summary>
    public int Depth { get; } = depth;
    public double IndentWidth => Depth * 14;

    public string Name { get; } = kind switch
    {
        AssetKind.Workflow => Path.GetFileName(relativePath)[..^ComfyWorkflow.Extension.Length],
        AssetKind.Folder => Path.GetFileName(relativePath),
        _ => Path.GetFileNameWithoutExtension(relativePath),
    };

    /// <summary>The name with its folders below assets/ ("Voice Reference/narrator"), for menus.</summary>
    public string DisplayPath
    {
        get
        {
            var folder = Path.GetDirectoryName(FolderUnderAssets)?.Replace('\\', '/');
            return string.IsNullOrEmpty(folder) ? Name : folder + "/" + Name;
        }
    }

    /// <summary>The path below assets/ ("Voice Reference" for that folder).</summary>
    public string FolderUnderAssets =>
        RelativePath.Length > ProjectPaths.AssetsFolder.Length ? RelativePath[(ProjectPaths.AssetsFolder.Length + 1)..] : "";

    public double DurationMs { get; } = durationMs;
    public string DurationText => TimeFormat.Short(DurationMs);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip), nameof(IsUnused))]
    private int _useCount;

    public bool IsUnused => UseCount == 0 && !IsFolder;

    /// <summary>For folders: its contents are shown.</summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>True while this asset is being previewed (its play button shows stop).</summary>
    [ObservableProperty] private bool _isPlaying;

    public string Tooltip => Kind switch
    {
        AssetKind.Folder => $"{RelativePath}\nDrop files here to add them to this folder",
        AssetKind.Workflow => $"{RelativePath}\nComfyUI workflow\n" +
                              (UseCount == 0 ? "Not used yet" : $"Used {UseCount} time(s)") +
                              "\nChoose it as the Workflow of a speaker on the ComfyUI bridge",
        _ => $"{RelativePath}\nLength: {DurationText}\n" +
             (UseCount == 0 ? "Not used yet" : $"Used {UseCount} time(s)") +
             "\nDouble-click to insert below the selection, or drag into the segment list",
    };

    public string PlayTooltip => $"Play ({DurationText})";
}
