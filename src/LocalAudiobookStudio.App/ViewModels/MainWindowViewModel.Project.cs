using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Engine.Export;
using LocalAudiobookStudio.Workers.Scheduling;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>File menu, chapters, and the secondary windows.</summary>
public sealed partial class MainWindowViewModel
{
    // ================================================================== projects

    [RelayCommand]
    private async Task NewProject()
    {
        var title = await Dialogs.Prompt("New project", "Book title:", "My Book");
        if (string.IsNullOrWhiteSpace(title)) return;
        var folder = await ChooseNewProjectFolder(title);
        if (folder == null) return;
        try
        {
            var session = await ProjectSession.CreateAsync(folder, title.Trim());
            await SetSessionAsync(session);
            session.AddChapter("Chapter 1");
            session.Undo.Clear();
            StatusText = $"Created {folder}. Use File → Import text to add a book.";
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Cannot create project", ex.Message);
        }
    }

    /// <summary>Asks for a parent folder and returns a new, empty project folder inside it.</summary>
    private static async Task<string?> ChooseNewProjectFolder(string title)
    {
        var parent = await Dialogs.PickFolder("Choose where to create the project folder");
        if (parent == null) return null;
        // An empty folder is used directly; otherwise a sub-folder named after the book is created.
        if (!Directory.EnumerateFileSystemEntries(parent).Any()) return parent;
        return FreeProjectFolder(parent, AudiobookExporter.SafeFileName(title));
    }

    /// <summary>"name.audiobook" in <paramref name="parent"/>, or "name (2).audiobook" … when that one is taken.</summary>
    private static string FreeProjectFolder(string parent, string name)
    {
        var folder = Path.Combine(parent, name + ProjectFile.FolderExtension);
        for (int n = 2; Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(); n++)
            folder = Path.Combine(parent, $"{name} ({n}){ProjectFile.FolderExtension}");
        return folder;
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        var folder = await Dialogs.PickFolder("Open project folder");
        if (folder == null) return;
        if (!ProjectStore.IsProjectFolder(folder))
        {
            await Dialogs.ShowError("Not a project", "That folder is not a Local Audiobook Studio project. Choose a project folder, or use File → Unpack project for .abproj files.");
            return;
        }
        await OpenProjectAsync(folder);
    }

    [RelayCommand]
    private async Task OpenRecent(string? folder)
    {
        if (folder == null) return;
        if (!ProjectStore.IsProjectFolder(folder))
        {
            AppHost.Settings.RecentProjects.Remove(folder);
            AppHost.SaveSettings();
            RefreshRecent();
            await Dialogs.ShowError("Project not found", folder);
            return;
        }
        await OpenProjectAsync(folder);
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private async Task CloseProject()
    {
        await CloseProjectAsync();
        StatusText = "Project closed";
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private async Task Save()
    {
        if (Session == null) return;
        await Session.FlushAsync();
        StatusText = "All changes saved";
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private async Task ProjectProperties()
    {
        if (Session != null) await WindowService.ShowProjectPropertiesAsync(Session);
        OnPropertyChanged(nameof(WindowTitle));
    }

    // ================================================================== text import / export

    [RelayCommand]
    private async Task ImportText()
    {
        var file = await Dialogs.OpenFile("Import text", new FileFilter("Text files", "*.txt", "*.md"));
        if (file == null) return;
        await ImportTextFileAsync(file);
    }

    public async Task ImportTextFileAsync(string file)
    {
        string text;
        try { text = await Task.Run(() => TextDecoding.ReadFile(file)); }
        catch (Exception ex) { await Dialogs.ShowError("Cannot read file", ex.Message); return; }

        var vm = new ImportViewModel(Session, file, text, SelectedChapter);
        var ok = await WindowService.ShowImportAsync(vm);
        if (!ok) return;
        try
        {
            await ApplyImportAsync(vm);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Import failed", ex.Message);
        }
    }

    /// <summary>Imports a text file into a new project with default options (command line).</summary>
    private async Task ImportHeadlessAsync(string file, string folder)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = await Task.Run(() => TextDecoding.ReadFile(file));
            var result = await Task.Run(() => TextImporter.Parse(text, new ImportOptions()));
            var speakers = ProjectImport.SpeakersForLabels(AppHost.Host!, result.SpeakerLabels);
            var chapters = TextImporter.ToChapters(result, l => l == null ? "narrator" : Core.Model.Speaker.MakeId(l));
            await RunBusyAsync("Creating project…", file, async () =>
            {
                var session = await ProjectSession.CreateAsync(folder, Path.GetFileNameWithoutExtension(file), chapters, speakers);
                await SetSessionAsync(session);
            });
            StatusText = $"Imported {result.SegmentCount} segments in {result.Chapters.Count} chapters in {sw.ElapsedMilliseconds} ms";
            AppHost.Log.LogInformation("Headless import of {File}: {Segments} segments, {Ms} ms", file, result.SegmentCount, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Import failed", ex.Message);
        }
    }

    private async Task ApplyImportAsync(ImportViewModel vm)
    {
        var result = vm.Result ?? throw new InvalidOperationException("Nothing parsed.");
        if (vm.Target == ImportTarget.NewProject || Session == null)
        {
            var folder = await ChooseNewProjectFolder(vm.BookTitle);
            if (folder == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var speakers = vm.BuildNewSpeakers(out var labelToId);
            var chapters = TextImporter.ToChapters(result, label => label == null ? "narrator" : labelToId[label]);
            await RunBusyAsync("Creating project…", vm.BookTitle, async () =>
            {
                var session = await ProjectSession.CreateAsync(folder, vm.BookTitle, chapters, speakers);
                await SetSessionAsync(session);
            });
            StatusText = $"Imported {result.SegmentCount} segments in {result.Chapters.Count} chapters ({sw.ElapsedMilliseconds} ms)";
            return;
        }

        var s = Session;
        var map = vm.ApplySpeakerMapping(s);
        var imported = TextImporter.ToChapters(result, label => label == null ? s.Project.Defaults.Speaker : map[label]);
        switch (vm.Target)
        {
            case ImportTarget.AppendChapters:
                ChapterViewModel? first = null;
                foreach (var ch in imported)
                {
                    var added = s.AddChapter(ch.Title, null, ch.Items);
                    first ??= added;
                }
                if (first != null) SelectedChapter = first;
                StatusText = $"Appended {imported.Count} chapter(s)";
                break;
            case ImportTarget.ReplaceChapter when vm.TargetChapter != null:
                var items = imported.SelectMany(c => c.Items).ToList();
                var existingIds = vm.TargetChapter.Model.Items.Select(i => i.Id).ToHashSet();
                // Merge works on copies so the undo snapshot of the old items stays intact.
                var merged = ScriptMerger.Merge(vm.TargetChapter.Model.Items.Select(i => i.Clone()).ToList(), items);
                var kept = merged.Count(m => existingIds.Contains(m.Id));
                s.ReplaceChapterItems(vm.TargetChapter, merged);
                SelectedChapter = vm.TargetChapter;
                StatusText = $"Replaced chapter text; {kept} of {merged.Count} segments kept their takes";
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private async Task ExportScript()
    {
        if (Session == null) return;
        var path = await Dialogs.SaveFile("Export script", AudiobookExporter.SafeFileName(Session.Project.File.Title) + ".txt", "txt",
            new FileFilter("Text", "*.txt"));
        if (path == null) return;
        await Session.FlushAsync();
        var text = ScriptExporter.Export(Session.Project);
        await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false));
        StatusText = $"Script written to {path}. Re-import it with File → Import text → Replace chapter to keep takes.";
    }

    // ================================================================== audiobook export and packaging

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private async Task ExportAudiobook()
    {
        if (Session == null) return;
        await WindowService.ShowExportAsync(new ExportViewModel(Session, null));
    }

    public async Task ExportChapterAsync(ChapterViewModel ch)
    {
        if (Session == null) return;
        await WindowService.ShowExportAsync(new ExportViewModel(Session, ch));
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private async Task PackProject()
    {
        if (Session == null) return;
        var options = await WindowService.ShowPackOptionsAsync();
        if (options == null) return;
        var path = await Dialogs.SaveFile("Pack project", AudiobookExporter.SafeFileName(Session.Project.File.Title) + ProjectPackager.Extension,
            "abproj", new FileFilter("Audiobook project", "*" + ProjectPackager.Extension));
        if (path == null) return;
        await Session.FlushAsync();
        var selected = Session.Project.AllItems().Where(i => i.IsGenerated).ToDictionary(i => i.Id, i => i.SelectedTake);
        var root = Session.Paths.Root;
        var ok = await WindowService.RunWithProgressAsync("Packing project", async (progress, ct) =>
        {
            await ProjectPackager.PackAsync(root, path, options, selected,
                new Progress<PackProgress>(p => progress.Report((p.Total == 0 ? 0 : (double)p.Done / p.Total, p.CurrentFile))), ct);
        });
        if (ok) StatusText = $"Packed to {path}";
    }

    [RelayCommand]
    private async Task UnpackProject()
    {
        var file = await Dialogs.OpenFile("Unpack project", new FileFilter("Audiobook project", "*" + ProjectPackager.Extension));
        if (file != null) await UnpackAsync(file);
    }

    private async Task UnpackAsync(string file)
    {
        var parent = await Dialogs.PickFolder("Choose where to extract the project");
        if (parent == null) return;
        var dest = FreeProjectFolder(parent, Path.GetFileNameWithoutExtension(file));
        string? result = null;
        var ok = await WindowService.RunWithProgressAsync("Unpacking project", async (progress, ct) =>
        {
            result = await ProjectPackager.UnpackAsync(file, dest,
                new Progress<PackProgress>(p => progress.Report((p.Total == 0 ? 0 : (double)p.Done / p.Total, p.CurrentFile))), ct);
        });
        if (ok && result != null) await OpenProjectAsync(result);
    }

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private async Task CleanUpTakes()
    {
        if (Session == null) return;
        var total = Session.Project.AllItems().Where(i => i.IsGenerated).Sum(i => Session.Takes.Get(i.Id).Count(t => t.Take != i.SelectedTake));
        if (!await Dialogs.Confirm("Clean up unused takes",
                $"Delete {total} take(s) that are not selected, plus audio of deleted segments?\n\nThis cannot be undone.", "Delete", "Cancel"))
            return;
        var n = await Session.CleanUpUnusedTakesAsync();
        StatusText = $"Deleted {n} file(s)";
    }

    /// <summary>Converts the project's WAV takes to Opus (6 to 10 times smaller), with a progress window.</summary>
    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private async Task CompressTakes()
    {
        if (Session is not { } s) return;
        if (s.Project.IsReadOnly)
        {
            await Dialogs.ShowInfo("Compress takes", "This is a read-only example project; its takes stay as they are.");
            return;
        }
        var (count, bytes) = await Task.Run(s.UncompressedTakes);
        if (count == 0)
        {
            await Dialogs.ShowInfo("Compress takes", "All takes are already stored as Opus.");
            return;
        }
        if (!await Dialogs.Confirm("Compress takes",
                $"Convert {count} WAV take(s) ({SizeText(bytes)}) to Opus?\n\n" +
                "They become 6 to 10 times smaller. Opus is lossy, but at this bit rate the difference is very hard to hear. " +
                "The WAV files are deleted.", "Compress", "Cancel"))
            return;
        StopPlayback();
        (int Count, long BytesSaved) result = default;
        var ok = await WindowService.RunWithProgressAsync("Compressing takes", async (progress, ct) =>
        {
            result = await s.CompressTakesAsync(new Progress<double>(f => progress.Report((f, ""))), ct);
        });
        if (ok) StatusText = $"Compressed {result.Count} take(s), {SizeText(result.BytesSaved)} saved";
    }

    private static string SizeText(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.0} GB"
        : $"{bytes / (double)(1L << 20):0} MB";

    // ================================================================== chapters

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private void AddChapter()
    {
        if (Session == null) return;
        var index = SelectedChapter != null ? Session.Chapters.IndexOf(SelectedChapter) + 1 : Session.Chapters.Count;
        var ch = Session.AddChapter($"Chapter {Session.Chapters.Count + 1}", index);
        SelectedChapter = ch;
    }

    [RelayCommand]
    private async Task RenameChapter(ChapterViewModel? ch)
    {
        ch ??= SelectedChapter;
        if (Session == null || ch == null) return;
        var title = await Dialogs.Prompt("Rename chapter", "Chapter title:", ch.Title);
        if (!string.IsNullOrWhiteSpace(title)) Session.RenameChapter(ch, title.Trim());
        OnPropertyChanged(nameof(CurrentChapterTitle));
    }

    [RelayCommand]
    private async Task DeleteChapter(ChapterViewModel? ch)
    {
        ch ??= SelectedChapter;
        if (Session == null || ch == null) return;
        if (Session.RefuseIfLocked(ch.Items)) return; // explained in the status bar
        if (!await Dialogs.Confirm("Delete chapter", $"Delete \"{ch.Title}\" and its {ch.Items.Count} item(s)? (Undo restores it; audio files stay on disk.)", "Delete", "Cancel"))
            return;
        var idx = Session.Chapters.IndexOf(ch);
        Session.DeleteChapter(ch);
        SelectedChapter = Session.Chapters.Count == 0 ? null : Session.Chapters[Math.Clamp(idx, 0, Session.Chapters.Count - 1)];
    }

    [RelayCommand]
    private void GenerateChapter(ChapterViewModel? ch)
    {
        ch ??= SelectedChapter;
        if (Session == null || ch == null) return;
        var n = Session.GenerateStale([ch]);
        StatusText = n == 0 ? "Chapter is up to date." : $"Queued {n} item(s) in {ch.Title}";
    }

    /// <summary>A new take for every segment of the chapter, current or not (locked and recorded ones are skipped).</summary>
    [RelayCommand]
    private async Task RegenerateChapter(ChapterViewModel? ch)
    {
        ch ??= SelectedChapter;
        if (Session == null || ch == null) return;
        var count = ch.Items.Count(i => i.IsGenerated && !i.IsLocked && !Session.IsRecorded(i.Model));
        if (count == 0)
        {
            StatusText = "Nothing to generate in this chapter (locked and recorded segments are skipped).";
            return;
        }
        if (!await Dialogs.Confirm("Regenerate chapter",
                $"Generate a new take for all {count} segment(s) in \"{ch.Title}\"? Their current takes are kept.", "Regenerate", "Cancel"))
            return;
        var n = Session.Generate(ch.Items, JobPriority.Normal, skipLocked: true);
        StatusText = $"Queued {n} item(s) in {ch.Title}";
    }

    [RelayCommand]
    private async Task CleanUpChapterTakes(ChapterViewModel? ch)
    {
        ch ??= SelectedChapter;
        if (Session == null || ch == null) return;
        var total = ch.Items.Where(i => i.IsGenerated).Sum(i => Session.Takes.Get(i.Id).Count(t => t.Take != i.Model.SelectedTake));
        if (total == 0)
        {
            StatusText = "This chapter has no unused takes.";
            return;
        }
        if (!await Dialogs.Confirm("Clear unused takes",
                $"Delete the {total} take(s) in \"{ch.Title}\" that are not selected?\n\nThis cannot be undone.", "Delete", "Cancel"))
            return;
        var n = await Session.CleanUpUnusedTakesAsync(ch);
        StatusText = $"Deleted {n} file(s)";
    }

    [RelayCommand]
    private async Task ClearChapterTakes(ChapterViewModel? ch)
    {
        ch ??= SelectedChapter;
        if (Session == null || ch == null) return;
        var items = Session.ClearableItems(ch);
        if (items.Count == 0)
        {
            StatusText = "This chapter has no takes to clear (locked and recorded segments keep theirs).";
            return;
        }
        if (!await Dialogs.Confirm("Clear all takes",
                $"Delete every take of {items.Count} segment(s) in \"{ch.Title}\"? They will need generating again. " +
                "Locked and recorded segments keep theirs.\n\nThis cannot be undone.", "Delete", "Cancel"))
            return;
        StopPlayback();
        var (segments, files) = await Session.ClearTakesAsync(ch);
        StatusText = $"Cleared {segments} segment(s), deleted {files} file(s)";
    }

    /// <summary>The chapter's segments that need attention: stale, failed, or a transcript that doesn't match.</summary>
    public static List<ItemViewModel> Warnings(ChapterViewModel ch) =>
        ch.Items.Where(i => i.Status is ItemStatus.Stale or ItemStatus.Failed || i.TranscriptFlagged).ToList();

    /// <summary>Selects the chapter's next segment that needs attention, after the selection (wrapping round).</summary>
    [RelayCommand]
    private void JumpToWarning(ChapterViewModel? ch)
    {
        ch ??= SelectedChapter;
        if (ch == null || Warnings(ch) is not { Count: > 0 } warnings) return;
        var after = SelectedItems.Count > 0 && ReferenceEquals(SelectedItems[^1].Chapter, ch) ? ch.Items.IndexOf(SelectedItems[^1]) : -1;
        var target = warnings.FirstOrDefault(i => ch.Items.IndexOf(i) > after) ?? warnings[0];
        SelectAndReveal(target);
        StatusText = target.Status switch
        {
            ItemStatus.Stale => "Stale: its text, speaker or settings changed since it was generated.",
            ItemStatus.Failed => $"Failed: {target.Model.LastError}",
            _ => "The transcript doesn't match the text.",
        };
    }

    [RelayCommand]
    private async Task ExportChapter(ChapterViewModel? ch)
    {
        ch ??= SelectedChapter;
        if (ch != null) await ExportChapterAsync(ch);
    }

    /// <summary>Drag-and-drop of items onto a chapter in the chapter panel.</summary>
    public void MoveSelectionToChapter(ChapterViewModel target)
    {
        if (Session == null || SelectedItems.Count == 0) return;
        Session.MoveItems(SelectedItems.ToList(), target, target.Items.Count);
        StatusText = $"Moved {SelectedItems.Count} item(s) to {target.Title}";
    }

    // ================================================================== windows

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private async Task ShowSpeakers(SpeakerViewModel? speaker)
    {
        if (Session != null) await WindowService.ShowSpeakersAsync(Session, speaker);
    }

    [RelayCommand]
    private void ShowJobs() => WindowService.ShowJobs(this);

    /// <summary>Tools menu: the One shot window on the tab named by the parameter (Generate, Transcribe, Align, Compare).</summary>
    [RelayCommand]
    private void ShowOneShot(string? tab) =>
        WindowService.ShowOneShot(Enum.TryParse<OneShotKind>(tab, out var kind) ? kind : OneShotKind.Generate);

    [RelayCommand]
    private async Task ShowWorkers() => await WindowService.ShowWorkersAsync();

    [RelayCommand]
    private async Task ShowSettings()
    {
        var deviceBefore = AppHost.Settings.AudioOutputDevice;
        await WindowService.ShowSettingsAsync();
        if (deviceBefore != AppHost.Settings.AudioOutputDevice) ResetAudioDevice();
        RefreshSlots();
        ApplyTextFont();
    }

    [RelayCommand]
    private async Task About() => await WindowService.ShowAboutAsync();

    [RelayCommand]
    private async Task SystemCheck() => await WindowService.ShowSystemCheckAsync();

    [RelayCommand]
    private void OpenLogFolder() => Dialogs.Reveal(AppHost.DataRoot.Logs);

    [RelayCommand]
    private void OpenDataFolder() => Dialogs.Reveal(AppHost.DataRoot.Root);

    [RelayCommand(CanExecute = nameof(CanUseProject))]
    private void OpenProjectFolder()
    {
        if (Session != null) Dialogs.Reveal(Session.Paths.Root);
    }

    [RelayCommand]
    private void Exit() => Dialogs.Owner?.Close();

    /// <summary>Applies the configured reading font to the segment text (a dynamic resource).</summary>
    public static void ApplyTextFont()
    {
        var res = Avalonia.Application.Current!.Resources;
        var ui = AppHost.Settings.Ui;
        res["SegmentFontFamily"] = new Avalonia.Media.FontFamily(string.IsNullOrWhiteSpace(ui.TextFont) ? "Georgia" : ui.TextFont + ", Georgia, serif");
        res["SegmentFontSize"] = (double)Math.Clamp(ui.TextSizePx, 10, 32);
    }
}
