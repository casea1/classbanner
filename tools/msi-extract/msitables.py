import struct, sys, os
d=sys.argv[1]
rd=lambda n: open(os.path.join(d,n),'rb').read()
pool=rd('_T__StringPool'); data=rd('_T__StringData')
cp,=struct.unpack('<I',pool[:4]); refsz=3 if cp&0x80000000 else 2
ents=[struct.unpack('<HH',pool[i:i+4]) for i in range(4,len(pool),4)]
strs=[None]; off=0; i=0
while i<len(ents):
    l,r=ents[i]
    if l==0 and r!=0:
        l=ents[i+1][0]+(ents[i+1][1]<<16); i+=1; strs.append(None)
    s=data[off:off+l].decode('cp1252','replace'); off+=l
    strs.append(s); i+=1
# fix: long entries consume two ids; keep simple
def colw(t):
    if t&0x0800 or (t&0x0f00)==0x0900: return refsz if t&0x0800 else 2
    return 4 if (t&0xff)==4 else 2
def parse(raw, cols):
    w=sum(colw(t) for _,t in cols); n=len(raw)//w if w else 0
    rows=[[None]*len(cols) for _ in range(n)]; p=0
    for ci,(_,t) in enumerate(cols):
        cw=colw(t)
        for r in range(n):
            b=raw[p:p+cw]; p+=cw
            v=int.from_bytes(b,'little')
            if t&0x0800: rows[r][ci]=strs[v] if v<len(strs) else f'<s{v}>'
            elif (t&0x0f00)==0x0900: rows[r][ci]='<stream>'
            else:
                if v==0: rows[r][ci]=None
                else: rows[r][ci]=v-(0x80000000 if cw==4 else 0x8000)
    return rows
ccols=[('Table',0x0800|0x2000),('Number',0x2502),('Name',0x0800),('Type',0x0502)]
crow=parse(rd('_T__Columns'),ccols)
tables={}
for t,num,name,typ in crow: tables.setdefault(t,[]).append((num,name,typ))
want=sys.argv[2:] or sorted(tables)
for t in want:
    cols=[(n,ty) for _,n,ty in sorted(tables[t])]
    fn='_T_'+t
    if not os.path.exists(os.path.join(d,fn)): continue
    print(f'=== {t} ({", ".join(c for c,_ in cols)})')
    for r in parse(rd(fn),cols): print('  |'.join('' if x is None else str(x) for x in r))
