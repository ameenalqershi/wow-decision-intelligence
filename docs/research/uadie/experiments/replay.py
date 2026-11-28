"""Offline experiment runner. Invoke once per stage with a new output directory."""
import gzip
import json
import os
import pathlib
import platform
import random
import subprocess
import sys
import time

stage, dest = sys.argv[1:]
arms = {"first":["full","warm","partition","reuse","box"],"second":["box","pair"],"final":["full","warm","partition","reuse","box","pair"]}[stage]
split = "holdout" if stage == "final" else "dev"
root = pathlib.Path(__file__).resolve().parents[4]
dll = root / "research/AlgorithmDiscoveryLab/bin/Release/net10.0/AlgorithmDiscoveryLab.dll"
dest = pathlib.Path(dest)
dest.mkdir(parents=True,exist_ok=False)
rng = random.Random(236)
core = min(os.sched_getaffinity(0))
meta = dict(stage=stage,split=split,core=core,host=platform.platform(),cpu=pathlib.Path("/proc/cpuinfo").read_text().split("model name")[1].splitlines()[0],sdk=subprocess.check_output(["dotnet","--version"],text=True).strip(),seed=236,launches=[])
with gzip.open(dest/"raw.jsonl.gz","wt") as raw:
    for repetition in range(3):
        order = arms.copy()
        rng.shuffle(order)
        for arm in order:
            cmd = ["taskset","-c",str(core),"dotnet",str(dll),arm,split,str(repetition)]
            start = time.monotonic()
            try:
                result = subprocess.run(cmd,capture_output=True,text=True,timeout=10)
                output, status = result.stdout, result.returncode
            except subprocess.TimeoutExpired as exc:
                status, output = "timeout", (exc.stdout or b"").decode()
            meta["launches"].append(dict(arm=arm,repetition=repetition,status=status,processWall=time.monotonic()-start))
            raw.write(output[:output.rfind("\n")+1])
            (dest/"environment.json").write_text(json.dumps(meta,indent=2)+"\n")
            if status != 0: sys.exit(f"{arm} stopped: {status}")
