import struct, sys, os
from collections import Counter
def u16(d,o): return struct.unpack_from("<H", d, o)[0]
def u32(d,o): return struct.unpack_from("<I", d, o)[0]
def u64(d,o): return struct.unpack_from("<Q", d, o)[0]
IMG_FORMAT={98:"BC7",71:"BC1",80:"BC4",83:"BC5",99:"BC7_SRGB?",95:"BC6H?"}
def cstr(d,o):
    if o<0 or o>=len(d): return ""
    e=o
    while e<len(d) and d[e]!=0: e+=1
    try: return d[o:e].decode("ascii")
    except: return d[o:e].decode("latin-1","replace")
def parse(path):
    d=open(path,"rb").read()
    print(f"\n=== {os.path.basename(path)}  ({len(d):,} bytes) ===")
    if len(d)<0x20: print("  too small"); return
    magic=u32(d,0); ok={2681,2685,68217,68221,2147486329}
    print(f"  magic={magic} (0x{magic:X}) {'OK' if magic in ok else 'BAD'}")
    loginIdx=u32(d,8); loginOff=u32(d,0x0C); pageCt=u32(d,0x10); pPageTab=u32(d,0x14); fixupOff=u32(d,0x1C)
    print(f"  pageCt={pageCt} pageTabOff=0x{pPageTab:X}")
    pages=[]
    for i in range(pageCt):
        o=pPageTab+i*12
        if o+12>len(d): break
        pages.append((u32(d,o),u32(d,o+4),u32(d,o+8)))
    print(f"  pages={len(pages)}")
    fixDataOff=u32(d,fixupOff+4); numFix=u32(d,fixupOff+8)
    print(f"  fixups={numFix}")
    game="Unknown"
    if loginIdx<len(pages):
        ls=pages[loginIdx][0]+loginOff
        if ls+36<=len(d) and u32(d,ls+32)==74565: game="TLOU2"
        elif magic in (2685,68221): game="TLOUP1"
        else: game="U4/TLL"
    print(f"  game={game}")
    isT=game=="TLOU2"
    types=Counter(); geo=None; joint=None; vrams=[]
    for p,(fo,sz,fl) in enumerate(pages):
        start=fo
        if start+20>len(d): continue
        numPH=u16(d,start+18); cur=start+20
        for _ in range(numPH):
            if cur+16>len(d): break
            resItemOff=u32(d,cur+8); cur+=16
            itemAbs=resItemOff+start
            if itemAbs+16>len(d): continue
            inO=u64(d,itemAbs); itO=u64(d,itemAbs+8)
            iname=cstr(d,start+inO); itype=cstr(d,start+itO)
            types[itype]+=1
            if itype=="GEOMETRY_1" and geo is None: geo=(p,resItemOff,start,iname)
            if itype=="JOINT_HIERARCHY" and joint is None: joint=(p,resItemOff,start,iname)
            if itype=="VRAM_DESC": vrams.append((p,resItemOff,start,iname))
    print(f"  types={dict(types.most_common(12))}")
    print(f"  GEOMETRY_1={geo is not None} JOINT={joint is not None} VRAM_DESC={len(vrams)}")
    if geo:
        p,rio,start,iname=geo; riBase=start+rio; pad=48 if isT else 32; cA=riBase+pad+8
        if cA+4<=len(d): print(f"  geoName='{iname}' numSubmeshDesc={u32(d,cA)}")
    print("  --- VRAM_DESC ---")
    for i,(p,rio,start,iname) in enumerate(vrams[:8]):
        db=start+rio
        if isT: db+=16
        if db+120>len(d): print(f"    [{i}] OOB"); continue
        th=u64(d,db+56); po=u32(d,db+40); vs=u32(d,db+48); imf=u32(d,db+72)
        mip=u32(d,db+80); w=u32(d,db+84); h=u32(d,db+88); tp=cstr(d,db+112)
        fmt=IMG_FORMAT.get(imf,f"?{imf}")
        print(f"    [{i}] {w}x{h} mips={mip} fmt={imf}({fmt}) size={vs} pakOff={po} hash=0x{th:X}")
        if tp and all(32<=ord(c)<127 for c in tp[:30]): print(f"         texPath='{tp[:80]}'")
    if pages:
        last=pages[-1]; b=last[0]+last[1]
        print(f"  after-pages boundary=0x{b:X} ({b}) fileEnd=0x{len(d):X} trailing={len(d)-b:,}")
base="/sessions/amazing-quirky-heisenberg/mnt/Game Assets/TLOU2/common_unpacked/actor97"
targets=sys.argv[1:] or [f"{base}/ellie-head.pak",f"{base}/ellie-body.pak",f"{base}/ellie-arms.pak"]
for t in targets:
    try: parse(t)
    except Exception as e: print(f"  ERROR {t}: {e}")
