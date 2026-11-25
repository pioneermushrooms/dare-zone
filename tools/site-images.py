# Renders the home page map images (site/img/) from .lvl files in our own flat "chart" style
# (no game tile art). Usage: python3 tools/site-images.py <maps dir> <out dir>
import struct, zlib, sys, os
def png(path,w,h,px,alpha_black=False):
    raw=b''
    for row in px:
        raw+=b'\x00'+b''.join(bytes((r,g,b,0 if alpha_black and (r,g,b)==(0,0,0) else 255)) for r,g,b in row)
    def chunk(t,d): return struct.pack('>I',len(d))+t+d+struct.pack('>I',zlib.crc32(t+d)&0xffffffff)
    open(path,'wb').write(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(raw,9))+chunk(b'IEND',b''))
def scale(w,h,px,k): return w*k,h*k,[[c for c in row for _ in range(k)] for row in px for _ in range(k)]
M=sys.argv[1].rstrip('/')+'/'
OUT=sys.argv[2].rstrip('/')+'/'
BG=(4,7,14); GRID=(10,22,36); WALL=(34,211,238)
SPECIAL={170:(250,204,21),171:(34,120,70),172:(167,139,250),162:(255,138,31),166:(255,138,31),
         216:(120,140,160),217:(120,140,160),218:(120,140,160),219:(103,232,249),220:(236,72,153)}
def load(name):
    b=open(M+name,'rb').read(); start=0
    if b[:2]==b'BM': start,=struct.unpack_from('<I',b,2)
    elif b[:4]==b'elvl': start,=struct.unpack_from('<I',b,4)
    g={}
    for p in range(start,len(b)-3,4):
        v,=struct.unpack_from('<I',b,p); x=v&0xfff;y=(v>>12)&0xfff;t=v>>24
        if x<1024 and y<1024 and t: g[(x,y)]=t
    return g
def color(t):
    if t in SPECIAL: return SPECIAL[t]
    if 162<=t<=169: return (255,138,31)
    if t>=173 and t<=190: return (70,100,130)
    return WALL
def crop(name,x0,y0,tw,th,cell,out,grid_every=16):
    g=load(name); W,H=tw*cell,th*cell
    px=[[BG]*W for _ in range(H)]
    for gy in range(0,th):
        if (y0+gy)%grid_every==0:
            for x in range(W): px[gy*cell][x]=GRID
    for gx in range(0,tw):
        if (x0+gx)%grid_every==0:
            for y in range(H): px[y][gx*cell]=GRID
    for (x,y),t in g.items():
        if x0<=x<x0+tw and y0<=y<y0+th:
            c=color(t); ox,oy=(x-x0)*cell,(y-y0)*cell
            for j in range(cell-1):
                r=px[oy+j]
                for i in range(cell-1): r[ox+i]=c
    png(out,W,H,px)
def overview(name,out,size=512):
    g=load(name); k=1024//size
    px=[[BG]*size for _ in range(size)]
    for i in range(0,size,32):
        for j in range(size): px[i][j]=GRID; px[j][i]=GRID
    for (x,y),t in g.items(): px[y//k][x//k]=color(t)
    png(out,size,size,px)
os.makedirs(OUT+'',exist_ok=True)
crop('tactical2.lvl',460,640,200,112,8,OUT+'/hero.png')
for n in ['tactical2','teamversus','pbg3d','duelpub']:
    overview(n+'.lvl',OUT+'/'+n+'.png')
