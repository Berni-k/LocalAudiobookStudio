using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Core.Import;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.App.ViewModels;

public enum ImportTarget
{
    NewProject,
    AppendChapters,
    ReplaceChapter,
}

public enum LabelMappingMode
{
    NewSpeaker,
    ExistingSpeaker,
    MergeWithLabel,
}

/// <summary>The import preview dialog: options, a live parse preview, and speaker label mapping.</summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly string _text;
    private readonly ProjectSession? _session;
    private CancellationTokenSource? _parseCts;

    public ImportViewModel(ProjectSession? session, string file, string text, ChapterViewModel? currentChapter)
    {
        _session = session;
        _text = text;
        FileName = Path.GetFileName(file);
        BookTitle = Path.GetFileNameWithoutExtension(file);
        Target = session == null ? ImportTarget.NewProject : ImportTarget.AppendChapters;
        TargetChapter = currentChapter;
        ReparseSoon();
    }

    public string FileName { get; }
    public bool HasProject => _session != null;
    public IReadOnlyList<ChapterViewModel> Chapters => _session?.Chapters.ToList() ?? [];

    // ------------------------------------------------------------------ options

    [ObservableProperty] private string _bookTitle = "";
    [ObservableProperty] private string _chapterRegex = ImportOptions.DefaultChapterRegex;
    [ObservableProperty] private decimal _maxSegmentChars = 1500;
    [ObservableProperty] private bool _everyLineIsParagraph;
    [ObservableProperty] private bool _speakHeadings = true;
    [ObservableProperty] private string _knownNames = "";
    [ObservableProperty] private ImportTarget _target;
    [ObservableProperty] private ChapterViewModel? _targetChapter;

    public bool IsNewProject { get => Target == ImportTarget.NewProject; set { if (value) Target = ImportTarget.NewProject; } }
    public bool IsAppend { get => Target == ImportTarget.AppendChapters; set { if (value) Target = ImportTarget.AppendChapters; } }
    public bool IsReplace { get => Target == ImportTarget.ReplaceChapter; set { if (value) Target = ImportTarget.ReplaceChapter; } }

    partial void OnTargetChanged(ImportTarget value)
    {
        OnPropertyChanged(nameof(IsNewProject));
        OnPropertyChanged(nameof(IsAppend));
        OnPropertyChanged(nameof(IsReplace));
        BuildMappings();
    }

    partial void OnChapterRegexChanged(string value) => ReparseSoon();
    partial void OnMaxSegmentCharsChanged(decimal value) => ReparseSoon();
    partial void OnEveryLineIsParagraphChanged(bool value) => ReparseSoon();
    partial void OnSpeakHeadingsChanged(bool value) => ReparseSoon();
    partial void OnKnownNamesChanged(string value) => ReparseSoon();

    // ------------------------------------------------------------------ results

    [ObservableProperty] private ImportResult? _result;
    [ObservableProperty] private string _summary = "Parsing…";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isParsing;

    public ObservableCollection<string> ChapterPreview { get; } = new();
    public ObservableCollection<string> SegmentPreview { get; } = new();
    public ObservableCollection<LabelMappingViewModel> Mappings { get; } = new();
    public bool HasMappings => Mappings.Count > 0;

    public bool CanImport => Result != null && Error == null && !IsParsing && Result.SegmentCount > 0 &&
                             (Target != ImportTarget.ReplaceChapter || TargetChapter != null);

    partial void OnResultChanged(ImportResult? value) => OnPropertyChanged(nameof(CanImport));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(CanImport));
    partial void OnIsParsingChanged(bool value) => OnPropertyChanged(nameof(CanImport));
    partial void OnTargetChapterChanged(ChapterViewModel? value) => OnPropertyChanged(nameof(CanImport));

    private ImportOptions BuildOptions()
    {
        var known = KnownNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (_session != null)
        {
            foreach (var sp in _session.Project.Speakers.Values)
            {
                if (!string.IsNullOrWhiteSpace(sp.DisplayName)) known.Add(sp.DisplayName);
                known.AddRange(sp.MarkerAliases);
            }
        }
        var userMax = (int)MaxSegmentChars;
        return new ImportOptions
        {
            ChapterRegex = ChapterRegex,
            MaxSegmentChars = userMax,
            EveryLineIsParagraph = EveryLineIsParagraph,
            SpeakHeadings = SpeakHeadings,
            KnownSpeakerNames = known.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            DefaultChapterTitle = "Chapter 1",
            MaxCharsForSpeaker = label => Math.Min(userMax, MaxCharsFor(label)),
        };
    }

    /// <summary>Per-worker limit from the manifest of the speaker a label maps to.</summary>
    private int MaxCharsFor(string? label) => ProjectImport.MaxCharsFor(AppHost.Host!, _session?.Project, label);

    private void ReparseSoon()
    {
        _parseCts?.Cancel();
        var cts = _parseCts = new CancellationTokenSource();
        IsParsing = true;
        DispatcherTimer.RunOnce(async () =>
        {
            if (cts.IsCancellationRequested) return;
            ImportOptions options;
            try
            {
                options = BuildOptions();
                _ = new System.Text.RegularExpressions.Regex(options.ChapterRegex);
            }
            catch (Exception ex)
            {
                Error = "Invalid chapter pattern: " + ex.Message;
                IsParsing = false;
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ImportResult r;
            try
            {
                r = await Task.Run(() => TextImporter.Parse(_text, options));
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested) return;
                Error = ex.Message;
                IsParsing = false;
                return;
            }
            if (cts.IsCancellationRequested) return;
            Error = null;
            Result = r;
            Summary = $"{r.Chapters.Count} chapter(s), {r.SegmentCount} segment(s), {r.TextLength:N0} characters of text · " +
                      $"{r.SpeakerLabels.Count} speaker label(s) · parsed in {sw.ElapsedMilliseconds} ms";
            ChapterPreview.Clear();
            foreach (var c in r.Chapters.Take(500)) ChapterPreview.Add($"{c.Title}  ({c.Items.Count})");
            SegmentPreview.Clear();
            foreach (var i in r.Chapters.SelectMany(c => c.Items).Take(200))
            {
                var who = i.Kind switch
                {
                    ItemKind.Pause => $"[pause {i.DurationMs} ms]",
                    ItemKind.Comment => "[comment]",
                    ItemKind.Clip => "[clip]",
                    _ => $"[{i.SpeakerLabel ?? "default"}]",
                };
                var body = i.Text ?? i.ClipFile ?? "";
                if (body.Length > 160) body = body[..157] + "…";
                SegmentPreview.Add($"{who} {body}");
            }
            BuildMappings();
            IsParsing = false;
        }, TimeSpan.FromMilliseconds(250));
    }

    private void BuildMappings()
    {
        var labels = Result?.SpeakerLabels ?? [];
        var old = Mappings.ToDictionary(m => m.Label, StringComparer.OrdinalIgnoreCase);
        Mappings.Clear();
        var existing = _session?.Speakers.ToList() ?? [];
        foreach (var label in labels)
        {
            var m = new LabelMappingViewModel(label, existing, labels.Where(l => l != label).ToList(), Target != ImportTarget.NewProject);
            if (old.TryGetValue(label, out var prev)) m.CopyFrom(prev);
            else if (Target != ImportTarget.NewProject && _session?.Project.FindSpeakerByLabel(label) is { } sp)
            {
                m.Mode = LabelMappingMode.ExistingSpeaker;
                m.ExistingSpeaker = existing.FirstOrDefault(e => e.Id == sp.Id);
            }
            Mappings.Add(m);
        }
        OnPropertyChanged(nameof(HasMappings));
    }

    // ------------------------------------------------------------------ applying the mapping

    /// <summary>Resolves "merge with label" chains to the final label.</summary>
    private string Resolve(string label)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = label;
        while (seen.Add(current))
        {
            var m = Mappings.FirstOrDefault(x => x.Label.Equals(current, StringComparison.OrdinalIgnoreCase));
            if (m is not { Mode: LabelMappingMode.MergeWithLabel, MergeTarget: { } t }) break;
            current = t;
        }
        return current;
    }

    /// <summary>For a new project: speakers to create, and the label → speaker id map.</summary>
    public List<Speaker> BuildNewSpeakers(out Dictionary<string, string> labelToId)
    {
        labelToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var speakers = new List<Speaker>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "narrator" };
        var worker = AppHost.Host!.DefaultTtsWorker;
        var manifest = AppHost.Catalog.Get(worker);
        var voiceIndex = 1;
        foreach (var m in Mappings.Where(m => m.Mode != LabelMappingMode.MergeWithLabel))
        {
            var id = Speaker.MakeId(string.IsNullOrWhiteSpace(m.NewName) ? m.Label : m.NewName);
            var baseId = id;
            for (int n = 2; ids.Contains(id); n++) id = $"{baseId}-{n}";
            ids.Add(id);
            speakers.Add(new Speaker
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(m.NewName) ? m.Label : m.NewName.Trim(),
                Color = ProjectStore.SpeakerPalette[(speakers.Count + 1) % ProjectStore.SpeakerPalette.Length],
                Worker = worker,
                Settings = manifest?.DefaultSettings(voiceIndex++) ?? new(),
                MarkerAliases = [m.Label],
            });
            labelToId[m.Label] = id;
        }
        foreach (var m in Mappings.Where(m => m.Mode == LabelMappingMode.MergeWithLabel))
        {
            var final = Resolve(m.Label);
            if (labelToId.TryGetValue(final, out var id))
            {
                labelToId[m.Label] = id;
                speakers.First(s => s.Id == id).MarkerAliases.Add(m.Label);
            }
            else
            {
                labelToId[m.Label] = "narrator";
            }
        }
        return speakers;
    }

    /// <summary>For an existing project: creates new speakers, records aliases, returns label → id.</summary>
    public Dictionary<string, string> ApplySpeakerMapping(ProjectSession session)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaultWorker = session.Project.FindSpeaker(session.Project.Defaults.Speaker)?.Worker ?? "kokoro";
        foreach (var m in Mappings.Where(m => m.Mode != LabelMappingMode.MergeWithLabel))
        {
            Speaker sp;
            if (m.Mode == LabelMappingMode.ExistingSpeaker && m.ExistingSpeaker != null)
            {
                sp = m.ExistingSpeaker.Model;
            }
            else
            {
                sp = session.AddSpeaker(string.IsNullOrWhiteSpace(m.NewName) ? m.Label : m.NewName.Trim(), defaultWorker, session.Speakers.Count);
            }
            if (!sp.MarkerAliases.Contains(m.Label, StringComparer.OrdinalIgnoreCase)) sp.MarkerAliases.Add(m.Label);
            session.MarkSpeakerDirty(sp);
            map[m.Label] = sp.Id;
        }
        foreach (var m in Mappings.Where(m => m.Mode == LabelMappingMode.MergeWithLabel))
        {
            var final = Resolve(m.Label);
            var id = map.GetValueOrDefault(final) ?? session.Project.Defaults.Speaker;
            map[m.Label] = id;
            if (session.Project.FindSpeaker(id) is { } sp && !sp.MarkerAliases.Contains(m.Label, StringComparer.OrdinalIgnoreCase))
            {
                sp.MarkerAliases.Add(m.Label);
                session.MarkSpeakerDirty(sp);
            }
        }
        return map;
    }
}

/// <summary>How one speaker label from the text maps to a project speaker.</summary>
public sealed partial class LabelMappingViewModel : ObservableObject
{
    public LabelMappingViewModel(string label, IReadOnlyList<SpeakerViewModel> existing, IReadOnlyList<string> otherLabels, bool allowExisting)
    {
        Label = label;
        ExistingSpeakers = existing;
        OtherLabels = otherLabels;
        AllowExisting = allowExisting && existing.Count > 0;
        NewName = label;
        Modes = AllowExisting
            ? [LabelMappingMode.NewSpeaker, LabelMappingMode.ExistingSpeaker, LabelMappingMode.MergeWithLabel]
            : [LabelMappingMode.NewSpeaker, LabelMappingMode.MergeWithLabel];
        if (otherLabels.Count == 0) Modes = Modes.Where(m => m != LabelMappingMode.MergeWithLabel).ToList();
    }

    public string Label { get; }
    public IReadOnlyList<SpeakerViewModel> ExistingSpeakers { get; }
    public IReadOnlyList<string> OtherLabels { get; }
    public bool AllowExisting { get; }
    public IReadOnlyList<LabelMappingMode> Modes { get; }

    [ObservableProperty] private LabelMappingMode _mode = LabelMappingMode.NewSpeaker;
    [ObservableProperty] private SpeakerViewModel? _existingSpeaker;
    [ObservableProperty] private string? _mergeTarget;
    [ObservableProperty] private string _newName = "";

    public bool IsNew => Mode == LabelMappingMode.NewSpeaker;
    public bool IsExisting => Mode == LabelMappingMode.ExistingSpeaker;
    public bool IsMerge => Mode == LabelMappingMode.MergeWithLabel;

    partial void OnModeChanged(LabelMappingMode value)
    {
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(IsExisting));
        OnPropertyChanged(nameof(IsMerge));
        if (value == LabelMappingMode.ExistingSpeaker && ExistingSpeaker == null) ExistingSpeaker = ExistingSpeakers.FirstOrDefault();
        if (value == LabelMappingMode.MergeWithLabel && MergeTarget == null) MergeTarget = OtherLabels.FirstOrDefault();
    }

    public void CopyFrom(LabelMappingViewModel o)
    {
        if (Modes.Contains(o.Mode)) Mode = o.Mode;
        ExistingSpeaker = ExistingSpeakers.FirstOrDefault(s => s.Id == o.ExistingSpeaker?.Id);
        MergeTarget = OtherLabels.Contains(o.MergeTarget ?? "") ? o.MergeTarget : null;
        NewName = o.NewName;
    }
}
