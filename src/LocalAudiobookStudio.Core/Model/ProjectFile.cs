namespace LocalAudiobookStudio.Core.Model;

/// <summary>Contents of <c>local_audiobook_studio.json</c> (<see cref="FileName"/>) at the project root.</summary>
public sealed class ProjectFile
{
    /// <summary>The format version this build writes. Newer files are refused on load.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The project file's name in the project folder.</summary>
    public const string FileName = "local_audiobook_studio.json";

    /// <summary>Marks the file as ours: a project file without it is not a Local Audiobook Studio project.</summary>
    public const string AppId = "LocalAudiobookStudio";

    /// <summary>Suffix of the project folders the app creates ("My Book.audiobook").</summary>
    public const string FolderExtension = ".audiobook";

    public string App { get; set; } = AppId;
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";

    /// <summary>Optional cover image, relative to the project root.</summary>
    public string? CoverImage { get; set; }

    public List<Guid> ChapterOrder { get; set; } = new();

    /// <summary>
    /// A read-only project (the bundled examples): everything works, but nothing is saved into the project
    /// folder. Generated takes are written so they can be played, and deleted again when the project closes
    /// (see <see cref="IO.ReadOnlyProject"/>). Only set by hand in the project file.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReadOnly { get; set; }
    public ProjectDefaults Defaults { get; set; } = new();
}

public sealed class ProjectDefaults
{
    public string Speaker { get; set; } = "narrator";
    public int GapAfterMs { get; set; } = 400;
    public int ChapterGapMs { get; set; } = 2000;
    public double TargetLoudnessLufs { get; set; } = -18.0;
    /// <summary>
    /// The rate playback and export run at. Takes keep the rate their model made; they are resampled when mixed.
    /// </summary>
    public int SampleRate { get; set; } = 44100;

    /// <summary>
    /// How new takes, recordings and imported WAVs are stored: <see cref="AudioFormats.Opus"/> (small) or
    /// <see cref="AudioFormats.Wav"/>. Existing files keep their format until converted.
    /// </summary>
    public string AudioFormat { get; set; } = AudioFormats.Opus;

    /// <summary>
    /// Opus bit rate per channel in kbps (mono speech gets this, stereo twice). Only affects audio stored from then
    /// on: re-encoding Opus would only lose quality.
    /// </summary>
    public int OpusKbps { get; set; } = 64;

    /// <summary>
    /// The Opus qualities offered: smaller, standard, high. Nothing below 48: Concentus's encoder (libopus 1.1.2)
    /// degrades faster there than current libopus, for little extra saving.
    /// </summary>
    public static readonly IReadOnlyList<int> OpusKbpsOptions = [48, 64, 96];

    /// <summary>The rates offered: every rate both MP3 (LAME) and AAC encode, so any export format works.</summary>
    public static readonly IReadOnlyList<int> SampleRates = [16000, 22050, 24000, 32000, 44100, 48000];
}
