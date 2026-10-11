namespace LocalAudiobookStudio.Core.IO;

/// <summary>Resolves the fixed layout of a project folder.</summary>
public sealed class ProjectPaths(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string ProjectFile => Path.Combine(Root, Model.ProjectFile.FileName);
    public string SpeakersDir => Path.Combine(Root, "speakers");
    public string ChaptersDir => Path.Combine(Root, "chapters");
    /// <summary>The folder of imported files (audio clips, ComfyUI workflows), shown in the Assets panel.</summary>
    public const string AssetsFolder = "assets";
    public string AssetsDir => Path.Combine(Root, AssetsFolder);
    public string AudioDir => Path.Combine(Root, "audio");
    public string ExportDir => Path.Combine(Root, "export");
    public string CacheDir => Path.Combine(Root, ".cache");

    /// <summary>Kept fused generations (<see cref="Model.FusionRecord"/>), one folder each, when the setting is on.</summary>
    public string FusedDir => Path.Combine(Root, "fused");
    public string FusionRecordDir(string id) => Path.Combine(FusedDir, id);

    /// <summary>Scratch space for exports; wiped before and after each export.</summary>
    public string TempDir => Path.Combine(Root, ".temp");

    public string SpeakerFile(string speakerId) => Path.Combine(SpeakersDir, speakerId + ".json");
    public string ChapterFile(Guid chapterId) => Path.Combine(ChaptersDir, chapterId.ToString("D") + ".json");
    public string SegmentAudioDir(Guid itemId) => Path.Combine(AudioDir, itemId.ToString("D"));

    /// <summary>The take's audio file in its stored format.</summary>
    public string TakeAudio(Guid itemId, Model.TakeInfo take) => Path.Combine(SegmentAudioDir(itemId), take.FileName);

    /// <summary>Where a new take's WAV goes (workers and recordings write WAV; it may be compressed afterwards).</summary>
    public string TakeWav(Guid itemId, int take) => Path.Combine(SegmentAudioDir(itemId), Model.TakeInfo.FileStem(take) + ".wav");
    public string TakeJson(Guid itemId, int take) => Path.Combine(SegmentAudioDir(itemId), Model.TakeInfo.FileStem(take) + ".json");
    public string TakePeaks(Guid itemId, int take) => Path.Combine(SegmentAudioDir(itemId), Model.TakeInfo.FileStem(take) + ".peaks");

    /// <summary>What a transcription worker heard in the take (<see cref="Transcripts.StoredTranscript"/>).</summary>
    public string TakeTranscript(Guid itemId, int take) => Path.Combine(SegmentAudioDir(itemId), Model.TakeInfo.FileStem(take) + ".transcript");

    /// <summary>Peaks for imported clips live in the rebuildable cache.</summary>
    public string ClipPeaks(string clipFile) =>
        Path.Combine(CacheDir, "peaks", Path.GetFileNameWithoutExtension(clipFile) + "-" + StableHash(clipFile) + ".peaks");

    /// <summary>Resolves a project-relative path (forward slashes allowed) to an absolute path.</summary>
    public string Resolve(string relative) =>
        Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Makes an absolute path inside the project relative, using forward slashes.</summary>
    public string MakeRelative(string absolute) =>
        Path.GetRelativePath(Root, absolute).Replace(Path.DirectorySeparatorChar, '/');

    private static string StableHash(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}
