# Traces a black-on-white logo image into a filled, extruded 2D curve.
import bpy, numpy as np, json, collections
def trace_logo(path, width_m=2.2, depth=0.03, eps=0.9):
    img=bpy.data.images.load(path); w,h=img.size
    px=np.empty(w*h*4,np.float32); img.pixels.foreach_get(px); px=px.reshape(h,w,4)  # row0 = bottom
    lum=px[...,:3].mean(2)*px[...,3]+(1-px[...,3])
    D=np.zeros((h+2,w+2),bool); D[1:-1,1:-1]=lum<0.5
    # directed edges, dark on the left (CCW in y-up coords)
    nxt=collections.defaultdict(list)
    ys,xs=np.where(D[1:-1,1:-1])
    for y,x in zip(ys.tolist(),xs.tolist()):
        Y,X=y+1,x+1
        if not D[Y-1,X]: nxt[(x,y)].append((x+1,y))        # bottom edge -> +x
        if not D[Y,X+1]: nxt[(x+1,y)].append((x+1,y+1))    # right edge -> +y
        if not D[Y+1,X]: nxt[(x+1,y+1)].append((x,y+1))    # top edge -> -x
        if not D[Y,X-1]: nxt[(x,y+1)].append((x,y))        # left edge -> -y
    loops=[]
    while nxt:
        start=next(iter(nxt)); loop=[start]; cur=start; prev=None
        while True:
            outs=nxt[cur]
            if len(outs)>1 and prev is not None:
                d=(cur[0]-prev[0],cur[1]-prev[1])
                # prefer left turn to keep diagonal-touching regions separate
                def turn(o):
                    e=(o[0]-cur[0],o[1]-cur[1]); return -(d[0]*e[1]-d[1]*e[0])
                outs.sort(key=turn)
            n=outs.pop(0)
            if not outs: del nxt[cur]
            if n==start: break
            loop.append(n); prev=cur; cur=n
        loops.append(loop)
    def rdp(pts,eps):
        P=np.array(pts,float)
        def rec(a,b):
            if b<=a+1: return [a]
            seg=P[b]-P[a]; L=np.hypot(*seg)
            sub=P[a+1:b]-P[a]
            d=np.abs(seg[0]*sub[:,1]-seg[1]*sub[:,0])/L if L>0 else np.hypot(sub[:,0],sub[:,1])
            i=int(np.argmax(d))
            if d[i]>eps: return rec(a,a+1+i)+rec(a+1+i,b)
            return [a]
        n=len(P); k=n//2
        idx=rec(0,k)+rec(k,n-1)+[n-1]
        return [pts[i] for i in idx]
    # remove collinear first
    simp=[]
    for L in loops:
        if len(L)<8: continue
        s=rdp(L+[L[0]],eps)[:-1]
        if len(s)>=3: simp.append(s)
    xs=[p[0] for L in simp for p in L]; ys=[p[1] for L in simp for p in L]
    x0,x1,y0,y1=min(xs),max(xs),min(ys),max(ys)
    sc=width_m/(x1-x0); cx=(x0+x1)/2; cy=(y0+y1)/2
    cu=bpy.data.curves.new("Logo","CURVE"); cu.dimensions='2D'; cu.fill_mode='BOTH'; cu.extrude=depth/2
    for L in simp:
        sp=cu.splines.new('POLY'); sp.points.add(len(L)-1)
        for i,(x,y) in enumerate(L): sp.points[i].co=((x-cx)*sc,(y-cy)*sc,0,1)
        sp.use_cyclic_u=True
    ob=bpy.data.objects.new("Logo",cu); bpy.context.scene.collection.objects.link(ob)
    return ob,{"loops":len(simp),"pts":sum(len(L) for L in simp),"height_m":(y1-y0)*sc}
