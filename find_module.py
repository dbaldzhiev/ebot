import json
import sys

with open(sys.argv[1], 'r', encoding='utf-8') as f:
    root = json.load(f)

def find_by_typeid(node, target_id):
    name = node.get("dictEntriesOfInterest", {}).get("_name", "")
    if f"_{target_id}" in name:
        return node
    
    children = node.get("children", [])
    if isinstance(children, list):
        for child in children:
            res = find_by_typeid(child, target_id)
            if res: return res
    return None

node = find_by_typeid(root, 2281)
if node:
    print(json.dumps(node.get("dictEntriesOfInterest", {}), indent=2))
else:
    print("Not found")
