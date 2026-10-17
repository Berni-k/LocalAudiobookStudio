# ComfyUI bridge

Runs a [ComfyUI](https://github.com/comfyanonymous/ComfyUI) workflow (API format, a project asset
`*.comfy.json`) for each segment and returns its audio. How to set it up and wire a workflow is in
[docs/comfyui.md](../../docs/comfyui.md); this file is about the implementation.

- Runs in the `basic` environment: `soundfile` decodes ComfyUI's FLAC, `websocket-client` follows progress.
  No torch, no GPU (`external: true`: the app gives it its own device, with "Parallel workers" lanes).
- Each lane uses server `lane % len(servers)` from the `servers` worker setting.
- Tags: the app turns a workflow's `{{tags}}` into settings in `src/LocalAudiobookStudio.Workers/Manifest/ComfyWorkflow.cs`
  (types, defaults, the built-in `text`, `seed` and `duration`); `worker.py` fills the same tags. Keep the
  two in step.
- One generation: fill the tags (audio settings are uploaded with `POST /upload/image` to `input/las/<hash>`),
  turn Save Audio nodes into Preview Audio, `POST /prompt`, follow `/ws` (falls back to polling
  `/history/<id>`), download the result with `GET /view`, write it as WAV, then `POST /history {"delete"}`.
- Cancel: `POST /queue {"delete"}` while waiting, `POST /interrupt {"prompt_id"}` only when this prompt is
  the one running (older servers ignore `prompt_id` and would stop another lane's job).
- Comfy Cloud: a server host under `comfy.org` switches `ComfyBridge.cloud` on. Paths get `/api`, requests carry
  `X-API-Key` (websocket: `&token=`), Save Audio nodes are kept (no temp folder), the result is found with
  `GET /jobs/<id>` and downloaded from `/view`'s redirect to a signed URL, which must be fetched without the key
  (`_download`), and cancel is always `POST /queue {"delete"}`. Uploads use the name the server returns (it ignores
  the subfolder). Cloud was only checked up to connecting, errors and unknown jobs: a full run needs credits.
- A failed or interrupted prompt is deleted from the history too. "out of memory" in ComfyUI's error makes
  the protocol restart this process; that only costs a reconnect.
