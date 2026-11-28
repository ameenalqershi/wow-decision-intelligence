"""Paginated vector PDF with Pango text shaping and Cairo destinations."""
from ctypes import CDLL, c_char_p, c_double, c_int, c_void_p, POINTER, byref
from ctypes.util import find_library
from pathlib import Path
from xml.etree import ElementTree as ET
import re

def bind(library, name, result, *params):
    f=getattr(library,name)
    f.argtypes=list(params)
    f.restype=result
    return f

cairo,pc,pango,go=[CDLL(find_library(x)) for x in ('cairo','pangocairo-1.0','pango-1.0','gobject-2.0')]
P,D,I,S=c_void_p,c_double,c_int,c_char_p
def c(name,result,*args): return bind(cairo,'cairo_'+name,result,*args)
pdf=c('pdf_surface_create',P,S,D,D)
img=c('image_surface_create',P,I,I,I)
png=c('surface_write_to_png',I,P,S)
create=c('create',P,P)
color=c('set_source_rgb',None,P,D,D,D)
rect=c('rectangle',None,P,D,D,D,D)
fill=c('fill',None,P)
line=c('line_to',None,P,D,D)
move=c('move_to',None,P,D,D)
stroke=c('stroke',None,P)
width=c('set_line_width',None,P,D)
show=c('show_page',None,P)
begin=c('tag_begin',None,P,S,S)
end=c('tag_end',None,P,S)
close=c('surface_finish',None,P)
status=c('surface_status',I,P)
cr_free=c('destroy',None,P)
surface_free=c('surface_destroy',None,P)
save=c('save',None,P)
restore=c('restore',None,P)
translate=c('translate',None,P,D,D)
scale=c('scale',None,P,D,D)
layout=bind(pc,'pango_cairo_create_layout',P,P)
draw=bind(pc,'pango_cairo_show_layout',None,P,P)
font=bind(pango,'pango_font_description_from_string',P,S)
font_free=bind(pango,'pango_font_description_free',None,P)
abs_size=bind(pango,'pango_font_description_set_absolute_size',None,P,D)
setfont=bind(pango,'pango_layout_set_font_description',None,P,P)
settext=bind(pango,'pango_layout_set_text',None,P,S,I)
setwidth=bind(pango,'pango_layout_set_width',None,P,I)
setwrap=bind(pango,'pango_layout_set_wrap',None,P,I)
setalign=bind(pango,'pango_layout_set_alignment',None,P,I)
setauto=bind(pango,'pango_layout_set_auto_dir',None,P,I)
context=bind(pango,'pango_layout_get_context',P,P)
setdir=bind(pango,'pango_context_set_base_dir',None,P,I)
getsize=bind(pango,'pango_layout_get_size',None,P,POINTER(I),POINTER(I))
unref=bind(go,'g_object_unref',None,P)
def ink(ctx,tint):
    tint=tint.lstrip('#')
    if tint=='white': tint='ffffff'
    color(ctx,*[int(tint[i:i+2],16)/255 for i in [0,2,4]])
def box(ctx,x,y,w,h,tint):
    ink(ctx,tint); rect(ctx,x,y,w,h); fill(ctx)
def text_at(ctx,value,x,y,w,sz=10,tint='#233245',bold=False,rtl=False,paint=True,mono=False):
    l=layout(ctx); f=font((('DejaVu Sans Mono' if mono else 'DejaVu Sans')+(' Bold' if bold else '')).encode())
    abs_size(f,sz*1024); setfont(l,f); font_free(f)
    setauto(l,0); setdir(context(l),int(rtl)); setalign(l,2 if rtl else 0)
    setwidth(l,int(w*1024)); setwrap(l,2); settext(l,value.encode(),-1)
    a,b=I(),I();getsize(l,byref(a),byref(b))
    if paint:
        ink(ctx,tint);move(ctx,x,y);draw(ctx,l)
    unref(l)
    return b.value/1024
def draw_svg(ctx,path,x,y,w):
    root=ET.parse(path).getroot();v=list(map(float,root.attrib['viewBox'].split()))
    save(ctx);translate(ctx,x,y);scale(ctx,w/v[2],w/v[2])
    for item in root:
        tag=item.tag.split('}')[-1];a=item.attrib
        if tag=='rect':box(ctx,*[float(a[k]) for k in ['x','y','width','height']],a['fill'])
        if tag in ('polygon','polyline'):
            pts=[tuple(map(float,p.split(','))) for p in a['points'].split()]
            ink(ctx,a.get('stroke',a.get('fill')));move(ctx,*pts[0])
            for pt in pts[1:]:line(ctx,*pt)
            if tag=='polygon':fill(ctx)
            else:width(ctx,2);stroke(ctx)
        if tag=='text':
            size=float(a['font-size'])
            text_at(ctx,''.join(item.itertext()),float(a['x']),float(a['y'])-size, max(v[2]-float(a['x']),10),size,a['fill'])
    restore(ctx);return v[3]*w/v[2]
def figure_png(path,out):
    root=ET.parse(path).getroot();v=list(map(float,root.attrib['viewBox'].split()))
    s=img(0,int(v[2]*2),int(v[3]*2));ctx=create(s);box(ctx,0,0,v[2]*2,v[3]*2,'#ffffff')
    draw_svg(ctx,path,0,0,v[2]*2);png(s,str(out).encode());cr_free(ctx);surface_free(s)
class Book:
    def __init__(self,out,rtl):
        self.s=pdf(str(out).encode(),595,842);self.ctx=create(self.s);self.rtl=rtl;self.page=1;self.y=56
    def newpage(self):
        show(self.ctx);self.page+=1;self.y=62
        text_at(self.ctx,'WOW DI  /  UADIE',50,23,495,9,'#37566a',True)
        text_at(self.ctx,str(self.page),50,800,495,9,'#37566a',rtl=True)
        box(self.ctx,50,43,495,1,'#bbd5d2')
    def room(self,h):
        if self.y+h>780 and self.y>62:self.newpage()
    def t(self,txt,sz=10,bold=False,col='#233245',rtl=None,indent=0,gap=7,link=None,dest=None,mono=False):
        if rtl is None:rtl=self.rtl
        w=495-indent;x=50+(0 if rtl else indent)
        h=text_at(self.ctx,txt,x,self.y,w,sz,col,bold,rtl,False,mono)
        if h>680 and len(txt)>100:
            p=txt.rfind(' ',0,len(txt)//2)
            if p<1:p=len(txt)//2
            self.t(txt[:p],sz,bold,col,rtl,indent,gap,link,dest,mono)
            self.t(txt[p:].lstrip(),sz,bold,col,rtl,indent,gap,link,None,mono)
            return
        self.room(h+gap+(24 if dest else 0))
        if dest: begin(self.ctx,b'cairo.dest',f"name='{dest}' x=50 y={self.y}".encode());end(self.ctx,b'cairo.dest')
        if link:
            attr=(f"dest='{link[1:]}'" if link.startswith('#') else "uri='"+link.replace("'","%27")+"'")
            begin(self.ctx,b'Link',(attr+f' rect=[{x} {self.y} {w} {h}]').encode())
        text_at(self.ctx,txt,x,self.y,w,sz,col,bold,rtl,True,mono)
        if link:end(self.ctx,b'Link')
        self.y+=h+gap
    def table(self,headers,rows):
        n=len(headers)
        if not n:return
        cw=495/n; fs=8.5 if n>4 else 9.2
        def measure(row,bold=False):
            return max(text_at(self.ctx,v,0,0,cw-12,fs,'#233245',bold,self.rtl,False) for v in row)+12
        def paint(row,h,header=False,alternate=False):
            for j,v in enumerate(row):
                x=50+cw*(n-1-j if self.rtl else j)
                box(self.ctx,x,self.y,cw-0.6,h-0.6,'#165d52' if header else ('#eaf3f1' if alternate else '#f5f7fa'))
                text_at(self.ctx,v,x+6,self.y+6,cw-12,fs,'#ffffff' if header else '#233245',header,self.rtl)
            self.y+=h
        hh=measure(headers,True)
        self.room(hh+min(measure(rows[0]),680-hh) if rows else hh)
        paint(headers,hh,True)
        for j,row in enumerate(rows):
            h=measure(row)
            if h>690-hh:
                for k,v in zip(headers,row):self.t(k+' · '+v,9.4,gap=3)
                continue
            if self.y+h>780:
                self.newpage();paint(headers,hh,True)
            paint(row,h,alternate=j%2==0)
        self.y+=10
    def figure(self,path):
        h=draw_height(path,495);self.room(h+10);draw_svg(self.ctx,path,50,self.y,495);self.y+=h+7
    def cover(self,title,sub):
        box(self.ctx,0,0,595,842,'#10151e');box(self.ctx,48,75,80,6,'#36c9aa')
        text_at(self.ctx,'WOW DI',48,95,500,27,'#36c9aa',True)
        text_at(self.ctx,title,48,196,499,28,'#ffffff',True,self.rtl)
        text_at(self.ctx,sub,48,520,499,13,'#a4c6c2',False,self.rtl)
        text_at(self.ctx,'WOW Decision Intelligence   /   2026-11-28',48,732,499,11,'#e2e8f0')
        self.newpage()
    def finish(self):
        close(self.s);code=status(self.s);cr_free(self.ctx);surface_free(self.s)
        if code:raise RuntimeError('Cairo PDF surface error '+str(code))
def draw_height(path,w):
    vb=list(map(float,ET.parse(path).getroot().attrib['viewBox'].split()));return w*vb[3]/vb[2]
