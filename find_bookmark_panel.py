import json
import os

def find_nodes(node, pattern, path=""):
    matches = []
    
    # Check type name
    type_name = node.get("pythonObjectTypeName", "")
    if pattern.lower() in type_name.lower():
        matches.append((path + "/" + type_name, node.get("dictEntriesOfInterest", {}).get("_name")))

    # Check dict entries for text
    dict_entries = node.get("dictEntriesOfInterest", {})
    for k, v in dict_entries.items():
        if isinstance(v, str) and pattern.lower() in v.lower():
            matches.append((path + "/" + type_name + ":" + k, v))

    # Recurse
    children = node.get("children")
    if children:
        for i, child in enumerate(children):
            if child:
                matches.extend(find_nodes(child, pattern, path + "/" + type_name + f"[{i}]"))
                
    return matches

frames_dir = "logs/frames"
files = [f for f in os.listdir(frames_dir) if f.endswith(".json")]

patterns = ["Bookmark", "Location", "Places"]

for file in files[:3]: # check first 3 files
    print(f"--- Checking {file} ---")
    with open(os.path.join(frames_dir, file), 'r', encoding='utf-8') as f:
        try:
            data = json.load(f)
            for p in patterns:
                results = find_nodes(data, p)
                for r in results:
                    print(f"Match [{p}]: {r}")
        except Exception as e:
            print(f"Error reading {file}: {e}")
