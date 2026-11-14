# =============================================================================
# frames.py - the hidden structure frames of a VI in the order LabVIEW's Print VI
# To HTML writes their images (<base>d1.png .. dN.png), each with a caption, so
# lvai_render_diagrams can say which PNG shows which frame.
# =============================================================================
"""
Usage: python frames.py <vi> [<out.json>]

THE ORDER, measured 2026-11-14 on LabVIEW 2026 with two composed probes whose every frame carried a
subdiagram label naming it: <base>d.png is the top-level diagram with each structure's VISIBLE frame;
d1..dN are the other frames, depth first - the structures of a diagram in its nodeList order, each
structure's frames in frame order, a frame that is not the visible one written, and the structures
INSIDE every frame (visible or not) walked where that frame stands. Probe two: a case S1 (frames A
shown, B) holding a case S2 (p shown, q) in A, then a case S3 (m shown, n) -> d1 S2 q, d2 S1 B, d3 S3 n.
Probe three, a stacked sequence (0 shown, 1, 2) with a case in frame 1 -> d1 seq 1, d2 its case's
Default, d3 seq 2. Checked against renders of NI's examples the same day: Handling Mouse Events.vi
(event frames inside a While Loop), Conditional and Diagram Disable Structure.vi (the Diagram
Disable showing its frame 1) and Case Structure - Selector Data Types.vi (12 frames, a Boolean case
showing True) - every PNG showed the frame its caption names.

The walk and the visible frame (`dIdx`, hex like every heap index) come from the block diagram heap;
the frame NAMES from the AIXML reader (a CaseFrame's selector, an event frame's events, a Diagram's
label), so a frame the reader cannot name - or a VI it refuses - is captioned by its index instead.
"""
import json
import os
import re
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lvheap  # noqa: E402

KINDS = {
    "select": "Case Structure",
    "eventStruct": "Event Structure",
    "sequence": "Stacked Sequence",
    "commentNode": "Disable Structure",
}


def unescape_label(s):
    """The reader writes , : \\ and control characters as \\XX; show them as themselves."""
    return re.sub(r"\\([0-9A-Fa-f]{2})", lambda m: chr(int(m.group(1), 16)), s or "")


def frame_names(aixml_root):
    """heap frame uid -> its name as the reader exported it."""
    names = {}
    if aixml_root is None:
        return names
    for el in aixml_root.iter():
        if el.tag not in ("CaseFrame", "Diagram") or not el.get("uid"):
            continue
        sel = (el.get("selector") or "").strip()
        lab = " ".join(unescape_label(el.get("label") or "").split())
        if len(lab) > 60:
            lab = lab[:57] + "..."
        name = sel or lab
        if sel and lab:
            name = "%s (%s)" % (sel, lab)
        if name:
            names[el.get("uid")] = unescape_label(name)
    return names


def walk(diag, names, path, out, defs):
    """`defs`: uid -> the element that DEFINES it. pylabview writes an object in full where it first
    appears - in a diagram's zPlaneList as often as in its nodeList - and a bare uid elsewhere, so a
    nodeList entry may be a reference (NI's Handling Mouse Events.vi: the While Loop holding the
    Event Structure is defined under zPlaneList)."""
    nodes = diag.find("nodeList")
    for item in (nodes if nodes is not None else []):
        node = item if item.get("class") else defs.get(item.get("uid"), item)
        frames_el = node.find("diagramList")
        if frames_el is None:
            continue
        frames = list(frames_el)
        cls = node.get("class") or ""
        kind = KINDS.get(cls)
        try:
            shown = int(node.findtext("dIdx") or "0", 16)
        except ValueError:
            shown = 0
        for i, frame in enumerate(frames):
            frame = frame if frame.find("nodeList") is not None or frame.get("class") else defs.get(frame.get("uid"), frame)
            here = path
            if kind and len(frames) > 1:
                name = names.get(frame.get("uid")) or "frame %d of %d" % (i, len(frames))
                here = path + ["%s %s" % (kind, name)]
                if i != shown:
                    out.append({"caption": " > ".join(here), "structure": kind, "frame": name,
                                "frameIndex": i, "frameCount": len(frames), "visibleFrameIndex": shown,
                                "uid": frame.get("uid")})
            walk(frame, names, here, out, defs)


def frames_of(vi_path):
    tmp = tempfile.mkdtemp(prefix="frames_")
    try:
        main_xml = lvheap.extract(vi_path, tmp)
        heaps = [f for f in os.listdir(tmp) if f.endswith("_BDHb.xml")]
        if not heaps:
            return {"frames": [], "problem": "no block diagram heap in the file"}
        heap = ET.parse(os.path.join(tmp, heaps[0])).getroot()
        root = heap.find("root")
        defs = {el.get("uid"): el for el in heap.iter() if el.get("class") and el.get("uid")}
        aixml, reader_error = None, None
        try:
            import vi2aixml
            aixml = vi2aixml.Reader(main_xml, vi_path=os.path.abspath(vi_path)).read()
        except BaseException as e:  # the reader refuses some VIs; the walk does not need it
            reader_error = "%s: %s" % (type(e).__name__, str(e).splitlines()[0] if str(e) else "")
        out = []
        walk(root, frame_names(aixml), [], out, defs)
        for n, f in enumerate(out, 1):
            f["image"] = "d%d" % n
        answer = {"frames": out, "namedBy": "reader" if aixml is not None else "index"}
        if reader_error:
            answer["readerError"] = reader_error
        return answer
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main(argv):
    answer = frames_of(argv[0])
    text = json.dumps(answer, ensure_ascii=False)
    if len(argv) > 1:
        open(argv[1], "w", encoding="utf-8").write(text)
    else:
        print(text)


if __name__ == "__main__":
    main(sys.argv[1:])
