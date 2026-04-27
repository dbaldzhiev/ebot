import json
import sys

with open(sys.argv[1], 'r', encoding='utf-8') as f:
    root = json.load(f)

def find_slots(node, path="root"):
    results = []
    name = node.get("dictEntriesOfInterest", {}).get("_name", "")
    if "Slot" in name:
        results.append((path, name, node.get("dictEntriesOfInterest", {})))
    
    children = node.get("children", [])
    if isinstance(children, list):
        for i, child in enumerate(children):
            results.extend(find_slots(child, f"{path}/children[{i}]"))
    return results

slots = find_slots(root)
print(f"{'Name':30} | {'Y':5} | {'Path'}")
for path, name, entries in slots:
    y = entries.get("_displayY", "?")
    print(f"{name:30} | {y:5} | {path}")
