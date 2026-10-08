using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAudiobookStudio.App.Services;
using LocalAudiobookStudio.Workers.Models;

namespace LocalAudiobookStudio.App.ViewModels;

/// <summary>
/// One model a worker can ask for (workers/models.json), in the Workers window: whether it is downloaded, its
/// size, and deleting it or pointing it at a folder of the user's own (a finetune).
/// </summary>
public sealed partial class ModelItemViewModel : ObservableObject
{
    private readonly WorkersDialogViewModel _owner;

    public ModelItemViewModel(WorkersDialogViewModel owner, string id, string? requiredBy)
    {
        _owner = owner;
        Id = id;
        RequiredBy = requiredBy;
        Refresh();
    }

    public string Id { get; }
    private ModelEntry? Entry => AppHost.Models.Catalog.Get(Id);
    public string Name => Entry?.Name ?? Id;

    /// <summary>Set for a model that comes along with another one (a tokenizer): that model's name.</summary>
    public string? RequiredBy { get; }
    public bool IsRequired => RequiredBy != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private ModelStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloading;

    public bool CanDownload => !IsDownloading && Status is ModelStatus.NotDownloaded or ModelStatus.Partial;
    [ObservableProperty] private string _detail = "";

    public bool IsDownloaded => Status is ModelStatus.Ready or ModelStatus.Partial;
    public bool IsOverride => Status == ModelStatus.Override;
    public bool IsReady => Status is ModelStatus.Ready or ModelStatus.Override;
    public bool CanOpen => IsDownloaded || IsOverride;

    private int _refreshes;

    public void Refresh()
    {
        Status = AppHost.Models.GetStatus(Id);
        Detail = Describe(extraBytes: 0);
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(IsOverride));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanOpen));
        if (Status != ModelStatus.Ready) return;

        // The folder can hold more than the listed files (voices, HartsyInference's converted weights): measured off
        // the UI thread, and shown when it is more than a rounding difference.
        var refresh = ++_refreshes;
        var listed = Entry?.TotalBytes ?? 0;
        _ = Task.Run(() => AppHost.Models.SizeOnDisk(Id)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && refresh == _refreshes && t.Result - listed > 1 << 20) Detail = Describe(t.Result - listed);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private string Describe(long extraBytes)
    {
        var size = Entry is { TotalBytes: > 0 } e ? ModelStore.FormatBytes(e.TotalBytes) : null;
        var extra = extraBytes > 0 ? $" + {ModelStore.FormatBytes(extraBytes)}" : "";
        return Status switch
        {
            ModelStatus.Ready => (size != null ? $"Downloaded · {size}" : "Downloaded") + extra,
            ModelStatus.Partial => "Partly downloaded: completes when a worker next needs it" + (size != null ? $" ({size} in all)" : ""),
            ModelStatus.Override => $"Your own folder: {AppHost.Settings.ModelOverrides.GetValueOrDefault(Id)}",
            _ => size != null ? $"Not downloaded · {size}, downloads when first used" : "Downloads when first used",
        } + (IsRequired ? $" · needed by {RequiredBy}" : "") + (Entry?.Gated == true ? " · gated: needs a Hugging Face token (Settings)" : "");
    }

    /// <summary>Downloads the model now instead of when a worker first needs it; the progress shows in its line.</summary>
    [RelayCommand]
    private async Task Download()
    {
        IsDownloading = true;
        try
        {
            await AppHost.Models.EnsureAsync(Id, progress: new Progress<LocalAudiobookStudio.Workers.Protocol.WorkerProgress>(p =>
            {
                if (p.Message != null) Detail = p.Message;
            }));
            _owner.AppendLog($"{Name}: downloaded.");
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Download model", ex.Message);
        }
        finally
        {
            IsDownloading = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task Delete()
    {
        var others = AppHost.Host!.WorkersUsingModel(Id).Select(w => w.DisplayName).Distinct().ToList();
        if (!await Dialogs.Confirm("Delete model",
                $"Delete {Name}? It is downloaded again when a worker next needs it." +
                (others.Count > 1 ? $"\n\nUsed by: {string.Join(", ", others)}." : "") +
                "\n\nWorkers using it are stopped first.",
                "Delete", "Cancel"))
            return;
        try
        {
            await AppHost.Host.DeleteModelAsync(Id);
            _owner.AppendLog($"{Name}: deleted.");
        }
        catch (Exception ex)
        {
            await Dialogs.ShowError("Delete model", ex.Message);
        }
        Refresh();
    }

    /// <summary>Points the model at a folder of the user's own; it isn't checked against models.json.</summary>
    [RelayCommand]
    private async Task UseOwnFolder()
    {
        var folder = await Dialogs.PickFolder($"Folder with your own {Name}");
        if (folder == null) return;
        AppHost.Host!.SetModelOverride(Id, folder);
        _owner.AppendLog($"{Name}: using {folder}. Workers using it restart before their next job; takes made with another model show as out of date.");
        Refresh();
    }

    [RelayCommand]
    private void UseDownloaded()
    {
        AppHost.Host!.SetModelOverride(Id, null);
        _owner.AppendLog($"{Name}: using the downloaded model again.");
        Refresh();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var folder = IsOverride ? AppHost.Settings.ModelOverrides.GetValueOrDefault(Id) : AppHost.Models.FolderOf(Id);
        if (folder != null && Directory.Exists(folder)) Dialogs.Reveal(folder);
    }
}
