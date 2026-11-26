# Voice menu clips

The Dare client's voice menu (V, then letters) plays one short clip per phrase. This folder makes
those clips. `phrases.json` is the single phrase table: the plugin embeds it, these scripts read
it, and `export.py` writes the client's copy.

Voice: [Chatterbox](https://github.com/resemble-ai/chatterbox) (MIT, runs locally)
copying `ref/ralph_low.wav` (macOS "Ralph", pitched down), then the "monster" treatment in
`voicetool.py` — approved by Chris on 2026-11-25.

## Setup (once)

About **1 GB** for the Python environment and **3 GB** for the model (downloaded to
`~/.cache/huggingface` on first run). Neither goes in git.

```bash
uv venv --python 3.11 .venv && uv pip install --python .venv/bin/python -r requirements.txt
```

## Making clips

```bash
PYTORCH_ENABLE_MPS_FALLBACK=1 .venv/bin/python generate.py --takes 4          # all phrases
PYTORCH_ENABLE_MPS_FALLBACK=1 .venv/bin/python generate.py --only gw,tr       # redo some
.venv/bin/python audition.py                                                  # pick at http://localhost:28417
.venv/bin/python export.py --client ~/oss-src/oss-client                      # into the client
```

`picks.json` (committed) records which take was picked per phrase. Adding a phrase: add it to
`phrases.json` with the next unused sound id (never reuse one), generate, pick, export, and commit
both repos.

Tests: `.venv/bin/python -m pytest -q`.
