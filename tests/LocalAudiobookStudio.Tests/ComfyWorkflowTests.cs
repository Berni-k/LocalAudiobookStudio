using System.Text.Json.Nodes;
using LocalAudiobookStudio.Workers.Manifest;

namespace LocalAudiobookStudio.Tests;

/// <summary>ComfyUI workflows: the API format check and the settings their tags add.</summary>
public sealed class ComfyWorkflowTests
{
    private const string Workflow = """
        {
          "1": { "class_type": "PrimitiveFloat", "_meta": { "title": "{{cfg}}" }, "inputs": { "value": 7 } },
          "2": { "class_type": "PrimitiveInt", "_meta": { "title": "{{steps}}" }, "inputs": { "value": 30 } },
          "3": { "class_type": "PrimitiveStringMultiline", "_meta": { "title": "{{style}}" }, "inputs": { "value": "calm" } },
          "4": { "class_type": "PrimitiveBoolean", "_meta": { "title": "{{loud}}" }, "inputs": { "value": false } },
          "5": { "class_type": "PrimitiveString", "_meta": { "title": "{{text}}" }, "inputs": { "value": "" } },
          "6": { "class_type": "TTS", "inputs": { "prompt": "{{mood}}: {{text}}", "seed": "{{seed}}", "extra": "{{style}}", "model": ["9", 0] } },
          "7": { "class_type": "LoadAudio", "_meta": { "title": "{{voice}}" }, "inputs": { "audio": "ref.wav", "audioUI": "" } },
          "8": { "class_type": "SaveAudio", "inputs": { "audio": ["6", 0], "filename_prefix": "x" } }
        }
        """;

    [Fact]
    public void Tags_become_settings_in_order_without_the_built_in_ones()
    {
        var all = ComfyWorkflow.SettingsOf((JsonObject)JsonNode.Parse(Workflow)!);
        Assert.Equal("seed", all[0].Key); // this workflow has a {{seed}}
        Assert.Equal(SettingTypes.Seed, all[0].Type);
        var settings = all.Skip(1).ToList();

        Assert.Equal(["cfg", "steps", "style", "loud", "mood", "voice"], settings.Select(s => s.Key));
        Assert.Equal([SettingTypes.Float, SettingTypes.Int, SettingTypes.String, SettingTypes.Bool, SettingTypes.String, SettingTypes.Asset],
            settings.Select(s => s.Type));
        Assert.Equal(7, settings[0].Default!.GetValue<int>());
        Assert.True(settings[2].Multiline);
        Assert.Equal("calm", settings[2].Default!.GetValue<string>()); // the constant node wins over {{style}} in text
        Assert.Null(settings[4].Default);
        Assert.Equal("Mood", settings[4].Label);
        // A titled Load Audio node picks an audio asset; empty keeps the node's own file.
        Assert.Contains(".wav", settings[5].Extensions);
        Assert.Equal("Voice Reference", settings[5].AssetFolder);
        Assert.Null(settings[5].Default);
    }

    [Fact]
    public void The_seed_setting_appears_only_when_the_workflow_has_something_to_seed()
    {
        static List<string> Keys(string inputs) => ComfyWorkflow.SettingsOf((JsonObject)JsonNode.Parse(
            $$"""{ "1": { "class_type": "Sampler", "inputs": { {{inputs}} } } }""")!).Select(s => s.Key).ToList();

        Assert.Empty(Keys(""" "steps": 20 """));
        Assert.Empty(Keys(""" "seed": ["5", 0] """)); // a link, not a value
        Assert.Equal(["seed"], Keys(""" "noise_seed": 42 """));
        Assert.Equal(["seed"], Keys(""" "prompt": "go {{seed}}" """));
    }

    [Fact]
    public void Listening_tags_are_built_in_and_the_bridge_is_both_a_voice_and_a_listener()
    {
        var settings = ComfyWorkflow.SettingsOf((JsonObject)JsonNode.Parse("""
            {
              "1": { "class_type": "LoadAudio", "_meta": { "title": "{{audio}}" }, "inputs": { "audio": "a.wav" } },
              "2": { "class_type": "LoadAudio", "_meta": { "title": "{{reference}}" }, "inputs": { "audio": "r.wav" } },
              "3": { "class_type": "PreviewAny", "_meta": { "title": "{{result}}" }, "inputs": { "source": ["1", 0], "preview": "{{result}}" } }
            }
            """)!);
        Assert.Empty(settings);

        var catalog = new WorkerCatalog(Repo.Combine("workers"));
        var bridge = catalog.Get("comfyui_bridge")!;
        Assert.Contains(bridge, catalog.Generators);
        Assert.Contains(bridge, catalog.Transcribers);
        Assert.True(bridge.AlignsText);
        Assert.True(bridge.VerifiesSpeaker);
    }

    [Fact]
    public void An_unknown_settings_reader_is_a_load_error()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "w"));
        File.WriteAllText(Path.Combine(tmp.Path, "w", "manifest.json"), """
            { "id": "w", "displayName": "w", "envGroup": "basic", "modelVersion": "1",
              "settings": [ { "key": "preset", "type": "asset", "addsSettings": "nope" } ] }
            """);

        var error = Assert.Single(new WorkerCatalog(tmp.Path).LoadErrors);
        Assert.Contains("addsSettings", error);
    }

    [Fact]
    public void The_editor_format_is_refused()
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "ui.comfy.json");
        File.WriteAllText(file, """{ "nodes": [], "links": [] }""");

        var ex = Assert.Throws<InvalidDataException>(() => ComfyWorkflow.Read(file));
        Assert.Contains("Export (API)", ex.Message);
        Assert.Empty(ComfyWorkflow.Settings(file));
    }

    [Fact]
    public void The_bridge_adds_the_chosen_workflows_settings()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "assets"));
        File.WriteAllText(Path.Combine(tmp.Path, "assets", "a.comfy.json"), Workflow);
        var bridge = new WorkerCatalog(Repo.Combine("workers")).Get("comfyui_bridge")!;
        string Resolve(string rel) => Path.Combine(tmp.Path, rel);

        Assert.Equal(["workflow"], bridge.SettingsFor(new JsonObject(), Resolve).Select(s => s.Key));
        var keys = bridge.SettingsFor(new JsonObject { ["workflow"] = "assets/a.comfy.json" }, Resolve).Select(s => s.Key);
        Assert.Equal(["workflow", "seed", "cfg", "steps", "style", "loud", "mood", "voice"], keys);

        // The worker gets the workflow's audio assets as absolute paths, like the workflow itself.
        var merged = Engine.OneShot.MergeSettings(
            new JsonObject { ["workflow"] = "assets/a.comfy.json", ["voice"] = "assets/me.wav", ["mood"] = "assets/x" },
            null, bridge, Resolve);
        Assert.Equal(Resolve("assets/a.comfy.json"), merged["workflow"]!.GetValue<string>());
        Assert.Equal(Resolve("assets/me.wav"), merged["voice"]!.GetValue<string>());
        Assert.Equal("assets/x", merged["mood"]!.GetValue<string>()); // text stays text
    }
}
