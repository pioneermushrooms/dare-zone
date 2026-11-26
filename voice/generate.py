"""Generate raw takes for every voice phrase (or --only some) with Chatterbox.

    PYTORCH_ENABLE_MPS_FALLBACK=1 .venv/bin/python generate.py [--takes 4] [--only gw,tr]

Writes takes/<code>_<n>.wav (24 kHz). A take that runs far longer than its words is regenerated
with a new seed (up to 3 tries). --only replaces that phrase's takes; picks for it are kept.
"""
import argparse
import zlib

import torch
import torchaudio as ta
from chatterbox.tts import ChatterboxTTS

from voicetool import REF, TAKES, duration, exaggeration, load_phrases, too_long


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--takes", type=int, default=4)
    ap.add_argument("--only", default="", help="comma-separated codes, e.g. gw,tr")
    args = ap.parse_args()

    phrases = load_phrases()["phrases"]
    only = {c.strip() for c in args.only.split(",") if c.strip()}
    unknown = only - {p["code"] for p in phrases}
    if unknown:
        raise SystemExit(f"unknown codes: {', '.join(sorted(unknown))}")
    todo = [p for p in phrases if not only or p["code"] in only]

    TAKES.mkdir(exist_ok=True)
    device = "mps" if torch.backends.mps.is_available() else "cpu"
    model = ChatterboxTTS.from_pretrained(device=device)

    for p in todo:
        for old in TAKES.glob(f"{p['code']}_*.wav"):
            old.unlink()
        for n in range(args.takes):
            out = TAKES / f"{p['code']}_{n}.wav"
            for attempt in range(3):
                torch.manual_seed(zlib.crc32(p["code"].encode()) + 1000 * attempt + n)
                wav = model.generate(p["text"], exaggeration=exaggeration(p["delivery"]), cfg_weight=0.3,
                                     temperature=0.8, audio_prompt_path=str(REF))
                ta.save(str(out), wav, model.sr)
                if not too_long(p["text"], duration(out)):
                    break
            print(f"{p['code']} take {n} ({duration(out):.2f}s)", flush=True)


if __name__ == "__main__":
    main()
