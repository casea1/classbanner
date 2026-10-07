import struct, zlib, sys, os
d=open(sys.argv[1],'rb').read(); out=sys.argv[2]
fo,=struct.unpack('<I',d[16:20]); nf,nfi,fl=struct.unpack('<HHH',d[26:32])
o=36; folders=[]
for i in range(nf):
    folders.append(struct.unpack('<IHH',d[o:o+8])); o+=8
data=[]
for start,nblk,comp in folders:
    p=start; buf=b''; zd=None; prev=b''
    for b in range(nblk):
        csum,cb,ub=struct.unpack('<IHH',d[p:p+8]); blk=d[p+8:p+8+cb]; p+=8+cb
        assert blk[:2]==b'CK'
        z=zlib.decompressobj(-15, zdict=prev[-32768:]) if prev else zlib.decompressobj(-15)
        u=z.decompress(blk[2:]); buf+=u; prev=buf[-32768:]
    data.append(buf)
o=fo
for i in range(nfi):
    sz,off,fi,dt,tm,at=struct.unpack('<IIHHHH',d[o:o+16]); o+=16
    e=d.index(b'\0',o); name=d[o:e].decode('cp1252'); o=e+1
    safe=os.path.basename(name.replace('\\','/'))
    open(os.path.join(out,safe),'wb').write(data[fi][off:off+sz]); print(name,sz)
