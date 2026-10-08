using System.Globalization;

namespace LocalAudiobookStudio.Cli;

/// <summary>A wrong command line: printed with a pointer to the help, exit code 2.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>Something the app must set up first (a worker, ffmpeg, a free project): exit code 3.</summary>
public sealed class SetupException(string message) : Exception(message);

/// <summary>An option a command accepts: <c>--name</c>, an optional short alias, and whether it takes a value.</summary>
public sealed record OptionSpec(string Name, string? Alias = null, bool HasValue = true, bool Repeatable = false);

/// <summary>
/// The options and positional arguments of one command or project step. Options are <c>--name value</c>,
/// <c>--name=value</c> or flags; anything else is positional. <c>--</c> makes the rest positional.
/// </summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _context;

    public IReadOnlyList<string> Positionals { get; }

    private CliArgs(string context, Dictionary<string, List<string>> values, List<string> positionals)
    {
        _context = context;
        _values = values;
        Positionals = positionals;
    }

    public static CliArgs Parse(string context, IReadOnlyList<string> tokens, params OptionSpec[] specs)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        var onlyPositionals = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (onlyPositionals || t == "-" || !t.StartsWith('-')) { positionals.Add(t); continue; }
            if (t == "--") { onlyPositionals = true; continue; }

            string key = t, value = "";
            bool inline = false;
            var eq = t.IndexOf('=');
            if (t.StartsWith("--") && eq > 0) { key = t[..eq]; value = t[(eq + 1)..]; inline = true; }
            var spec = specs.FirstOrDefault(s => s.Name.Equals(key, StringComparison.OrdinalIgnoreCase)
                                                 || (s.Alias != null && s.Alias.Equals(key, StringComparison.Ordinal)))
                       ?? throw new UsageException($"{context}: unknown option {key}.");
            if (spec.HasValue && !inline)
            {
                if (i + 1 >= tokens.Count) throw new UsageException($"{context}: {spec.Name} needs a value.");
                value = tokens[++i];
            }
            else if (!spec.HasValue && inline)
            {
                throw new UsageException($"{context}: {spec.Name} takes no value.");
            }
            if (!values.TryGetValue(spec.Name, out var list)) values[spec.Name] = list = new List<string>();
            else if (!spec.Repeatable) throw new UsageException($"{context}: {spec.Name} is given twice.");
            list.Add(value);
        }
        return new CliArgs(context, values, positionals);
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.TryGetValue(name, out var l) ? l[^1] : null;

    public IReadOnlyList<string> GetAll(string name) => _values.TryGetValue(name, out var l) ? l : [];

    public int? GetInt(string name, int min = int.MinValue, int max = int.MaxValue)
    {
        if (Get(name) is not { } s) return null;
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
            throw new UsageException($"{_context}: {name} must be a whole number{(min > int.MinValue ? $" from {min}" : "")}{(max < int.MaxValue ? $" to {max}" : "")}.");
        return v;
    }

    public double? GetDouble(string name)
    {
        if (Get(name) is not { } s) return null;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            throw new UsageException($"{_context}: {name} must be a number.");
        return v;
    }

    /// <summary>"--chapter 1,3-5" (repeatable) as zero-based chapter indexes, or null when not given.</summary>
    public HashSet<int>? GetChapters(string name = "--chapter")
    {
        if (!Has(name)) return null;
        var set = new HashSet<int>();
        foreach (var part in GetAll(name).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var a) && int.TryParse(part[(dash + 1)..], out var b) && a >= 1 && b >= a)
                for (int n = a; n <= b; n++) set.Add(n - 1);
            else if (int.TryParse(part, out var n) && n >= 1) set.Add(n - 1);
            else throw new UsageException($"{_context}: {name} takes chapter numbers like 3, 1,4 or 2-5.");
        }
        return set;
    }

    /// <summary>"--set key=value" pairs.</summary>
    public List<(string key, string value)> GetPairs(string name)
    {
        var result = new List<(string, string)>();
        foreach (var v in GetAll(name))
        {
            var eq = v.IndexOf('=');
            if (eq <= 0) throw new UsageException($"{_context}: {name} takes key=value, not '{v}'.");
            result.Add((v[..eq].Trim(), v[(eq + 1)..]));
        }
        return result;
    }

    public void NoPositionals()
    {
        if (Positionals.Count > 0) throw new UsageException($"{_context}: unexpected argument '{Positionals[0]}'.");
    }
}
