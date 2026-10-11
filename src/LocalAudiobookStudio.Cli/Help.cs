namespace LocalAudiobookStudio.Cli;

internal static class Help
{
    public static string Text(string? topic) => topic?.ToLowerInvariant() switch
    {
        "tts" => Tts,
        "transcribe" => Transcribe,
        "project" => Project,
        _ => Main,
    };

    private const string Main = """
        las — Local Audiobook Studio on the command line

        Uses the app's settings, installed workers and devices (set them up in the app). Results go to stdout,
        progress to stderr. Ctrl+C cancels cleanly.

        Commands:
          tts        Text to a WAV file, like the speaker preview (no project involved)
          transcribe What a transcription worker hears in audio files, with word timings; compared with
                     the text the audio should contain when you give it
          project    Run steps on a project: import, erase-takes, generate, export, status, …
          workers    List workers and whether they're installed; 'workers <id>' shows settings and voices;
                     'workers install <id…>' installs workers like the Workers window (--runtime <name> for
                     another runtime than the chosen one), 'workers repair <id…>', 'workers uninstall <id…>'
          models     The models workers download: status, size on disk, which workers use them
                     ('models <id>', 'models download <id>', 'models delete <id>'; 'models pin <id>' fills
                     in a new model's files and hashes in workers/models.json)
          devices    GPUs found and the devices workers run on
          doctor     Check the machine: GPU, driver, uv, CUDA libraries, HartsyInference, the internal
                     worker, ffmpeg, disk space
                     (--fix downloads a missing uv, and ffmpeg on Windows, into the data folder)
          split      Show how a text file would be split into chapters and segments
          version    Print the version

        Global options:
          --json          Print the result as JSON on stdout
          -v, --verbose   Show details and worker logs on stderr
          -q, --quiet     Only errors on stderr
          -h, --help      Help ('las help tts', 'las help transcribe', 'las help project')

        Exit codes: 0 ok, 1 something failed, 2 wrong usage, 3 setup needed (worker, ffmpeg, project open in
        the app), 130 cancelled.

        """;

    private const string Tts = """
        las tts [text…] [options]

        Generates speech for a text and writes a WAV. No project is changed. The text comes from the arguments,
        from stdin when there are none, or from --input files. Several inputs are one batch: the worker starts
        once. Text longer than the worker takes is generated in parts and joined.

          -o, --output <path>    .wav file; '-' writes the WAV to stdout. With --input: a folder (the default
                                 is next to each text file), or a .wav for a single input file
          -i, --input <path>     A .txt/.md file or a folder of them (repeatable)
          -r, --recursive        Include subfolders of input folders
              --skip-existing    Skip inputs whose WAV already exists
          -w, --worker <id>      Worker (default: kokoro); see 'las workers'
              --runtime <name>   Runtime of the worker (python, onnx, onnx-cuda, hartsy); see 'las workers <id>'
              --voice <v>        A voice of the worker, or a reference clip to clone (workers that clone)
              --set key=value    A worker setting (repeatable); see 'las workers <id>'
              --seed <n>         Seed for repeatable results
              --length <sec>     Length, for workers that take one (otherwise named in the text, or 5 s)
              --gap <ms>         Silence between paragraphs (default 400)
          -p, --project <dir>    Use a speaker of this project (its worker, voice and settings) …
          -s, --speaker <id>     … this one

        Examples:
          las tts "Hello there." -o hello.wav
          echo "Piped text" | las tts -o out.wav --voice am_michael
          las tts -i chapters/ -o audio/ --skip-existing
          las tts -p MyBook -s narrator -i intro.txt -o intro.wav

        """;

    private const string Transcribe = """
        las transcribe <audio…> [options]

        Listens to audio files (WAV, Opus, MP3, or folders of them) and prints what was said. With the text
        the audio should contain, it also lists the words that are missing, extra or heard differently, and
        the word error rate. Case, punctuation and accents don't count.

        What a worker does depends on its tags, and is what it is asked for unless --mode says otherwise:
          transcribe-align  Transcription and Alignment (Whisper, Parakeet): the words heard, with their times
          transcribe        Transcription: the words heard, without times
          align             Alignment alone (MMS forced aligner): the times of a given text's words. It needs
                            -t or -f, always prints the word timings, and reports no differences.

          -t, --text <text>        The text the audio should contain (one audio file)
          -f, --text-file <path>   The same from a file; for several audio files, a folder of <audio name>.txt
              --words              Print every word with its start and end time
              --mode <mode>        transcribe, align or transcribe-align (default: the most the worker does)
              --speakers <a,b>     Who may be speaking, in order; --speaker-count <n> how many voices. Hints
                                   for workers tagged Multi-speaker, which say who said each word ([S0] …)
          -w, --worker <id>        Worker (default: the first installed transcription worker)
              --runtime <name>     Runtime of the worker (internal, internal-gpu, python, onnx, onnx-cuda);
                                   see 'las workers <id>'
              --device gpu|cpu     Where it runs this time (default: the worker's Device setting)
              --set key=value      A worker setting (repeatable); see 'las workers <id>'
          -r, --recursive          Include subfolders of input folders

        Examples:
          las transcribe take.wav --words
          las tts "The quick brown fox." -o fox.wav
          las transcribe fox.wav -t "The quick brown fox."
          las --json transcribe audio/ -f texts/

        """;

    private const string Project = """
        las project <folder> <step> [options] [<step> [options]]…

        Opens the project once and runs the steps in order. A step with failures stops the rest. Without
        steps, shows the status. The project must not be open in the app at the same time.

        Steps:
          import <file|->        Replace all chapters with the text (creates the project if the folder has none)
              --keep-takes       Chapters with the same title keep the takes of unchanged segments
              --max-chars <n>    Longest segment (also limited by each speaker's worker)
              --chapter-regex <r>  Pattern for chapter headings
              --every-line       Every line is a paragraph
              --no-headings      Don't speak chapter headings
              --names <a,b>      Speaker names to recognise as labels
              --title <t>        Title, when the import creates the project
          erase-takes            Delete all generated takes (locked segments and microphone recordings keep theirs)
          generate               Generate what's not generated, stale or failed, and wait for it
              --all              Every segment (a new take each)
              --failed           Only failed segments
              -c, --chapter <n>  Only these chapters: 3, 1,4 or 2-5 (repeatable)
          export                 Render and encode with ffmpeg
              -o, --output <p>   File (m4b, mp3) or folder; default: the project's export folder
              -f, --format <f>   m4b (default), mp3, mp3-chapters, wav, or wav-segments / mp3-segments:
                                 one file per segment, named by its [id: …] or its number in the chapter
              --bitrate <kbps>   Default 96 (m4b) or 128 (mp3)
              --mono, --limiter, --title <t>, --author <a>, --cover <image>
              --complete-only    Leave out chapters with missing, stale or failed audio
              -c, --chapter <n>  Only these chapters
          status                 Counts of ready, not generated, stale and failed segments per chapter
          timeline               Every segment with its start, length and state (-c to limit)
          speakers               The project's speakers
          speaker <id>           Change a speaker: --worker <id>, --voice <v>, --clone <clip>, --set k=v,
                                 --unset k, --name <n>, --default; --create to add a new one

        Examples:
          las project MyBook import book.txt erase-takes generate export -o book.m4b
          las project MyBook speaker narrator --voice bf_emma generate
          las --json project MyBook status

        """;
}
