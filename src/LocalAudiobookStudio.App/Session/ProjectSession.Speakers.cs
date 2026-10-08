using LocalAudiobookStudio.App.ViewModels;
using LocalAudiobookStudio.Core.IO;
using LocalAudiobookStudio.Core.Model;

namespace LocalAudiobookStudio.App.Session;

/// <summary>Speaker definitions. Speaker edits are saved immediately and are not undoable.</summary>
public sealed partial class ProjectSession
{
    private void RebuildSpeakerVms()
    {
        var def = Project.Defaults.Speaker;
        var ordered = Project.Speakers.Values
            .OrderBy(s => s.Id.Equals(def, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var desired = new List<SpeakerViewModel>(ordered.Count);
        foreach (var s in ordered)
        {
            if (!_speakerVms.TryGetValue(s.Id, out var vm) || !ReferenceEquals(vm.Model, s))
                _speakerVms[s.Id] = vm = new SpeakerViewModel(s);
            desired.Add(vm);
        }
        foreach (var stale in _speakerVms.Keys.Where(k => !Project.Speakers.ContainsKey(k)).ToList())
            _speakerVms.Remove(stale);

        // Update the collection in place (never Clear): lists and combo boxes bound to it keep
        // their selection, which matters while the Speakers dialog is editing a speaker.
        for (int i = Speakers.Count - 1; i >= 0; i--)
            if (!desired.Contains(Speakers[i])) Speakers.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            var current = Speakers.IndexOf(desired[i]);
            if (current < 0) Speakers.Insert(i, desired[i]);
            else if (current != i) Speakers.Move(current, i);
        }
        for (int i = 0; i < Speakers.Count; i++) Speakers[i].Position = i + 1;
    }

    /// <inheritdoc cref="ProjectEngine.AddSpeaker"/>
    public Speaker AddSpeaker(string displayName, string worker, int voiceVariant = 0) =>
        Engine.AddSpeaker(displayName, worker, voiceVariant);


    /// <summary>Call after editing a speaker in place: saves it and refreshes everything that depends on it.</summary>
    public void SpeakerChanged(Speaker sp)
    {
        MarkSpeakerDirty(sp);
        RebuildSpeakerVms();
        RefreshAll();
    }

    /// <summary>Deletes a speaker; its segments move to <paramref name="replacementId"/>.</summary>
    public void DeleteSpeaker(Speaker sp, string replacementId)
    {
        if (!Project.Speakers.Remove(sp.Id)) return;
        foreach (var ch in Chapters)
        {
            var changed = false;
            foreach (var i in ch.Items.Where(i => i.IsTts && string.Equals(i.Model.Speaker, sp.Id, StringComparison.OrdinalIgnoreCase)))
            {
                i.Model.Speaker = replacementId;
                changed = true;
            }
            if (changed) MarkChapterDirty(ch);
        }
        if (string.Equals(Project.Defaults.Speaker, sp.Id, StringComparison.OrdinalIgnoreCase))
        {
            Project.Defaults.Speaker = replacementId;
            MarkProjectDirty();
        }
        _ = Saves.FlushAsync().ContinueWith(_ => ProjectStore.DeleteSpeakerFile(Project, sp.Id));
        RebuildSpeakerVms();
        RefreshAll();
        Undo.Clear(); // earlier steps may refer to the deleted speaker
    }

    public void SetProjectInfo(string title, string author, double targetLufs, int gapMs, int chapterGapMs, string defaultSpeaker,
        int sampleRate, string audioFormat, int opusKbps)
    {
        var f = Project.File;
        f.Defaults.AudioFormat = audioFormat;
        f.Defaults.OpusKbps = opusKbps;
        f.Title = title;
        f.Author = author;
        f.Defaults.TargetLoudnessLufs = targetLufs;
        f.Defaults.GapAfterMs = gapMs;
        f.Defaults.ChapterGapMs = chapterGapMs;
        if (Project.Speakers.ContainsKey(defaultSpeaker)) f.Defaults.Speaker = defaultSpeaker;
        if (sampleRate > 0 && sampleRate != f.Defaults.SampleRate)
        {
            f.Defaults.SampleRate = sampleRate;
            // Decoded audio is cached at the mix rate: start a new cache at the new one.
            var old = AudioCache;
            AudioCache = new Audio.Mixing.AudioBufferCache(sampleRate);
            old.Clear();
            AudioCacheReplaced?.Invoke();
        }
        MarkProjectDirty();
        RebuildSpeakerVms();
        RefreshAll();
    }

    /// <summary>
    /// Makes a WAV reference clip for a speaker from another format (workers expect WAV): Opus and MP3 are decoded
    /// here, anything else with ffmpeg. Returns its file name in the speakers folder.
    /// </summary>
    public async Task<string> ConvertVoiceFileAsync(Speaker sp, string source)
    {
        var name = sp.Id + ".ref.wav";
        var target = Path.Combine(Paths.SpeakersDir, name);
        if (Audio.Decoding.AudioDecoder.CanDecode(source))
        {
            await Task.Run(() => Audio.Decoding.AudioDecoder.WriteWav(Audio.Decoding.AudioDecoder.Decode(source), target));
        }
        else
        {
            var ffmpeg = LocalAudiobookStudio.Engine.Export.Ffmpeg.Locate(Services.AppHost.Settings.FfmpegPath, Services.AppHost.DataRoot.Root)
                         ?? throw new LocalAudiobookStudio.Engine.Export.FfmpegNotFoundException();
            await LocalAudiobookStudio.Engine.Export.Ffmpeg.ConvertToWavAsync(ffmpeg, source, target, CancellationToken.None);
        }
        return name;
    }

    /// <summary>Copies a voice reference clip into the speakers folder and returns its file name.</summary>
    public string ImportVoiceFile(Speaker sp, string source)
    {
        Directory.CreateDirectory(Paths.SpeakersDir);
        var name = sp.Id + ".ref" + Path.GetExtension(source).ToLowerInvariant();
        var target = Path.Combine(Paths.SpeakersDir, name);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, target, overwrite: true);
        return name;
    }
}
