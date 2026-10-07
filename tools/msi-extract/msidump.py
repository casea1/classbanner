import olefile, sys, struct, os
def dec(name):
    out=[]
    for ch in name:
        c=ord(ch)
        if 0x3800<=c<0x4800:
            c-=0x3800
            tbl="0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._"
            out.append(tbl[c&0x3f]+tbl[(c>>6)&0x3f])
        elif 0x4800<=c<0x4840:
            tbl="0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz._"
            out.append(tbl[c-0x4800])
        elif c==0x4840: out.append('!')
        else: out.append(ch)
    return ''.join(out)
o=olefile.OleFileIO(sys.argv[1]); outdir=sys.argv[2]
for e in o.listdir():
    n=e[-1]; d=dec(n); data=o.openstream(e).read()
    print(repr(d), len(data))
    safe=d.replace('!','_T_').replace('\x05','_')
    open(os.path.join(outdir,safe),'wb').write(data)
