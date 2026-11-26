"""Copy the picked takes into the Dare client, finished, plus its generated phrase table.

    .venv/bin/python export.py --client ~/oss-src/oss-client

Writes <client>/assets/classic/sound/voice<id>.wav (mono 16-bit, 11 025 Hz, monster treatment,
silence-trimmed, loudness-normalised) and <client>/crates/oss-client/src/game/voice_table.rs.
Refuses if any phrase has no pick.
"""
import argparse
import json
from pathlib import Path

from voicetool import FINISH, MONSTER, PICKS, TAKES, ffmpeg, load_phrases, rust_table


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--client", required=True, type=Path)
    client = ap.parse_args().client.expanduser()
    sound_dir = client / "assets/classic/sound"
    table_rs = client / "crates/oss-client/src/game/voice_table.rs"
    if not sound_dir.is_dir() or not table_rs.parent.is_dir():
        raise SystemExit(f"{client} doesn't look like the oss-client checkout")

    table = load_phrases()
    picks = json.loads(PICKS.read_text()) if PICKS.exists() else {}
    missing = [p["code"] for p in table["phrases"]
               if p["code"] not in picks or not (TAKES / f"{p['code']}_{picks[p['code']]}.wav").exists()]
    if missing:
        raise SystemExit(f"no usable pick for: {', '.join(missing)} (audition.py, or generate.py --only ...)")

    for p in table["phrases"]:
        raw = TAKES / f"{p['code']}_{picks[p['code']]}.wav"
        ffmpeg(raw, sound_dir / f"voice{p['sound']}.wav", f"{MONSTER},{FINISH}",
               "-ar", "11025", "-c:a", "pcm_s16le")
    table_rs.write_text(rust_table(table))
    print(f"exported {len(table['phrases'])} clips to {sound_dir} and {table_rs}")


if __name__ == "__main__":
    main()
