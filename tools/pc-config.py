#!/usr/bin/env python3
"""Write the PC service's config.json from the project .env without printing any secret.

    tools/pc-config.py <output path> [--llm echo|claude]

Keys taken from .env: ELEVENLABS_KEY, ANTHROPIC_API_KEY (optional). Nothing is echoed.
"""
import json
import os
import sys

repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
env = {}
with open(os.path.join(repo, ".env"), encoding="utf-8") as f:
    for line in f:
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        env[k.strip()] = v.strip().strip('"').strip("'")

if len(sys.argv) < 2 or sys.argv[1].startswith("-"):
    # Never treat a flag (or a typo) as the output path: that would write the secrets into a file of that name.
    print(__doc__.strip(), file=sys.stderr)
    sys.exit(2)
out = sys.argv[1]
llm = "claude"
if "--llm" in sys.argv:
    llm = sys.argv[sys.argv.index("--llm") + 1]

cfg = {"elevenLabsKey": env.get("ELEVENLABS_KEY", ""), "llm": llm}
if env.get("ANTHROPIC_API_KEY"):
    cfg["anthropicApiKey"] = env["ANTHROPIC_API_KEY"]

with open(out, "w", encoding="utf-8") as f:
    json.dump(cfg, f, indent=2)
os.chmod(out, 0o600)
print(f"wrote {out}: elevenLabsKey={bool(cfg['elevenLabsKey'])} anthropicApiKey={'anthropicApiKey' in cfg} llm={llm}")
