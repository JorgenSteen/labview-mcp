# =============================================================================
# analyze_run.py - where did the wall clock of one benchmark build go?
# Reads a root agent's transcript and every agent it spawned (any depth) and prints,
# per agent and in total: model time, time inside each tool, waits, errors, regenerations
# and the slowest single calls - the input for "what in the MCP should be faster".
# =============================================================================
"""
    python -I scripts/bench/analyze_run.py <subagents-dir> <root-agent-id> [--json out.json]

<subagents-dir> is ~/.claude/projects/<project>/<session>/subagents. Agents name their parent in
agent-<id>.meta.json (`parentAgentId`), so the tree is rebuilt from those files.

Time accounting per agent, from the transcript's timestamps:
  model  = assistant message time - previous user/tool-result message time (one model turn);
  tool   = tool_result time - tool_use time, per tool name;
  a gap longer than IDLE_SECONDS inside a model turn of an agent that has running children is
  WAITING (for its children), not thinking, and is reported separately.
Tool calls sent together (before any result came back) return together, so each one's elapsed
time is the BATCH's, not its own. They are counted in the `batched` column and left out of
`min` / `avg s`, which measure solo calls only. (The first version charged a 30 s LabVIEW start
to the 0.4 s .ctl reads batched beside it.) The span is the wall clock that counts.
"""
import collections
import glob
import json
import os
import re
import sys
from datetime import datetime

IDLE_SECONDS = 60.0
ERROR_RE = re.compile(r'"ok"\s*:\s*false|"errorCode"\s*:\s*-?[1-9]|An error occurred invoking|"executable"\s*:\s*false|eBad|Error \d{2,}', re.I)


def ts(s):
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


def short(name):
    return re.sub(r"^mcp__(plugin_labview-mcp_)?labview__", "", name)


def tool_result_text(c):
    v = c.get("content")
    if isinstance(v, list):
        return " ".join(x.get("text", "") for x in v if isinstance(x, dict))
    return str(v or "")


def analyse(path):
    a = dict(model=0.0, wait=0.0, turns=0, out_tokens=0, calls=collections.Counter(),
             tool=collections.Counter(), errors=collections.Counter(), slow=[], first=None,
             last=None, gens=collections.Counter(), spawn_times=[], ctx_max=0,
             batched=collections.Counter())
    started, prev, seen = {}, None, set()
    pending_batch, batch_of = [], {}
    for line in open(path, encoding="utf-8"):
        try:
            o = json.loads(line)
        except ValueError:
            continue
        t, m = o.get("timestamp"), o.get("message")
        if not t or not isinstance(m, dict):
            continue
        T = ts(t)
        a["first"] = a["first"] or T
        a["last"] = T
        content = m.get("content") if isinstance(m.get("content"), list) else []
        if o.get("type") == "assistant":
            mid = m.get("id")
            if mid not in seen:
                seen.add(mid)
                u = m.get("usage") or {}
                a["out_tokens"] += u.get("output_tokens") or 0
                ctx = sum(u.get(k) or 0 for k in ("input_tokens", "cache_read_input_tokens", "cache_creation_input_tokens"))
                a["ctx_max"] = max(a["ctx_max"], ctx)
                if prev is not None:
                    dt = (T - prev).total_seconds()
                    a["turns"] += 1
                    if dt > IDLE_SECONDS and a["spawn_times"]:
                        a["wait"] += dt
                    else:
                        a["model"] += dt
            for c in content:
                if isinstance(c, dict) and c.get("type") == "tool_use":
                    n = short(c["name"])
                    if n in ("Bash", "PowerShell"):
                        n += ":" + (c["input"].get("command", "").strip().split() or ["?"])[0][:20]
                    a["calls"][n] += 1
                    started[c["id"]] = (n, T, c.get("input", {}))
                    pending_batch.append(c["id"])
                    if n in ("Agent", "Task"):
                        a["spawn_times"].append(T)
                    if n in ("generate_vi", "generate_vis", "convert_aixml_to_vi", "generate_vi_with_events"):
                        inp = c.get("input", {})
                        key = inp.get("viPath") or inp.get("outputPath") or inp.get("vi_path") or json.dumps(inp)[:80]
                        a["gens"][key] += 1
            prev = T
        elif o.get("type") == "user":
            if pending_batch:
                for i in pending_batch:
                    batch_of[i] = len(pending_batch)
                pending_batch = []
            for c in content:
                if isinstance(c, dict) and c.get("type") == "tool_result" and c.get("tool_use_id") in started:
                    n, T0, inp = started.pop(c["tool_use_id"])
                    dt = (T - T0).total_seconds()
                    if n not in ("Agent", "Task"):
                        if batch_of.get(c["tool_use_id"], 1) > 1:
                            a["batched"][n] += 1
                        else:
                            a["tool"][n] += dt
                            a["slow"].append((dt, n, json.dumps(inp)[:140]))
                    txt = tool_result_text(c)
                    if c.get("is_error") or (not n.startswith(("Read", "Grep", "Glob", "Bash", "PowerShell", "Write", "Edit")) and ERROR_RE.search(txt[:4000])):
                        a["errors"][n] += 1
            prev = T
    a["span"] = (a["last"] - a["first"]).total_seconds() if a["first"] else 0.0
    return a


def main():
    d, root = sys.argv[1], sys.argv[2]
    metas = {}
    for f in glob.glob(os.path.join(d, "agent-*.meta.json")):
        aid = os.path.basename(f)[6:-10]
        metas[aid] = json.load(open(f, encoding="utf-8"))
    children = collections.defaultdict(list)
    for aid, m in metas.items():
        if m.get("parentAgentId"):
            children[m["parentAgentId"]].append(aid)
    order, stack = [], [(root, 0)]
    while stack:
        aid, depth = stack.pop()
        order.append((aid, depth))
        stack.extend((c, depth + 1) for c in sorted(children[aid], reverse=True))

    res, tot_tool, tot_calls, tot_err = {}, collections.Counter(), collections.Counter(), collections.Counter()
    slow, gens, tot_batched = [], collections.Counter(), collections.Counter()
    print(f"{'agent':58s}{'span m':>7s}{'model m':>8s}{'tools m':>8s}{'wait m':>7s}{'turns':>6s}{'calls':>6s}{'err':>4s}{'out k':>6s}{'ctx k':>6s}")
    for aid, depth in order:
        p = os.path.join(d, f"agent-{aid}.jsonl")
        if not os.path.exists(p):
            continue
        a = analyse(p)
        res[aid] = a
        m = metas.get(aid, {})
        label = ("  " * depth + (m.get("agentType", "?").replace("labview-mcp:", "") + " - " + m.get("description", "")))[:57]
        print(f"{label:58s}{a['span']/60:7.1f}{a['model']/60:8.1f}{sum(a['tool'].values())/60:8.1f}{a['wait']/60:7.1f}"
              f"{a['turns']:6d}{sum(a['calls'].values()):6d}{sum(a['errors'].values()):4d}{a['out_tokens']/1e3:6.1f}{a['ctx_max']/1e3:6.0f}")
        tot_tool.update(a["tool"]); tot_calls.update(a["calls"]); tot_err.update(a["errors"])
        tot_batched.update(a["batched"])
        slow.extend((dt, n, aid[:6], i) for dt, n, i in a["slow"]); gens.update(a["gens"])

    if res:
        first = min(a["first"] for a in res.values()); last = max(a["last"] for a in res.values())
        model = sum(a["model"] for a in res.values()); tools = sum(tot_tool.values())
        print(f"\nwall clock {(last-first).total_seconds()/60:.1f} min | model {model/60:.1f} min | inside tools {tools/60:.1f} min "
              f"(solo calls only, summed over agents; parallel agents overlap) | turns {sum(a['turns'] for a in res.values())} "
              f"| tool calls {sum(tot_calls.values())} | output tokens {sum(a['out_tokens'] for a in res.values())/1e3:.0f}k")
    print(f"\n{'tool':44s}{'calls':>6s}{'batched':>8s}{'min':>7s}{'avg s':>7s}{'err':>5s}")
    for n, _ in sorted(tot_calls.items(), key=lambda kv: -tot_tool[kv[0]])[:30]:
        solo = tot_calls[n] - tot_batched[n]
        avg = f"{tot_tool[n] / solo:7.1f}" if solo else "      -"
        print(f"{n:44s}{tot_calls[n]:6d}{tot_batched[n]:8d}{tot_tool[n]/60:7.1f}{avg}{tot_err[n]:5d}")
    print("\nslowest single calls:")
    for dt, n, aid, i in sorted(slow, reverse=True)[:15]:
        print(f"  {dt:6.0f}s {n:30s} [{aid}] {i}")
    rep = {k: v for k, v in gens.items() if v > 1}
    if rep:
        print("\nregenerated (same target generated more than once):")
        for k, v in sorted(rep.items(), key=lambda kv: -kv[1]):
            print(f"  {v}x {k}")
    if "--json" in sys.argv:
        out = sys.argv[sys.argv.index("--json") + 1]
        json.dump({aid: dict(span=a["span"], model=a["model"], wait=a["wait"], turns=a["turns"],
                             out_tokens=a["out_tokens"], calls=a["calls"], tool=a["tool"], errors=a["errors"])
                   for aid, a in res.items()}, open(out, "w"), indent=1, default=str)


if __name__ == "__main__":
    main()
