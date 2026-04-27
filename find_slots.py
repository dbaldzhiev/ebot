import json
import sys

with open(sys.argv[1], 'r', encoding='utf-8') as f:
    root = json.load(f)

def find_slots(node, path="root"):
    results = []
    name = node.get("dictEntriesOfInterest", {}).get("_name", "")
    if "Slot" in name:
        results.append((path, name))
    
    children = node.get("children", [])
    if isinstance(children, list):
        for i, child in enumerate(children):
            results.extend(find_slots(child, f"{path}/children[{i}]"))
    return results

slots = find_slots(root)
for path, name in slots:
    print(f"{name:40} | {path}")
