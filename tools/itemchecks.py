import os, re, sys
# Lists every place the game checks which freebie is in effect (ISIL dump): IsTargetItemInEffect / GetItemMultiplier
# calls with their item constants, and direct compares of MainGameManager's effect item (+876 = 0x36C).
root = sys.argv[1]
NAMES = {1:'Drink',2:'Magnet',3:'Pickel',4:'Sonar',5:'Timer',6:'Tarai',7:'Spider',8:'Bubble',9:'Mushroom',10:'Hand',11:'Hoop',12:'Smoke',
         13:'Shoes',14:'Crown',15:'Dice',16:'SP_Drink',17:'SP_Magnet',18:'SP_Pickel',19:'SP_Sonar',20:'SP_Timer',21:'SP_Tarai',22:'SP_Spider',
         23:'SP_Bubble',24:'SP_Mushroom',25:'SP_Crown',26:'SP_Dice',27:'Sunglass',28:'RandomItem'}
def nm(v):
    return NAMES.get(v, str(v))
line_re = re.compile(r'^\s*(\d+)\s+(.*)$')
for dp, dn, fn in os.walk(root):
    for f in fn:
        if not f.endswith('.txt'): continue
        path = os.path.join(dp, f)
        try: lines = open(path, encoding='utf-8', errors='replace').read().split('\n')
        except Exception: continue
        method = None; isil = False; body = []
        def flush():
            if not method or not body: return
            for i, ins in enumerate(body):
                kind = None
                if 'Call MainGameManager.IsTargetItemInEffect' in ins: kind = 'IsTargetItemInEffect'
                elif 'Call MainGameManager.GetItemMultiplier' in ins: kind = 'GetItemMultiplier'
                if kind:
                    vals = []; arr = []
                    for j in range(i - 1, max(-1, i - 40), -1):
                        b = body[j]
                        m = re.search(r'Move \[r\w+\+(\d+)\], (\d+)$', b)
                        if m and 32 <= int(m.group(1)) <= 32 + 4 * 30 and 'eInstage' not in b:
                            arr.append((int(m.group(1)), int(m.group(2))))
                        m2 = re.search(r'Move rdx, (\d+)$', b)
                        if m2 and not vals and not arr:
                            vals.append(int(m2.group(1))); break
                        if 'Call ' in b and 'SzArrayNew' not in b and arr: break
                    items = [nm(v) for _, v in sorted(arr)] if arr else [nm(v) for v in vals]
                    print(f'{f[:-4]} :: {method} :: {kind} :: {items}')
                m = re.search(r'Compare \[r\w+\+876\], (\d+)', ins)
                if m:
                    print(f'{f[:-4]} :: {method} :: effect item == :: [{nm(int(m.group(1)))}]')
        for ln in lines:
            if ln.startswith('Method:'):
                flush(); method = ln[8:].strip(); isil = False; body = []
            elif ln.startswith('ISIL:'):
                isil = True
            elif isil:
                m = line_re.match(ln)
                if m: body.append(m.group(2).strip())
        flush()
