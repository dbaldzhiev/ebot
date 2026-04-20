import json
import sys
import argparse

def find_nodes(node, predicate, path=""):
    """Recursively finds nodes matching a predicate."""
    results = []
    
    # Check if current node matches
    if predicate(node):
        results.append((path, node))
    
    # Recurse into children
    children = node.get("children")
    if children and isinstance(children, list):
        for i, child in enumerate(children):
            child_path = f"{path}/children[{i}]"
            results.extend(find_nodes(child, predicate, child_path))
            
    return results

def get_node_text(node):
    """Extracts all display text from a node and its immediate children."""
    texts = []
    # Check dictEntriesOfInterest for _text
    entries = node.get("dictEntriesOfInterest", {})
    if "_text" in entries:
        texts.append(str(entries["_text"]))
    if "_name" in entries:
        texts.append(str(entries["_name"]))
        
    # Check immediate children for labels
    children = node.get("children")
    if children and isinstance(children, list):
        for child in children:
            c_entries = child.get("dictEntriesOfInterest", {})
            if "_text" in c_entries:
                texts.append(str(c_entries["_text"]))
                
    return " | ".join(texts)

def summarize_node(path, node):
    """Returns a one-line summary of a node."""
    type_name = node.get("pythonObjectTypeName", "Unknown")
    address = node.get("pythonObjectAddress", "0x0")
    text = get_node_text(node)
    region = node.get("dictEntriesOfInterest", {})
    x = region.get("_displayX", "?")
    y = region.get("_displayY", "?")
    w = region.get("_displayWidth", "?")
    h = region.get("_displayHeight", "?")
    
    return f"[{address}] {type_name:20} | Path: {path} | Pos: ({x},{y}) {w}x{h} | Text: {text}"

def main():
    parser = argparse.ArgumentParser(description="EBot UI Frame Explorer")
    parser.add_argument("file", help="Path to the JSON frame file")
    parser.add_argument("--search", help="Search for text in nodes")
    parser.add_argument("--type", help="Search for pythonObjectTypeName")
    parser.add_argument("--address", help="Find a specific object address")
    parser.add_argument("--dump", action="store_true", help="Dump full JSON of found nodes")

    args = parser.parse_args()

    try:
        with open(args.file, 'r', encoding='utf-8') as f:
            root = json.load(f)
    except Exception as e:
        print(f"Error loading JSON: {e}")
        return

    predicate = lambda n: False
    if args.search:
        search_lower = args.search.lower()
        predicate = lambda n: search_lower in get_node_text(n).lower()
    elif args.type:
        predicate = lambda n: args.type.lower() in n.get("pythonObjectTypeName", "").lower()
    elif args.address:
        predicate = lambda n: n.get("pythonObjectAddress") == args.address
    else:
        print("Please specify --search, --type, or --address")
        return

    matches = find_nodes(root, predicate, "root")
    
    print(f"Found {len(matches)} matches:")
    for path, node in matches:
        if args.dump:
            print(json.dumps(node, indent=2))
        else:
            print(summarize_node(path, node))

if __name__ == "__main__":
    main()
