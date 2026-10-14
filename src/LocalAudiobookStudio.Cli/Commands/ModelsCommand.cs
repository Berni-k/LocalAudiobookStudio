using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAudiobookStudio.Workers.Manifest;
using LocalAudiobookStudio.Workers.Models;
using LocalAudiobookStudio.Workers.Protocol;

namespace LocalAudiobookStudio.Cli.Commands;

/// <summary><c>las models</c>: the models of workers/models.json, their downloads, and pinning new versions.</summary>
public static class ModelsCommand
{
    public static async Task<int> RunAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var sub = tokens.Count > 0 ? tokens[0].ToLowerInvariant() : null;
        var rest = tokens.Skip(1).ToList();
        return sub switch
        {
            null => await ListAsync(ctx),
            "download" => await DownloadAsync(ctx, rest),
            "delete" => await DeleteAsync(ctx, rest),
            "pin" => await PinAsync(ctx, rest),
            _ => await ShowAsync(ctx, tokens),
        };
    }

    private static async Task<int> ListAsync(CliContext ctx)
    {
        var host = await ctx.StartHostAsync();
        var store = host.Models;
        var list = store.Catalog.All.OrderBy(m => m.Id).Select(m =>
        {
            var status = store.GetStatus(m.Id);
            return new
            {
                id = m.Id,
                name = m.Name,
                status,
                downloadBytes = m.TotalBytes,
                diskBytes = status is ModelStatus.NotDownloaded or ModelStatus.Override ? (long?)null : store.SizeOnDisk(m.Id),
                usedBy = host.WorkersUsingModel(m.Id).Select(w => w.Id).ToList(),
                overrideFolder = host.Settings.ModelOverrides.GetValueOrDefault(m.Id),
            };
        }).ToList();
        foreach (var m in list)
        {
            var size = m.diskBytes is long d ? ModelStore.FormatBytes(d) + " on disk" : ModelStore.FormatBytes(m.downloadBytes);
            ctx.Out($"{m.id,-34} {m.status,-14} {size,-18} {string.Join(", ", m.usedBy)}");
        }
        ctx.WriteJson(list);
        return Program.ExitOk;
    }

    private static async Task<int> ShowAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("models", tokens);
        if (a.Positionals.Count != 1) throw new UsageException("models: give one model id, or download, delete or pin.");
        var host = await ctx.StartHostAsync();
        var m = Model(host.Models, a.Positionals[0]);
        var info = new
        {
            id = m.Id,
            name = m.Name,
            status = host.Models.GetStatus(m.Id),
            folder = host.Models.FolderOf(m.Id),
            overrideFolder = host.Settings.ModelOverrides.GetValueOrDefault(m.Id),
            gated = m.Gated,
            sources = m.Sources.Select(s => s.ToString()).ToList(),
            requires = m.Requires,
            files = m.Files.Select(f => new { path = f.Key, size = f.Value.Size }).ToList(),
            linkedTo = host.Models.LinkedFolders(m.Id),
            usedBy = host.WorkersUsingModel(m.Id).Select(w => w.Id).ToList(),
        };
        ctx.Out($"{m.Name} ({m.Id}) — {info.status}");
        ctx.Out($"  folder: {info.folder}{(info.overrideFolder != null ? $" (overridden: {info.overrideFolder})" : "")}");
        ctx.Out($"  sources: {string.Join(" · ", info.sources)}{(m.Gated ? " (gated: needs a token)" : "")}");
        if (m.Requires.Count > 0) ctx.Out($"  requires: {string.Join(", ", m.Requires)}");
        ctx.Out($"  {m.Files.Count} file(s), {ModelStore.FormatBytes(m.TotalBytes)}");
        foreach (var l in info.linkedTo) ctx.Out($"  linked into: {l}");
        if (info.usedBy.Count > 0) ctx.Out($"  used by: {string.Join(", ", info.usedBy)}");
        ctx.WriteJson(info);
        return Program.ExitOk;
    }

    private static async Task<int> DownloadAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("models download", tokens);
        if (a.Positionals.Count == 0) throw new UsageException("models download: give one or more model ids.");
        var host = await ctx.StartHostAsync();
        foreach (var id in a.Positionals)
        {
            var m = Model(host.Models, id);
            ctx.Info($"{m.Name}…");
            var last = "";
            var location = await host.Models.EnsureAsync(m.Id, progress: new Progress<WorkerProgress>(p =>
            {
                if (p.Message == last) return;
                last = p.Message ?? "";
                ctx.Info("  " + last);
            }), ct: ctx.Cancel);
            ctx.Out($"{m.Id}: {location.Path}");
        }
        return Program.ExitOk;
    }

    private static async Task<int> DeleteAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("models delete", tokens);
        if (a.Positionals.Count == 0) throw new UsageException("models delete: give one or more model ids.");
        var host = await ctx.StartHostAsync();
        foreach (var id in a.Positionals)
        {
            var m = Model(host.Models, id);
            await host.DeleteModelAsync(m.Id);
            ctx.Out($"{m.Id}: deleted");
        }
        return Program.ExitOk;
    }

    /// <summary>
    /// <c>las models pin [ids…] [--all] [--file models.json] [--from folder]</c>: fills in the file lists and hashes,
    /// for maintainers adding a model or moving its revision. Writes models.json.
    /// </summary>
    private static async Task<int> PinAsync(CliContext ctx, IReadOnlyList<string> tokens)
    {
        var a = CliArgs.Parse("models pin", tokens,
            new OptionSpec("--all", HasValue: false), new OptionSpec("--missing", HasValue: false), new OptionSpec("--file"), new OptionSpec("--from"));
        var file = a.Get("--file") ?? Path.Combine(WorkerCatalog.DefaultRoot, ModelCatalog.FileName);
        if (!File.Exists(file)) throw new UsageException($"models pin: {file} doesn't exist (give --file).");
        if (a.Has("--from") && a.Positionals.Count != 1) throw new UsageException("models pin: --from needs exactly one model id.");
        if (a.Positionals.Count == 0 && !a.Has("--all") && !a.Has("--missing"))
            throw new UsageException("models pin: give model ids, --missing (models without files) or --all.");

        var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        var catalog = ModelCatalog.Load(Path.GetDirectoryName(Path.GetFullPath(file))!);
        foreach (var e in catalog.LoadErrors) ctx.Error(e);
        var ids = a.Positionals.Count > 0 ? a.Positionals.Select(id => Model(catalog, id).Id).ToList()
            : catalog.All.Where(m => m.Sources.Any(s => s.IsHub) && (a.Has("--all") || m.Files.Count == 0)).Select(m => m.Id).OrderBy(id => id).ToList();

        // Gated repos need the token for their file list too (from the app's settings).
        string? token = null;
        try { token = (await ctx.StartHostAsync()).Settings.HuggingFaceToken; } catch (SetupException) { }
        var pinner = new ModelPinner { Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim() };
        var failures = 0;
        foreach (var id in ids)
        {
            var entry = catalog.Get(id)!;
            ctx.Info($"{id}:");
            try
            {
                var node = (JsonObject)root["models"]![root["models"]!.AsObject().First(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase)).Key]!;
                foreach (var note in await pinner.PinAsync(entry, node, a.Get("--from"), ctx.Detail, ctx.Cancel)) ctx.Info("  " + note);
                ModelPinner.Save(file, root); // after each model, so an interrupted run keeps what it did
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures++;
                ctx.Error($"  {ex.Message}");
            }
        }
        ctx.Out($"Pinned {ids.Count - failures} of {ids.Count} model(s) in {file}.");
        return failures == 0 ? Program.ExitOk : Program.ExitFailures;
    }

    private static ModelEntry Model(ModelStore store, string id) => Model(store.Catalog, id);

    private static ModelEntry Model(ModelCatalog catalog, string id) =>
        catalog.Get(id) ?? throw new UsageException($"models: unknown model '{id}'. 'las models' lists them.");
}
