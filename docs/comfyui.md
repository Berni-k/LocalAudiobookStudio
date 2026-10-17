# ComfyUI bridge

The ComfyUI bridge generates segments with your own [ComfyUI](https://github.com/comfyanonymous/ComfyUI)
workflows instead of a built-in model. Anything ComfyUI can turn into audio works: speech, voice cloning, music,
sound effects. ComfyUI does the work, on this machine or another one. The bridge itself needs no GPU and only a
small Python environment.

## Setting up

1. **Start ComfyUI.** On another machine, start it with `--listen` so this one can reach it.
2. **Install the bridge.** In *Workers → Manage workers*, install **ComfyUI bridge**. In its settings:
   - **Servers**: the ComfyUI addresses, `host:port`, one per line. ComfyUI's default is `127.0.0.1:8188`.
     Left empty, the bridge uses [Comfy Cloud](#comfy-cloud).
   - **Parallel workers**: how many segments are sent at once. Each one uses the next server in the list
     (the first the first server, the second the second, and so on round the list).
   - **Timeout (s)**: how long one segment may take, including time waiting in ComfyUI's queue.
   - **Comfy Cloud API key**: only for [Comfy Cloud](#comfy-cloud).
3. **Export your workflow.** In ComfyUI, wire it up as described [below](#wiring-a-workflow), then save it
   with **Workflow → Export (API)**. The normal *Save* format doesn't work (see [why](#why-the-api-format)).
4. **Add it to the project.** Drop the file on the *Assets* panel or click **+**. It is checked and stored as
   `assets/<name>.comfy.json`.
5. **Make a speaker.** In *Speakers*, set the worker to **ComfyUI bridge** and pick the workflow as its
   **Workflow**. The workflow's settings appear below it.

Then generate as usual. Each segment queues one run of the workflow on a ComfyUI server, and its audio comes
back as a take.

## Comfy Cloud

Instead of your own ComfyUI you can use [Comfy Cloud](https://www.comfy.org/cloud), Comfy's hosted ComfyUI. It
needs a paid plan, and runs use your Comfy credits.

1. Create an API key at [platform.comfy.org](https://platform.comfy.org) under *Profile → API Keys*. It is shown
   once.
2. In the bridge's settings, leave **Servers** empty (or list `cloud.comfy.org`) and paste the key into
   **Comfy Cloud API key**. The key is kept in this machine's settings in plain text.
3. Use workflows as usual. Cloud has ComfyUI's own nodes plus a set of popular custom nodes (no Kokoro, VibeVoice
   or F5 at the time of writing, but ACE-Step, Stable Audio, Qwen3-TTS, ChatterBox and Whisper). A workflow that
   uses a node or model Cloud doesn't have is refused.

Cloud keeps results in your account until the run is deleted from its history, which the bridge does after
downloading. Its API is marked experimental by Comfy and may change. Servers can be mixed: a list can hold both
your own machines and `cloud.comfy.org`.

## Wiring a workflow

The bridge needs two things from a workflow: an **audio output**, and **tags** that say where the segment's
text and the speaker's settings go.

### The output

End the workflow in a **Save Audio** node (any of them: FLAC, MP3, Opus, Advanced) or a **Preview Audio**
node. The bridge runs Save Audio nodes as Preview Audio, so nothing piles up in ComfyUI's `output` folder.

### Tags

A tag is a name in double braces, like `{{text}}` or `{{steps}}`. Tag a value in one of two ways:

- **Title a node with the tag.** Right-click the node → *Title* and enter `{{steps}}`. This works for the
  constant nodes (Int, Float, String, String (Multiline), Boolean) and for **Load Audio**. To feed a constant
  into another node's widget, connect the constant to that widget's input (in older ComfyUI versions,
  right-click the node → *Convert widget to input* first).
- **Type the tag into a text widget**, alone or inside longer text: `{{mood}}`, or
  `A {{mood}} voice reading slowly`. The tag is replaced by the setting's text.

Tag names are letters, digits and `_`. The setting is labelled after the name: `voice_text` shows as
"Voice text". A tag used in several places is one setting, filled in everywhere.

### Built-in tags

These are filled from the segment instead of becoming settings:

| Tag | Value |
|---|---|
| `{{text}}` | The segment's text |
| `{{seed}}` | The take's seed: the speaker's *Seed* setting, or a random one, so regenerating gives a new take |
| `{{duration}}` | The segment's length in seconds (*Length* in the inspector) |

Without a `{{seed}}` tag, every input named `seed` or `noise_seed` gets the take's seed anyway.

### Setting types

Every other tag becomes a speaker setting. Its type comes from what is tagged:

| Tagged | Setting | Default | Left empty |
|---|---|---|---|
| Int node | Whole number | The node's value | The node's value |
| Float node | Decimal number | The node's value | The node's value |
| String node | Text | The node's value | The node's value |
| String (Multiline) node | Multi-line text | The node's value | The node's value |
| Boolean node | On/off | The node's value | The node's value |
| Load Audio node | An audio file from the project's assets | None | The node's own file on the ComfyUI server |
| `{{name}}` in a text widget | Text | Empty | Empty text |

Other nodes with exactly one widget can be tagged too; the setting's type follows the widget's value.

An audio setting lists the project's audio assets. Its **…** button adds a file: it is copied into
`assets/Voice Reference/` (and converted to WAV) so the project keeps it. The bridge uploads the file to
ComfyUI when a segment needs it. A workflow can have several audio settings, for example `{{voice}}` and
`{{background}}`.

The built-in `{{duration}}` follows the same fallback: a tagged Float node keeps its value for a segment
without a length, while `{{duration}}` in a text widget makes such a segment fail.

All settings can also be overridden per segment in the inspector, including the workflow itself.

### Example

`examples/The Last Lamp.audiobook/assets/VibeVoice_TTS_VoiceClone.comfy.json` clones a voice with the
ComfyUI-VibeVoice custom node:

| Node | Title | Wired into VibeVoice TTS | Becomes |
|---|---|---|---|
| String | `{{text}}` | `text` | The segment's text |
| Int | `{{seed}}` | `seed` | The take's seed |
| Float | `{{cfg}}` | `cfg_scale` | Setting "Cfg", default 1.3 |
| Int | `{{steps}}` | `inference_steps` | Setting "Steps", default 10 |
| Load Audio | `{{voice}}` | `speaker_1_voice` | Setting "Voice": the clip to clone |
| Preview Audio | – | its `Audio` output | The take |

## What it leaves on the server

- **Results** go to ComfyUI's `temp` folder, which ComfyUI empties each time it starts. Each run is deleted
  from ComfyUI's history once its audio is downloaded (failed and cancelled ones too).
- **Audio files** from audio settings are uploaded to ComfyUI's `input/las/` folder, named by a hash of their
  content, so each file is stored once however often it is used.

## Good to know

- **Changing a workflow file doesn't mark takes out of date**: only the chosen file and the settings count.
  Add a changed workflow as a new asset, or regenerate the segments.
- **Cancelling** takes a waiting run out of ComfyUI's queue, or stops it if it is running.
- **Progress** shows ComfyUI's sampler steps. If ComfyUI's websocket can't be reached, progress shows no steps
  but generation still works.
- **Sample rate and channels** are whatever the workflow produces.
- **Custom nodes** the workflow uses must be installed on every server in the list.

## Why the API format

ComfyUI saves two kinds of workflow file:

- The **editor format** (*Save*, *Export*) describes the canvas: where nodes sit, their colours and groups,
  links as a separate list, and widget values as unnamed lists that only make sense with each node's
  definition. It also holds things only the browser knows about, such as reroutes, muted nodes and subgraphs.
- The **API format** (*Export (API)*) is what the ComfyUI server runs: each node with its type and named
  inputs, links written as `[node, output]`.

When you press *Queue*, the browser converts the first into the second. The bridge talks to the server
directly, so it needs the second.
