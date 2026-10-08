using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.App.Session;
using LocalAudiobookStudio.Engine.Export;
using Microsoft.Extensions.Logging;

namespace LocalAudiobookStudio.App.ViewModels;

public enum ReadinessChoice
{
    ExportAnyway,
    OnlyCompleteChapters,
}

/// <summary>Export dialog: readiness check, format options, and a cancellable background export.</summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly ProjectSession _session;
    private readonly ChapterViewModel? _onlyChapter;
    private CancellationTokenSource? _cts;

    public ExportViewModel(ProjectSession session, ChapterViewModel? onlyChapter)
    {
        _session = session;
        _onlyChapter = onlyChapter;
        Title = session.Project.File.Title;
        Author = session.Project.File.Author;
        CoverImage = session.Project.File.CoverImage is { } c ? session.Paths.Resolve(c) : null;
        OutputFolder = session.Paths.ExportDir;
        BuildReadiness();
    }

    public string Heading => _onlyChapter == null ? "Export audiobook" : $"Export chapter: {_onlyChapter.Title}";

    public IReadOnlyList<ExportFormat> Formats { get; } =
        [ExportFormat.M4b, ExportFormat.Mp3Single, ExportFormat.Mp3PerChapter, ExportFormat.Wav, ExportFormat.WavPerSegment, ExportFormat.Mp3PerSegment];

    [ObservableProperty] private ExportFormat _format = ExportFormat.M4b;
    [ObservableProperty] private decimal _bitrate = 96;
    [ObservableProperty] private bool _mono;
    [ObservableProperty] private bool _finalLimiter;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string? _coverImage;
    [ObservableProperty] private string _outputFolder = "";
    [ObservableProperty] private ReadinessChoice _readiness = ReadinessChoice.ExportAnyway;

    public bool IsLossy => Format is not (ExportFormat.Wav or ExportFormat.WavPerSegment);
    public string FormatHelp => Format switch
    {
        ExportFormat.M4b => "One .m4b file (AAC) with chapter markers — best for audiobook players.",
        ExportFormat.Mp3Single => "One MP3 file with ID3 tags and chapter frames.",
        ExportFormat.Mp3PerChapter => "One MP3 file per chapter, numbered, with ID3 tags.",
        ExportFormat.WavPerSegment => SegmentHelp("24-bit WAV"),
        ExportFormat.Mp3PerSegment => SegmentHelp("MP3"),
        _ => "One 24-bit WAV per chapter, lossless, for external editing.",
    };

    private static string SegmentHelp(string kind) =>
        $"One {kind} per segment, without the gaps between them. A segment is named by its [id: …] (or its Export ID in " +
        "the inspector), otherwise by its number in the chapter. Segments without audio yet are left out.";

    partial void OnFormatChanged(ExportFormat value)
    {
        OnPropertyChanged(nameof(IsLossy));
        OnPropertyChanged(nameof(FormatHelp));
        if (value.IsMp3() && Bitrate < 128) Bitrate = 128;
        if (value == ExportFormat.M4b && Bitrate > 128) Bitrate = 96;
    }

    public bool ExportAnyway { get => Readiness == ReadinessChoice.ExportAnyway; set { if (value) Readiness = ReadinessChoice.ExportAnyway; } }
    public bool OnlyComplete { get => Readiness == ReadinessChoice.OnlyCompleteChapters; set { if (value) Readiness = ReadinessChoice.OnlyCompleteChapters; } }
    partial void OnReadinessChanged(ReadinessChoice value) { OnPropertyChanged(nameof(ExportAnyway)); OnPropertyChanged(nameof(OnlyComplete)); }

    // ------------------------------------------------------------------ readiness

    public ObservableCollection<string> Problems { get; } = new();
    public bool HasProblems => Problems.Count > 0;
    [ObservableProperty] private string _readinessSummary = "";

    private IEnumerable<ChapterViewModel> ChaptersInScope => _onlyChapter != null ? [_onlyChapter] : _session.Chapters;

    private void BuildReadiness()
    {
        Problems.Clear();
        int missing = 0, stale = 0, failed = 0;
        foreach (var ch in ChaptersInScope)
        {
            var m = ch.Items.Count(i => i.HasAudio && i.Status is ItemStatus.NotGenerated or ItemStatus.Queued or ItemStatus.Generating);
            var s = ch.Items.Count(i => i.Status == ItemStatus.Stale);
            var f = ch.Items.Count(i => i.Status == ItemStatus.Failed);
            missing += m; stale += s; failed += f;
            if (m + s + f > 0) Problems.Add($"{ch.Number}. {ch.Title}: {m} not generated, {s} stale, {f} failed");
        }
        ReadinessSummary = Problems.Count == 0
            ? "Everything is generated and up to date."
            : $"{missing} not generated (exported as silence of the estimated length), {stale} stale (old take is used), {failed} failed.";
        OnPropertyChanged(nameof(HasProblems));
    }

    // ------------------------------------------------------------------ running

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _stage = "";
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _errorText;
    public ObservableCollection<string> Log { get; } = new();
    private IReadOnlyList<string> _outputs = [];
    public bool HasOutput => _outputs.Count > 0;

    [RelayCommand]
    private async Task BrowseCover()
    {
        var f = await Dialogs.OpenFile("Cover image", new FileFilter("Images", "*.jpg", "*.jpeg", "*.png"));
        if (f != null) CoverImage = f;
    }

    [RelayCommand]
    private async Task BrowseOutput()
    {
        var f = await Dialogs.PickFolder("Export folder");
        if (f != null) OutputFolder = f;
    }

    [RelayCommand]
    private void OpenOutput()
    {
        if (_outputs.Count > 0) Dialogs.Reveal(_outputs[0]);
        else Dialogs.Reveal(OutputFolder);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task Export()
    {
        if (IsRunning) return;
        ErrorText = null;
        ResultText = null;
        _outputs = [];
        OnPropertyChanged(nameof(HasOutput));
        Log.Clear();

        var ffmpeg = Ffmpeg.Locate(AppHost.Settings.FfmpegPath, AppHost.DataRoot.Root);
        if (ffmpeg == null)
        {
            ErrorText = new FfmpegNotFoundException().Message;
            return;
        }

        await _session.FlushAsync();
        var layout = _session.Layout;
        var indices = ChaptersInScope.Select(ch => _session.Chapters.IndexOf(ch))
            .Where(idx => Readiness != ReadinessChoice.OnlyCompleteChapters || ExportPlanner.IsComplete(_session.Engine, layout, idx));
        var indexList = indices.ToList();
        var chapters = ExportPlanner.Chapters(_session.Project.Chapters, layout, indexList);
        if (chapters.Count == 0)
        {
            ErrorText = "No chapters to export (no complete chapters).";
            return;
        }

        var options = new ExportOptions
        {
            Format = Format,
            BitrateKbps = (int)Bitrate,
            Mono = Mono,
            Title = Title,
            Author = Author,
            CoverImagePath = CoverImage,
            FinalLimiter = FinalLimiter,
            OutputPath = ExportPlanner.OutputPath(Format, OutputFolder, Title, _onlyChapter?.Title),
        };

        _cts = new CancellationTokenSource();
        IsRunning = true;
        try
        {
            var exporter = new AudiobookExporter(ffmpeg, _session.AudioCache, _session.Paths.TempDir)
            {
                Log = line => Avalonia.Threading.Dispatcher.UIThread.Post(() => { Log.Add(line); if (Log.Count > 200) Log.RemoveAt(0); }),
            };
            var progress = new Progress<ExportProgress>(p => { Progress = p.Fraction * 100; Stage = p.Stage; });
            var skipped = 0;
            if (Format.IsPerSegment())
            {
                var (segments, left) = ExportPlanner.Segments(_session.Engine, indexList);
                skipped = left;
                _outputs = await Task.Run(() => exporter.ExportSegmentsAsync(segments, options, progress, _cts.Token));
            }
            else
            {
                var plan = _session.MixPlan;
                _outputs = await Task.Run(() => exporter.ExportAsync(plan, chapters, options, progress, _cts.Token));
            }
            ResultText = (_outputs.Count == 1 ? $"Written: {_outputs[0]}" : $"Written {_outputs.Count} files to {Path.GetDirectoryName(_outputs[0])}")
                         + (skipped > 0 ? $". {skipped} segment(s) without audio were left out." : "");
            AppHost.Log.LogInformation("Exported {Format} to {Output}", Format, options.OutputPath);
        }
        catch (OperationCanceledException)
        {
            ErrorText = "Export cancelled.";
        }
        catch (Exception ex)
        {
            AppHost.Log.LogError(ex, "Export failed");
            ErrorText = ex.Message;
        }
        finally
        {
            IsRunning = false;
            OnPropertyChanged(nameof(HasOutput));
        }
    }
}
