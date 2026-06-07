#!/usr/bin/env python3
"""
TLOU2 geometry decoder + multi-part actor merger (Python reference for
NdPakMeshParser.cs). Decodes GEOMETRY_1 submeshes (quantised continuous
bitstream positions + UV1), merges all groups in a pak, and can merge across
an actor's part-paks (head+body+arms+...) into ONE OBJ with per-group materials.

Deps: numpy, pillow, texture2ddecoder
"""
import struct, os, sys, glob
import numpy as np

def u16(d,o): return struct.unpack_from("<H",d,o)[0]
def u32(d,o): return struct.unpack_from("<I",d,o)[0]
def i64(d,o): return struct.unpack_from("<q",d,o)[0]
def f32(d,o): return struct.unpack_from("<f",d,o)[0]
def cstr(d,o):
    e=o
    while e<len(d) and d[e]!=0: e+=1
    return d[o:e].decode("latin-1","replace")

SMD_STRIDE=192  # TLOU2 stride (NdPakMeshParser.cs uses 176 = BUG; verified 192 by contiguous-SMD scan)

def read_pak(path):
    d=open(path,"rb").read()
    magic=u32(d,0)
    assert magic in (0xA79,0x10A79,0x80000A79,0xA7D), f"bad magic 0x{magic:X}"
    pageCt=u32(d,0x10); ptOff=u32(d,0x14); fixupOff=u32(d,0x1C)
    loginIdx=u32(d,0x08); loginOff=u32(d,0x0C)
    pages=[(u32(d,ptOff+i*12),u32(d,ptOff+i*12+4)) for i in range(pageCt)]
    fixDataOff=u32(d,fixupOff+4); fixCount=u32(d,fixupOff+8)
    if fixDataOff<=0: fixDataOff=fixupOff
    fixups={}
    for i in range(fixCount):
        fo=fixDataOff+i*8
        if fo+8>len(d): break
        src=u16(d,fo); dst=u16(d,fo+2); poff=u32(d,fo+4)
        if src<pageCt and dst<pageCt:
            fixups[pages[src][0]+poff]=dst
    isT = (loginIdx<pageCt and u32(d, pages[loginIdx][0]+loginOff+32)==74565)
    return dict(d=d,pages=pages,pageCt=pageCt,fixups=fixups,isT=isT)

def resolve(pk,addr):
    """resolve 64-bit pointer at addr -> absolute offset, or None."""
    d=pk["d"]
    if addr+8>len(d): return None
    lo=i64(d,addr)
    dst=pk["fixups"].get(addr)
    if dst is None: return None
    return pk["pages"][dst][0]+lo

def read_bits_fields(d, base, n, sizes):
    """Vectorized LSB-first continuous bitstream. sizes=list of bit widths per
    field per vertex. Returns list of np arrays (one per field), each len n."""
    per=sum(sizes); total=per*n
    nbytes=(total+7)//8 + 1
    raw=np.frombuffer(d, dtype=np.uint8, count=nbytes, offset=base)
    bits=np.unpackbits(raw, bitorder="little")[:total].reshape(n, per)
    out=[]; off=0
    for sz in sizes:
        if sz==0:
            out.append(np.zeros(n,dtype=np.float64)); continue
        field=bits[:, off:off+sz].astype(np.uint64)
        weights=(np.uint64(1)<<np.arange(sz,dtype=np.uint64))
        out.append((field*weights).sum(axis=1).astype(np.float64))
        off+=sz
    return out

def decode_pak_groups(path, want_lod=0):
    pk=read_pak(path); d=pk["d"]; pages=pk["pages"]; pageCt=pk["pageCt"]; fx=pk["fixups"]
    padSz=48 if pk["isT"] else 32
    # find GEOMETRY_1
    geoStart=geoOff=-1
    for (fo,sz) in pages:
        start=fo
        if start+20>len(d): continue
        nEnt=u16(d,start+18); cur=start+20
        for _ in range(nEnt):
            if cur+16>len(d): break
            riOff=u32(d,cur+8); cur+=16
            rb=start+riOff
            if rb+16>len(d): continue
            tp=i64(d,rb+8)
            if tp<=0 or start+tp>=len(d): continue
            if cstr(d,start+tp)=="GEOMETRY_1":
                geoStart,geoOff=start,riOff; break
        if geoOff>=0: break
    if geoOff<0: return []
    ghOff=geoStart+geoOff+padSz
    numSMD=u32(d,ghOff+8)
    smBase=resolve(pk, ghOff+40)
    if smBase is None or not(0<numSMD<=256): return []
    groups=[]
    for i in range(numSMD):
        sd=smBase+SMD_STRIDE*i
        if sd+0x94>len(d): continue
        nV=u32(d,sd+0x88); nI=u32(d,sd+0x8C); nSS=u32(d,sd+0x90)
        if not(3<=nV<=500000) or not(3<=nI<=3000000) or nI%3 or not(1<=nSS<=32): continue
        ixAbs=resolve(pk, sd+0x40)
        if ixAbs is None or ixAbs+nI*2>len(d): continue
        idx=np.frombuffer(d,dtype=np.uint16,count=nI,offset=ixAbs).astype(np.int64)
        if idx[:min(16,nI)].max()>=nV: continue
        sdBase=resolve(pk, sd+0x30)
        if sdBase is None: continue
        # find pos (ctype64 or j0/stride12) + uv (ctype65, fallback 75)
        posJ=uvJ=uvFb=-1
        for j in range(nSS):
            sat=sdBase+64*j
            if sat+0x40>len(d): break
            if sat not in fx: continue
            ct=d[sat+0x14]; strd=(d[sat+0x16]>>4)&0xF
            if posJ<0 and ((j==0 and strd==12) or ct==64): posJ=j
            elif uvJ<0 and ct==65: uvJ=j
            elif uvFb<0 and ct==75: uvFb=j
        if uvJ<0: uvJ=uvFb
        if posJ<0: continue
        # position
        pso=sdBase+64*posJ; pbuf=resolve(pk,pso)
        strd=(d[pso+0x16]>>4)&0xF
        posF32=(posJ==0 and strd==12)
        if posF32:
            if pbuf+nV*12>len(d): continue
            P=np.frombuffer(d,dtype=np.float32,count=nV*3,offset=pbuf).reshape(nV,3).astype(np.float64)
        else:
            sz=[d[pso+0x18],d[pso+0x19],d[pso+0x1A],d[pso+0x1B]]
            qs=[f32(d,pso+0x20),f32(d,pso+0x24),f32(d,pso+0x28)]
            qo=[f32(d,pso+0x30),f32(d,pso+0x34),f32(d,pso+0x38)]
            if sz[0]==0 and sz[1]==0 and sz[2]==0: continue
            fld=read_bits_fields(d,pbuf,nV,sz)
            P=np.stack([fld[0]*qs[0]+qo[0], fld[1]*qs[1]+qo[1], fld[2]*qs[2]+qo[2]],axis=1)
        # uv
        UV=None
        if uvJ>=0:
            uo=sdBase+64*uvJ
            if uo in fx:
                ubuf=resolve(pk,uo)
                us=[d[uo+0x18],d[uo+0x19]]
                ucs=[f32(d,uo+0x20),f32(d,uo+0x24)]; uof=[f32(d,uo+0x30),f32(d,uo+0x34)]
                if us[0]>0 and us[1]>0 and ubuf+((us[0]+us[1])*nV+7)//8<=len(d):
                    uf=read_bits_fields(d,ubuf,nV,us)
                    UV=np.stack([uf[0]*ucs[0]+uof[0], uf[1]*ucs[1]+uof[1]],axis=1)
        # name + lod
        nm=""; lod=0
        nAbs=resolve(pk, sd+0x20)
        if nAbs is not None:
            full=cstr(d,nAbs); nm=full.split("|")[-1]
            sp=nm.find("Shape")
            if sp>=0 and sp+5<len(nm) and nm[sp+5].isdigit(): lod=int(nm[sp+5])
        groups.append(dict(name=nm or f"Shape{i}", lod=lod, V=P, UV=UV,
                           F=idx.reshape(-1,3), nV=nV, nTri=nI//3))
    if want_lod is not None:
        groups=[g for g in groups if g["lod"]==want_lod] or groups
    return groups

def write_obj(parts, obj_path, mtl_name=None):
    """parts: list of dict(name, groups[list], material). Writes one merged OBJ."""
    with open(obj_path,"w") as f:
        if mtl_name: f.write(f"mtllib {mtl_name}\n")
        voff=0
        for part in parts:
            for g in part["groups"]:
                V=g["V"]; UV=g["UV"]; F=g["F"]
                f.write(f"g {part['name']}_{g['name']}\n")
                if part.get("material"): f.write(f"usemtl {part['material']}\n")
                for v in V: f.write(f"v {v[0]:.6f} {v[1]:.6f} {v[2]:.6f}\n")
                if UV is not None:
                    for t in UV: f.write(f"vt {t[0]:.6f} {t[1]:.6f}\n")
                for tri in F:
                    a,b,c=tri+1+voff
                    if UV is not None:
                        ua,ub,uc=tri+1+voff
                        f.write(f"f {a}/{ua} {b}/{ub} {c}/{uc}\n")
                    else:
                        f.write(f"f {a} {b} {c}\n")
                voff+=len(V)
    return voff

if __name__=="__main__":
    base="/sessions/amazing-quirky-heisenberg/mnt/Game Assets/TLOU2/common_unpacked/actor97"
    paks=sys.argv[1:] or [f"{base}/ellie-body.pak"]
    allparts=[]
    for p in paks:
        gs=decode_pak_groups(p, want_lod=0)
        name=os.path.basename(p).replace(".pak","")
        tV=sum(g["nV"] for g in gs); tT=sum(g["nTri"] for g in gs)
        if gs:
            allV=np.vstack([g["V"] for g in gs])
            mn=allV.min(0); mx=allV.max(0)
            uvr="none"
            uvs=[g["UV"] for g in gs if g["UV"] is not None]
            if uvs:
                uu=np.vstack(uvs); uvr=f"U[{uu[:,0].min():.2f},{uu[:,0].max():.2f}] V[{uu[:,1].min():.2f},{uu[:,1].max():.2f}]"
            print(f"{name}: groups={len(gs)} verts={tV} tris={tT}")
            print(f"   bounds min=({mn[0]:.3f},{mn[1]:.3f},{mn[2]:.3f}) max=({mx[0]:.3f},{mx[1]:.3f},{mx[2]:.3f})  UV {uvr}")
        allparts.append(dict(name=name,groups=gs,material=name))
    out="/tmp/ellie_merged.obj"
    nv=write_obj(allparts,out,"ellie.mtl")
    print(f"-> wrote {out}  total verts={nv}")
