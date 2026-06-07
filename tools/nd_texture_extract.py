#!/usr/bin/env python3
"""
Working TLOU2 texture extractor — Python reference for NdTextureDictionary.cs.

Pipeline (verified end-to-end against real data):
  1. actor .pak  -> VRAM_DESC -> texPath -> hash (filename token)
  2. texturedict3/common-dict*.pak -> scan structural pages -> hash index
  3. seek to (lastPageEnd + pakOffset), read BC blocks, decode -> PNG

Embedded actor-pak textures are only 64px GPU-tiled thumbnails; the dict holds
the full-res LINEAR copies, so this resolves real full-resolution textures.

Deps: pip install texture2ddecoder pillow
"""
import struct, os, sys, glob
import texture2ddecoder as t2d
from PIL import Image

def u16(d,o): return struct.unpack_from("<H",d,o)[0]
def u32(d,o): return struct.unpack_from("<I",d,o)[0]
def u64(d,o): return struct.unpack_from("<Q",d,o)[0]
def cstr(d,o):
    e=o
    while e<len(d) and d[e]!=0: e+=1
    return d[o:e].decode("latin-1","replace")

IMG_FMT={71:"BC1",80:"BC4",83:"BC5",98:"BC7"}
DECODE={"BC1":t2d.decode_bc1,"BC4":t2d.decode_bc4,"BC5":t2d.decode_bc5,"BC7":t2d.decode_bc7}

def hash_of(texpath):
    return os.path.splitext(os.path.basename(texpath))[0]

# ── actor pak: list textures (hash, fmt, w, h, texpath, role) ──────────────
def actor_textures(pak):
    d=open(pak,"rb").read()
    pageCt=u32(d,0x10); pPT=u32(d,0x14)
    pages=[u32(d,pPT+i*12) for i in range(pageCt)]
    out=[]
    for start in pages:
        if start+20>len(d): continue
        n=u16(d,start+18); cur=start+20
        for _ in range(n):
            if cur+16>len(d): break
            rio=u32(d,cur+8); cur+=16
            ia=rio+start
            if ia+16>len(d): continue
            if cstr(d,start+u64(d,ia+8))!="VRAM_DESC": continue
            db=start+rio+16
            if db+120>len(d): continue
            imf=u32(d,db+72); w=u32(d,db+84); h=u32(d,db+88); tp=cstr(d,db+112)
            fmt=IMG_FMT.get(imf)
            if not fmt: continue
            low=tp.lower()
            role=("diffuse" if "-color" in low or "_color" in low or "albedo" in low
                  else "normal" if "normal" in low
                  else "ao" if "-ao" in low or "nao" in low
                  else "other")
            out.append(dict(hash=hash_of(tp),fmt=fmt,w=w,h=h,texpath=tp,role=role))
    return out

# ── dict pak: build hash -> entry index (structural scan only) ─────────────
def build_dict_index(dict_paks):
    idx={}
    PRIO={"BC7":4,"BC5":3,"BC4":2,"BC1":1}
    for pak in dict_paks:
        f=open(pak,"rb"); hdr=f.read(0x20)
        magic=u32(hdr,0)
        if magic not in (0xA79,0x10A79,0xA7D,0x80000A79):
            f.close(); continue
        pageCt=u32(hdr,0x10); ptOff=u32(hdr,0x14); fixOff=u32(hdr,0x1C)
        f.seek(ptOff); pt=f.read(pageCt*12)
        offs=[u32(pt,i*12) for i in range(pageCt)]; szs=[u32(pt,i*12+4) for i in range(pageCt)]
        lastEnd=offs[-1]+szs[-1]
        f.seek(0); data=f.read(lastEnd)            # structural region only
        for p in range(pageCt):
            start=offs[p]
            if start+20>len(data): continue
            n=u16(data,start+18); cur=start+20
            for _ in range(n):
                if cur+16>len(data): break
                rio=u32(data,cur+8); cur+=16
                rb=start+rio
                if rb+16>len(data): continue
                tptr=u64(data,rb+8)
                if tptr<=0 or start+tptr>=len(data): continue
                if cstr(data,start+tptr)!="VRAM_DESC": continue
                vb=start+rio+16
                if vb+120>len(data): continue
                pakOff=u32(data,vb+40); vsz=u32(data,vb+48); imf=u32(data,vb+72)
                w=u32(data,vb+84); h=u32(data,vb+88); tp=cstr(data,vb+112)
                fmt=IMG_FMT.get(imf)
                if not fmt or not (4<=w<=8192 and 4<=h<=8192) or not (16<=vsz<=50_000_000): continue
                hh=hash_of(tp)
                if not hh: continue
                cand=dict(file=pak,offset=lastEnd+pakOff,size=vsz,w=w,h=h,fmt=fmt)
                if hh not in idx or PRIO[fmt]>PRIO[idx[hh]["fmt"]]:
                    idx[hh]=cand
        f.close()
    return idx

def decode_to_png(entry, out_png):
    with open(entry["file"],"rb") as f:
        f.seek(entry["offset"]); raw=f.read(entry["size"])
    w,h,fmt=entry["w"],entry["h"],entry["fmt"]
    bgra=DECODE[fmt](raw,w,h)               # returns BGRA bytes
    img=Image.frombytes("RGBA",(w,h),bgra,"raw","BGRA")
    if fmt=="BC5":                          # 2-channel normal: B from RG, fill blue
        r,g,_,_=img.split(); from_=Image.merge("RGB",(r,g,g))
        from_.save(out_png)
    else:
        img.convert("RGB" if fmt in("BC1","BC7") else "RGBA").save(out_png)
    return w,h,fmt

if __name__=="__main__":
    base="/sessions/amazing-quirky-heisenberg/mnt/Game Assets/TLOU2/common_unpacked"
    actor=sys.argv[1] if len(sys.argv)>1 else f"{base}/actor97/ellie-body.pak"
    outdir=sys.argv[2] if len(sys.argv)>2 else "/tmp/tex_out"
    os.makedirs(outdir,exist_ok=True)

    print(f"actor pak: {os.path.basename(actor)}")
    texs=actor_textures(actor)
    diff=[x for x in texs if x["role"]=="diffuse"]
    print(f"  textures={len(texs)}  diffuse={len(diff)}  normal={sum(x['role']=='normal' for x in texs)}")

    print("building dict index from texturedict3 ...")
    dpaks=sorted(glob.glob(f"{base}/texturedict3/*.pak"))
    idx=build_dict_index(dpaks)
    print(f"  dict entries indexed = {len(idx):,}")

    targets = diff[:3] if diff else texs[:3]
    for tx in targets:
        e=idx.get(tx["hash"])
        name=os.path.basename(tx["texpath"]).split(".tga")[0]
        if not e:
            print(f"  [miss] {tx['role']:7} {tx['hash']} (thumb {tx['w']}x{tx['h']} {tx['fmt']}) -> not in dict")
            continue
        out=os.path.join(outdir,f"{name}_{tx['role']}.png")
        w,h,fmt=decode_to_png(e,out)
        print(f"  [ OK ] {tx['role']:7} {tx['hash']} thumb {tx['w']}x{tx['h']} -> FULL {w}x{h} {fmt} -> {os.path.basename(out)}")
