#!/usr/bin/env python3
"""Oracle generator for the golden/parity test.

Reuses the numeric core of reference/sync_srt.py (subtitle_signal,
best_offset_for_ratio, DEFAULT_RATIOS, parse_srt) to produce the reference
result the TypeScript port must match, and writes it to
test/fixtures/expected.json.

Only numpy + scipy are required (both install as wheels, no C compiler). The
reference module imports webrtcvad at top level, but we never run VAD here — the
speech signal is loaded from the committed test/fixtures/speech_signal.json — so
we inject a stub `webrtcvad` module before importing sync_srt.

Usage:  pip install numpy scipy   then:   python test/oracle/oracle.py
"""

import importlib.util
import json
import sys
import types
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
REF = REPO / "reference" / "sync_srt.py"
SIGNAL_JSON = REPO / "test" / "fixtures" / "speech_signal.json"
SRT = REPO / "test" / "fixtures" / "sample.srt"
OUT = REPO / "test" / "fixtures" / "expected.json"

# Must match analyze()'s default maxOffset in lib/sync.ts (and the test call).
MAX_OFFSET_S = 120.0

# sync_srt.py does `import webrtcvad` at module load. We don't run VAD here, so
# stub it out to avoid needing the (compiler-requiring) native package.
if "webrtcvad" not in sys.modules:
    sys.modules["webrtcvad"] = types.ModuleType("webrtcvad")


def load_reference():
    spec = importlib.util.spec_from_file_location("sync_srt_ref", REF)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main():
    ref = load_reference()

    # Load the committed speech signal (0/1 at SIGNAL_HZ).
    data = json.loads(SIGNAL_JSON.read_text(encoding="utf-8"))
    assert data["signalHz"] == ref.SIGNAL_HZ, "signalHz mismatch with reference"
    speech = np.asarray(data["signal"], dtype=np.float32)
    assert len(speech) == data["length"]

    blocks = ref.parse_srt(SRT)

    results = []
    for label, ratio in ref.DEFAULT_RATIOS.items():
        offset, score = ref.best_offset_for_ratio(speech, blocks, ratio, MAX_OFFSET_S)
        results.append(
            {"label": label, "ratio": ratio, "offset": offset, "score": score}
        )

    # Rank exactly like main(): highest score first (stable sort preserves the
    # DEFAULT_RATIOS order on ties, matching JS Array.sort stability).
    ranked = sorted(results, key=lambda r: r["score"], reverse=True)
    best = ranked[0]

    out = {
        "meta": {
            "source": "reference/sync_srt.py via test/oracle/oracle.py",
            "signalHz": ref.SIGNAL_HZ,
            "signalLength": len(speech),
            "maxOffsetS": MAX_OFFSET_S,
            "numpy": np.__version__,
        },
        "best": best,
        "all": results,  # DEFAULT_RATIOS order (matches lib/sync.ts input order)
    }
    OUT.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")

    print(f"Wrote {OUT}")
    print(
        f"Best: {best['label']}  ratio={best['ratio']:.6f}  "
        f"offset={best['offset']:+.3f}s  score={best['score']:.6f}"
    )
    for r in results:
        print(
            f"  {r['label']:22s} ratio={r['ratio']:.6f}  "
            f"offset={r['offset']:+7.3f}s  score={r['score']:.6f}"
        )


if __name__ == "__main__":
    main()
