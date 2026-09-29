"""Read-only Unity 6 map artifact check. No Unity or third-party Python dependency.

Check material PPtr<Shader> values in the actual selected UnityFS bytes, not the
source .mat files or a potentially stale .manifest. Unsupported formats fail closed.
"""
import io,struct,pathlib,sys
sys.stdout.reconfigure(encoding='utf-8')
class R:
 def __init__(self,b,end='<'):self.f=io.BytesIO(b);self.end=end
 def n(self,fmt):return struct.unpack(self.end+fmt,self.f.read(struct.calcsize(fmt)))[0]
 def z(self):
  b=bytearray()
  while (x:=self.f.read(1)) not in (b'\0',b''):b+=x
  return b.decode(errors='replace')
 def align(self,n=4):self.f.seek((-self.f.tell())%n,1)
def lz(b):
 out=bytearray();i=0
 while i<len(b):
  t=b[i];i+=1;n=t>>4
  if n==15:
   while True:
    a=b[i];i+=1;n+=a
    if a!=255:break
  out+=b[i:i+n];i+=n
  if i>=len(b):break
  off=b[i]|b[i+1]<<8;i+=2;n=(t&15)+4
  if (t&15)==15:
   while True:
    a=b[i];i+=1;n+=a
    if a!=255:break
  seed=out[-off:];out+=(seed*((n+off-1)//off))[:n]
 return bytes(out)
def decompress(b,k):
 if k not in (0,2,3): raise ValueError('Unsupported UnityFS compression: '+str(k))
 return b if k==0 else lz(b)
def unpack(path):
 r=R(pathlib.Path(path).read_bytes(),'>');sig=r.z();v=r.n('I');r.z();r.z();size=r.n('Q');cs=r.n('I');us=r.n('I');fl=r.n('I');r.align(16)
 if sig!='UnityFS' or v!=8 or fl&128: raise ValueError('Unsupported UnityFS layout')
 info=R(decompress(r.f.read(cs),fl&63),'>');info.f.read(16)
 blocks=[(info.n('I'),info.n('I'),info.n('H')) for _ in range(info.n('I'))]
 files=[(info.n('Q'),info.n('Q'),info.n('I'),info.z()) for _ in range(info.n('I'))]
 if fl&512:r.align(16)
 data=b''.join(decompress(r.f.read(c),f&63) for u,c,f in blocks)
 return [(name,data[o:o+s]) for o,s,f,name in files]
def inspect(name,b):
 r=R(b,'>');ms=r.n('I');fs=r.n('I');ver=r.n('I');off=r.n('I');end=r.n('B');r.f.read(3)
 if ver!=22: raise ValueError('Expected Unity 6 SerializedFile version 22')
 if ver>=22:ms=r.n('I');fs=r.n('Q');off=r.n('Q');r.n('Q')
 r.end='>' if end else '<';uv=r.z();target=r.n('i');tree=r.n('B');types=[]
 for _ in range(r.n('i')):
  cls=r.n('i');r.n('B');si=r.n('h')
  if cls==114:r.f.read(16)
  r.f.read(16);nodes=[]
  if tree:
   nc=r.n('i');ss=r.n('i')
   for j in range(nc):
    nv=r.n('H');level=r.n('B');tf=r.n('B');to=r.n('I');no=r.n('I');bs=r.n('i');ix=r.n('i');mf=r.n('i');r.n('Q')
    nodes.append(dict(level=level,tf=tf,to=to,no=no,bs=bs,mf=mf))
   sb=r.f.read(ss)
   for nd in nodes:
    for k in ('to','no'):
     a=nd[k];nd[k]=('#'+str(a&0x7fffffff)) if a&0x80000000 else sb[a:sb.find(b'\0',a)].decode(errors='replace')
  r.f.read(r.n('i')*4);types.append((cls,nodes))
 objs=[]
 for _ in range(r.n('i')):
  r.align();pid=r.n('q');pos=r.n('Q')+off;sz=r.n('I');ti=r.n('i');objs.append((pid,pos,sz,ti))
 sc=r.n('i')
 for _ in range(sc):r.n('i');r.align();r.n('q')
 externals=[]
 for _ in range(r.n('i')):
  r.z();r.f.read(16);et=r.n('i');ep=r.z();externals.append((et,ep))
 
 shader_ids={p for p,o,s,t in objs if types[t][0]==48}
 result={'asset':name,'shaderCount':sum(types[t][0]==48 for p,o,s,t in objs),'materials':[]}
 for pid,pos,sz,ti in objs:
  cls,nodes=types[ti]
  if cls not in (21,48):continue
  ob=R(b[pos:pos+sz]);ln=ob.n('i');nm=ob.f.read(ln).decode(errors='replace')
  ob.align()
  if cls==21:
   fid,pointer=ob.n('i'),ob.n('q')
   if pointer!=0 and (fid<0 or fid>len(externals) or (fid==0 and pointer not in shader_ids)):
    raise ValueError('Unresolved shader object reference in material '+nm)
   result['materials'].append({'name':nm,'fileID':fid,'pathID':pointer})
 return result

def validate(path):
 import hashlib
 records=[inspect(name,b) for name,b in unpack(path) if not name.endswith(('.resource','.resS'))]
 materials=[m for record in records for m in record['materials']]
 missing=[m['name'] for m in materials if m['pathID']==0]
 if not materials: raise ValueError('No materials found; unsupported or empty scene bundle')
 if missing: raise ValueError('Null shader references: '+', '.join(missing))
 return {'path':str(path),'sha256':hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest(),
         'materials':len(materials),'shaders':sum(r['shaderCount'] for r in records),'nullShaderReferences':0}

if __name__=='__main__':
 import argparse,json
 sys.stdout.reconfigure(encoding='utf-8')
 ap=argparse.ArgumentParser(description='Fail closed on null material shader references in Unity 6 Windows map bundles.')
 ap.add_argument('bundles',nargs='+')
 ap.add_argument('--report')
 args=ap.parse_args()
 results=[]
 for path in args.bundles:
  try: results.append({'valid':True,**validate(path)})
  except Exception as error: results.append({'valid':False,'path':path,'error':str(error)})
 output=json.dumps(results,ensure_ascii=False,indent=2)
 print(output)
 if args.report: pathlib.Path(args.report).write_text(output,encoding='utf-8')
 sys.exit(0 if all(r['valid'] for r in results) else 1)
