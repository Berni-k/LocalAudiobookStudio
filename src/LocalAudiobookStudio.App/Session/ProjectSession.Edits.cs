using System.Text.Json.Nodes;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.ViewModels;
using LocalAudiobookStudio.Core.Model;
using LocalAudiobookStudio.Core.Undo;

namespace LocalAudiobookStudio.App.Session;

/// <summary>Every project edit, each recorded on the undo stack.</summary>
public sealed partial class ProjectSession
{
    // ================================================================== field edits (snapshot based)

    // ================================================================== locked sections
    //
    // A locked section can't be changed (text, speaker, audio settings, takes, position, deletion,
    // regeneration) until it is unlocked. Only its lock and its note can be edited. Every edit
    // below goes through these checks, so menus, keys, the inspector and drag-and-drop all agree.

    private const string LockedMessage = "Locked sections can't be changed. Unlock them first (L).";

    /// <summary>The unlocked items; reports in the status bar when locked ones were left out.</summary>
    private List<ItemViewModel> Unlocked(IReadOnlyList<ItemViewModel> items)
    {
        var free = items.Where(i => !i.IsLocked).ToList();
        if (free.Count < items.Count) Notify(LockedMessage);
        return free;
    }

    /// <summary>True (and reports it) when any item is locked: for operations that must apply to all or none.</summary>
    public bool RefuseIfLocked(IEnumerable<ItemViewModel> items)
    {
        if (!items.Any(i => i.IsLocked)) return false;
        Notify(LockedMessage);
        return true;
    }

    /// <summary>
    /// Applies <paramref name="apply"/> to each item's model and records one undo step. Undo and
    /// redo restore full snapshots of the affected items, so every field edit works the same way.
    /// Locked items are left out unless <paramref name="evenIfLocked"/> (for the lock itself).
    /// </summary>
    public void Edit(IReadOnlyList<ItemViewModel> items, string description, Action<TimelineItem> apply, string? mergeKey = null,
        bool evenIfLocked = false)
    {
        if (!evenIfLocked) items = Unlocked(items);
        if (items.Count == 0) return;
        var before = items.Select(i => i.Model.Clone()).ToList();
        foreach (var i in items) apply(i.Model);
        var after = items.Select(i => i.Model.Clone()).ToList();

        void Restore(List<TimelineItem> snapshot)
        {
            for (int k = 0; k < items.Count; k++) items[k].Model.CopyFrom(snapshot[k]);
            RefreshItems(items);
        }

        Undo.Push(new DelegateCommand(description, () => Restore(after), () => Restore(before)), mergeKey);
        RefreshItems(items);
    }

    private static string Key(string what, IReadOnlyList<ItemViewModel> items) =>
        what + ":" + string.Join(",", items.Select(i => i.Id.ToString("N")[..8]));

    public void SetText(ItemViewModel item, string text) =>
        Edit([item], "Edit text", m => m.Text = text);

    public void SetSpeaker(IReadOnlyList<ItemViewModel> items, string speakerId)
    {
        var tts = items.Where(i => i.IsTts).ToList();
        Edit(tts, "Assign speaker", m => m.Speaker = speakerId);
    }

    public void SetGain(IReadOnlyList<ItemViewModel> items, double db) =>
        Edit(items, "Set gain", m => m.GainDb = Math.Round(Math.Clamp(db, -40, 20), 1), Key("gain", items));

    public void ResetGain(IReadOnlyList<ItemViewModel> items) =>
        Edit(items, "Reset gain", m => m.GainDb = 0);

    // ------------------------------------------------------------------ background items

    public void SetBackground(IReadOnlyList<ItemViewModel> items, bool on) =>
        Edit(items.Where(i => !i.IsPause).ToList(), on ? "Make background" : "Make normal section", m => m.Background = on ? true : null);

    public void SetBackgroundLength(IReadOnlyList<ItemViewModel> items, BackgroundLength mode, double? limit) =>
        Edit(items.Where(i => !i.IsPause).ToList(), "Set background length", m =>
        {
            m.BackgroundLength = mode == BackgroundLength.Full ? null : mode;
            m.BackgroundLimit = mode is BackgroundLength.Seconds or BackgroundLength.Sections ? limit : null;
        }, Key("bglen", items));

    public void SetBackgroundLoop(IReadOnlyList<ItemViewModel> items, bool loop) =>
        Edit(items.Where(i => !i.IsPause).ToList(), loop ? "Loop background" : "Don't loop background", m => m.BackgroundLoop = loop ? true : null);

    public void SetTrims(ItemViewModel item, int? startMs, int? endMs) =>
        Edit([item], "Trim", m => { m.TrimStartMs = startMs; m.TrimEndMs = endMs; }, Key("trim", [item]));

    public void SetFades(IReadOnlyList<ItemViewModel> items, int? fadeIn, int? fadeOut) =>
        Edit(items, "Set fades", m => { m.FadeInMs = fadeIn; m.FadeOutMs = fadeOut; }, Key("fade", items));

    public void SetNormalize(IReadOnlyList<ItemViewModel> items, bool on) =>
        Edit(items, on ? "Enable normalization" : "Disable normalization", m => m.Normalize = on ? null : false);

    public void SetLocked(IReadOnlyList<ItemViewModel> items, bool locked) =>
        Edit(items, locked ? "Lock" : "Unlock", m => m.Locked = locked, evenIfLocked: true);

    public void ToggleLock(IReadOnlyList<ItemViewModel> items)
    {
        if (items.Count == 0) return;
        SetLocked(items, !items.All(i => i.IsLocked));
    }

    public void SetPauseDuration(IReadOnlyList<ItemViewModel> items, int ms) =>
        Edit(items.Where(i => i.IsPause).ToList(), "Set pause length", m => m.DurationMs = Math.Clamp(ms, -600_000, 600_000), Key("pause", items));

    /// <summary>
    /// Requested length of segments whose worker takes one, kept within each worker's range.
    /// Null clears it: the worker then decides, or (when it requires a length) the guess is used.
    /// </summary>
    public void SetLength(IReadOnlyList<ItemViewModel> items, double? seconds) =>
        Edit(items.Where(i => WorkerFor(i.Model)?.SupportsLength == true).ToList(), seconds == null ? "Clear length" : "Set length",
            m => m.DurationSec = seconds is double s && WorkerFor(m) is not null ? Math.Clamp(Math.Round(s, 1), WorkerFor(m)!.MinLengthSec, MaxLengthSec(m)) : null,
            Key("length", items));

    /// <summary>The file name in a per-segment export (blank: numbered).</summary>
    public void SetExportId(ItemViewModel item, string? id) =>
        Edit([item], "Set export ID", m => m.ExportId = string.IsNullOrWhiteSpace(id) ? null : id.Trim(), Key("exportid", [item]));

    public void SetClipLabel(ItemViewModel item, string? label) =>
        Edit([item], "Rename clip", m => m.Label = label, Key("label", [item]));

    /// <summary>Sets (or clears, with null) one per-segment setting override such as the seed.</summary>
    public void SetOverride(IReadOnlyList<ItemViewModel> items, string key, JsonNode? value) =>
        Edit(items, $"Set {key}", m =>
        {
            var o = m.SettingsOverride ?? new JsonObject();
            if (value == null) o.Remove(key); else o[key] = value.DeepClone();
            m.SettingsOverride = o.Count == 0 ? null : o;
        }, Key("override-" + key, items));

    // ================================================================== takes

    /// <summary>Selects a take (undoable). Gain and gap are never changed by take selection.</summary>
    public void SelectTake(ItemViewModel item, int take)
    {
        var info = Takes.Find(item.Id, take);
        if (info == null || item.Model.SelectedTake == take) return;
        Edit([item], $"Select take {take}", m =>
        {
            m.SelectedTake = take;
            m.InputHash = info.InputHash;
            m.LastError = null;
        });
    }

    public void StepTake(ItemViewModel item, int delta)
    {
        var takes = Takes.Get(item.Id);
        if (takes.Count == 0) return;
        var idx = takes.ToList().FindIndex(t => t.Take == item.Model.SelectedTake);
        var next = idx < 0 ? (delta > 0 ? 0 : takes.Count - 1) : Math.Clamp(idx + delta, 0, takes.Count - 1);
        SelectTake(item, takes[next].Take);
    }

    /// <summary>Rates a take (1–5 stars, null to clear). Stored in the take's metadata; not undoable.</summary>
    public void RateTake(ItemViewModel item, int take, int? rating)
    {
        var info = Takes.Find(item.Id, take);
        if (info == null) return;
        info.Rating = rating;
        Core.IO.TakeStore.SaveTakeInfo(Paths, item.Id, info);
        item.RefreshAudio();
    }

    /// <summary>
    /// Deletes a take's files from disk (not undoable). When it was the selected take, the next
    /// remaining take (or the previous one, if it was the last) becomes selected.
    /// </summary>
    public void DeleteTake(ItemViewModel item, int take)
    {
        if (RefuseIfLocked([item])) return;
        var before = Takes.Get(item.Id).ToList();
        var idx = before.FindIndex(t => t.Take == take);
        if (idx < 0) return;

        var peaks = Takes.TakePeaks(item.Id, take);
        PeaksCache.Invalidate(peaks);
        File.Delete(Takes.TakeAudio(item.Id, take));
        try { File.Delete(Paths.TakeJson(item.Id, take)); } catch (IOException) { /* the audio is gone, so the take is hidden anyway */ }
        try { File.Delete(peaks); } catch (IOException) { /* rebuildable cache */ }
        Takes.Reload(item.Id);

        if (item.Model.SelectedTake == take)
        {
            var remaining = Takes.Get(item.Id);
            var next = remaining.Count == 0 ? null : remaining[Math.Min(idx, remaining.Count - 1)];
            item.Model.SelectedTake = next?.Take;
            item.Model.InputHash = next?.InputHash;
            item.Model.LastError = null;
        }
        RefreshItems([item]);
    }

    // ================================================================== structure: items

    public void InsertItems(ChapterViewModel chapter, int index, IReadOnlyList<TimelineItem> models, string description = "Insert")
    {
        if (models.Count == 0) return;
        var vms = models.Select(m => new ItemViewModel(this, chapter, m)).ToList();

        void Do()
        {
            for (int k = 0; k < vms.Count; k++)
            {
                chapter.InsertItem(index + k, vms[k]);
                _itemVms[vms[k].Id] = vms[k];
            }
            AfterStructureChange([chapter], vms);
        }

        void UndoInsert()
        {
            foreach (var vm in vms)
            {
                vm.Chapter.RemoveItem(vm);
                _itemVms.Remove(vm.Id);
            }
            AfterStructureChange([chapter], []);
        }

        Undo.Execute(new DelegateCommand(description, Do, UndoInsert));
        foreach (var vm in vms) vm.UpdateStatus(null);
    }

    public void DeleteItems(IReadOnlyList<ItemViewModel> items)
    {
        items = Unlocked(items);
        if (items.Count == 0) return;
        AppHost.Scheduler.CancelForItems(items.Select(i => i.Id));
        // Remember positions in document order so undo can restore them exactly.
        var entries = items
            .Select(i => (vm: i, chapter: i.Chapter, index: i.Chapter.Items.IndexOf(i)))
            .Where(e => e.index >= 0)
            .OrderBy(e => ChapterIndex(e.chapter)).ThenBy(e => e.index)
            .ToList();

        void Do()
        {
            foreach (var e in Enumerable.Reverse(entries))
            {
                e.chapter.RemoveItem(e.vm);
                _itemVms.Remove(e.vm.Id);
            }
            AfterStructureChange(entries.Select(e => e.chapter).Distinct(), []);
        }

        void UndoDelete()
        {
            foreach (var e in entries)
            {
                e.chapter.InsertItem(e.index, e.vm);
                _itemVms[e.vm.Id] = e.vm;
            }
            AfterStructureChange(entries.Select(e => e.chapter).Distinct(), entries.Select(e => e.vm));
        }

        Undo.Execute(new DelegateCommand(items.Count == 1 ? "Delete item" : $"Delete {items.Count} items", Do, UndoDelete));
    }

    /// <summary>Moves items (in document order) to <paramref name="target"/> at <paramref name="index"/>.</summary>
    public void MoveItems(IReadOnlyList<ItemViewModel> items, ChapterViewModel target, int index)
    {
        if (items.Count == 0 || RefuseIfLocked(items)) return;
        var entries = items
            .Select(i => (vm: i, chapter: i.Chapter, index: i.Chapter.Items.IndexOf(i)))
            .Where(e => e.index >= 0)
            .OrderBy(e => ChapterIndex(e.chapter)).ThenBy(e => e.index)
            .ToList();
        // Items removed above the insertion point shift it up.
        var adjusted = index - entries.Count(e => e.chapter == target && e.index < index);

        void Do()
        {
            foreach (var e in Enumerable.Reverse(entries)) e.chapter.RemoveItem(e.vm);
            for (int k = 0; k < entries.Count; k++) target.InsertItem(Math.Clamp(adjusted + k, 0, target.Items.Count), entries[k].vm);
            AfterStructureChange(entries.Select(e => e.chapter).Append(target).Distinct(), entries.Select(e => e.vm));
        }

        void UndoMove()
        {
            foreach (var e in entries) target.RemoveItem(e.vm);
            foreach (var e in entries) e.chapter.InsertItem(e.index, e.vm);
            AfterStructureChange(entries.Select(e => e.chapter).Append(target).Distinct(), entries.Select(e => e.vm));
        }

        Undo.Execute(new DelegateCommand("Move items", Do, UndoMove));
    }

    /// <summary>Merges adjacent speech segments of one chapter into the first one.</summary>
    public bool Merge(IReadOnlyList<ItemViewModel> items)
    {
        var tts = items.Where(i => i.IsTts).OrderBy(i => i.Chapter.Items.IndexOf(i)).ToList();
        if (tts.Count < 2 || tts.Select(i => i.Chapter).Distinct().Count() != 1) return false;
        if (RefuseIfLocked(tts)) return true; // handled: the status bar explains
        var chapter = tts[0].Chapter;
        var first = tts[0];
        var rest = tts.Skip(1).ToList();
        var restIdx = rest.Select(r => chapter.Items.IndexOf(r)).ToList();
        var oldText = first.Model.Text;
        var newText = string.Join(" ", tts.Select(t => t.Model.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)));

        void Do()
        {
            first.Model.Text = newText;
            foreach (var r in rest) { chapter.RemoveItem(r); _itemVms.Remove(r.Id); }
            AfterStructureChange([chapter], [first]);
        }

        void UndoMerge()
        {
            first.Model.Text = oldText;
            for (int k = 0; k < rest.Count; k++) { chapter.InsertItem(restIdx[k], rest[k]); _itemVms[rest[k].Id] = rest[k]; }
            AfterStructureChange([chapter], rest.Append(first));
        }

        Undo.Execute(new DelegateCommand("Merge segments", Do, UndoMerge));
        return true;
    }

    /// <summary>Splits a speech segment at a character position; the second part becomes a new segment.</summary>
    public ItemViewModel? Split(ItemViewModel item, int position)
    {
        var text = item.Model.Text ?? "";
        if (RefuseIfLocked([item])) return null;
        if (!item.IsTts || position <= 0 || position >= text.Length) return null;
        var left = text[..position].Trim();
        var right = text[position..].Trim();
        if (left.Length == 0 || right.Length == 0) return null;

        var chapter = item.Chapter;
        var newModel = item.Model.CloneAsNew();
        newModel.Text = right;
        var newVm = new ItemViewModel(this, chapter, newModel);
        var oldText = text;

        void Do()
        {
            item.Model.Text = left;
            chapter.InsertItem(chapter.Items.IndexOf(item) + 1, newVm);
            _itemVms[newVm.Id] = newVm;
            AfterStructureChange([chapter], [item, newVm]);
        }

        void UndoSplit()
        {
            item.Model.Text = oldText;
            chapter.RemoveItem(newVm);
            _itemVms.Remove(newVm.Id);
            AfterStructureChange([chapter], [item]);
        }

        Undo.Execute(new DelegateCommand("Split segment", Do, UndoSplit));
        return newVm;
    }

    private void AfterStructureChange(IEnumerable<ChapterViewModel> chapters, IEnumerable<ItemViewModel> changed)
    {
        foreach (var c in chapters)
        {
            MarkChapterDirty(c);
            c.RecountStatus();
        }
        RefreshItems(changed, layout: false);
        InvalidateLayout();
        StructureChanged?.Invoke();
    }

    // ================================================================== structure: chapters

    public ChapterViewModel AddChapter(string title, int? index = null, IEnumerable<TimelineItem>? items = null)
    {
        var model = new Chapter { Title = title };
        if (items != null) model.Items.AddRange(items);
        var vm = new ChapterViewModel(this, model);
        var at = Math.Clamp(index ?? Chapters.Count, 0, Chapters.Count);

        void Do()
        {
            Project.Chapters.Insert(at, model);
            Chapters.Insert(at, vm);
            foreach (var i in vm.Items) _itemVms[i.Id] = i;
            AfterChapterChange();
            MarkChapterDirty(vm);
            foreach (var i in vm.Items) i.UpdateStatus(null);
            vm.RecountStatus();
        }

        void UndoAdd()
        {
            Project.Chapters.Remove(model);
            Chapters.Remove(vm);
            foreach (var i in vm.Items) _itemVms.Remove(i.Id);
            Core.IO.ProjectStore.DeleteChapterFile(Project, model.Id);
            AfterChapterChange();
        }

        Undo.Execute(new DelegateCommand("Add chapter", Do, UndoAdd));
        return vm;
    }

    public void RenameChapter(ChapterViewModel ch, string title)
    {
        var old = ch.Model.Title;
        if (old == title) return;
        Undo.Execute(new DelegateCommand("Rename chapter",
            () => { ch.Model.Title = title; ch.RefreshTitle(); MarkChapterDirty(ch); },
            () => { ch.Model.Title = old; ch.RefreshTitle(); MarkChapterDirty(ch); }));
    }

    public void DeleteChapter(ChapterViewModel ch)
    {
        var index = Chapters.IndexOf(ch);
        if (index < 0 || RefuseIfLocked(ch.Items)) return;
        AppHost.Scheduler.CancelForItems(ch.Items.Select(i => i.Id));

        void Do()
        {
            Project.Chapters.Remove(ch.Model);
            Chapters.Remove(ch);
            foreach (var i in ch.Items) _itemVms.Remove(i.Id);
            // Remove the file too, otherwise the loader would re-append it as an unlisted chapter.
            AfterChapterChange();
            _ = Saves.FlushAsync().ContinueWith(_ =>
            {
                if (!Project.Chapters.Contains(ch.Model)) Core.IO.ProjectStore.DeleteChapterFile(Project, ch.Id);
            });
        }

        void UndoDelete()
        {
            Project.Chapters.Insert(index, ch.Model);
            Chapters.Insert(index, ch);
            foreach (var i in ch.Items) _itemVms[i.Id] = i;
            MarkChapterDirty(ch);
            AfterChapterChange();
        }

        Undo.Execute(new DelegateCommand("Delete chapter", Do, UndoDelete));
    }

    public void MoveChapter(ChapterViewModel ch, int newIndex)
    {
        var old = Chapters.IndexOf(ch);
        newIndex = Math.Clamp(newIndex, 0, Chapters.Count - 1);
        if (old < 0 || old == newIndex) return;

        void MoveTo(int from, int to)
        {
            Chapters.Move(from, to);
            Project.Chapters.RemoveAt(from);
            Project.Chapters.Insert(to, ch.Model);
            AfterChapterChange();
        }

        Undo.Execute(new DelegateCommand("Move chapter", () => MoveTo(old, newIndex), () => MoveTo(newIndex, old)));
    }

    private void AfterChapterChange()
    {
        for (int i = 0; i < Chapters.Count; i++) Chapters[i].Number = i + 1;
        MarkProjectDirty();
        InvalidateLayout();
        StructureChanged?.Invoke();
    }

    /// <summary>Replaces a chapter's items (re-import); items matched by text keep their ids and takes.</summary>
    public void ReplaceChapterItems(ChapterViewModel ch, List<TimelineItem> newItems)
    {
        var oldVms = ch.Items.ToList();
        var byId = oldVms.ToDictionary(v => v.Id);
        var snapshot = oldVms.ToDictionary(v => v.Id, v => v.Model.Clone());
        // Items whose id survived the merge keep their view model (and takes); the rest are new.
        var newVms = newItems.Select(m => byId.TryGetValue(m.Id, out var existing)
            ? existing
            : new ItemViewModel(this, ch, m)).ToList();
        var newSnapshot = newItems.Select(m => m.Clone()).ToList();

        void SetItems(List<ItemViewModel> vms, Func<ItemViewModel, TimelineItem?> state)
        {
            foreach (var v in ch.Items.ToList()) { ch.RemoveItem(v); _itemVms.Remove(v.Id); }
            foreach (var v in vms)
            {
                if (state(v) is { } s) v.Model.CopyFrom(s);
                ch.InsertItem(ch.Items.Count, v);
                _itemVms[v.Id] = v;
            }
            AfterStructureChange([ch], vms);
        }

        var newState = newVms.Select((v, k) => (v, k)).ToDictionary(x => x.v, x => newSnapshot[x.k]);
        Undo.Execute(new DelegateCommand("Replace chapter text",
            () => SetItems(newVms, v => newState[v]),
            () => SetItems(oldVms, v => snapshot[v.Id])));
    }
}
