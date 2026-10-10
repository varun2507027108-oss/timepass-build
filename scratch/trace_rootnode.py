import json

with open('Assets/Artwork/SourceModels/PanzerIV/manifest.json') as f:
    d = json.load(f)

nodes = {n['name']: n for n in d['nodes']}

cur = 'RootNode'
chain = []
while cur:
    n = nodes.get(cur)
    if not n:
        break
    chain.append((n['name'], n.get('parent'), n.get('pos'), n.get('rot'), n.get('scl')))
    cur = n.get('parent')

print("Chain from RootNode up to root:")
for item in chain:
    print(item)

print("\nChildren of RootNode:")
rootnode_children = [n['name'] for n in d['nodes'] if n.get('parent') == 'RootNode']
print(f"Total children of RootNode: {len(rootnode_children)}")
for c in rootnode_children[:15]:
    print(" -", c)
