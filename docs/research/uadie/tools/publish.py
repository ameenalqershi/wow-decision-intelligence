"""Render both UADIE editions. Requires Python markdown-it, PangoCairo and Cairo."""
import argparse
from pathlib import Path
import re
from markdown_it import MarkdownIt
from figures import ROOT,FIGURES
from publication_pdf import Book
from publication_word import Word
PARSER=MarkdownIt().enable("table")
def parse(source):
    lines=source.read_text()
    tokens=PARSER.parse(lines)
    blocks=[];row=None;header=None;i=0;table_id=0
    for n,token in enumerate(tokens):
        tag=token.type
        if tag=='table_open': header=None;table_id+=1
        if tag=='tr_open':row=[]
        if tag=='tr_close':
            if header is None:header=row;blocks.append(('table',str(table_id),''))
            else:blocks.append(('row',list(zip(header,row)),''))
            row=None
        if tag in ('fence','code_block'):
            blocks.append(('code',token.content,''))
        if tag!='inline':continue
        children=token.children or []
        value=''.join(t.content if t.type in ['text','code_inline','html_inline'] else (' ' if t.type in ['softbreak','hardbreak'] else '') for t in children).strip()
        links=[(t.attrGet('href') or '') for t in children if t.type=='link_open']
        if row is not None:
            row.append(value+(' · '+' '.join(links) if links else ''));continue
        if tokens[n-1].type=='heading_open':
            blocks.append(('head',value,int(tokens[n-1].tag[1:])));continue
        for t in children:
            if t.type=='image':blocks.append(('figure',t.content,(source.parent/t.attrGet('src')).resolve()))
        if value:
            blocks.append(('p',value,''))
        for link in links:
            if link.startswith(('https://','http://')):blocks.append(('url',link,link))
    return blocks
def render(source,output=None):
    rtl=source.stem=='guide'
    dest=output or source.with_suffix('');blocks=parse(source)
    title=next(v for k,v,h in blocks if k=='head')
    blocks.pop(next(i for i,b in enumerate(blocks) if b[0]=='head'))
    if not rtl:
        for directory in ['algorithms','mathematical-proofs','references','experiments','implementation-roadmap']:
            for appendix in sorted((ROOT/directory).glob('*.md')):
                for k,v,h in parse(appendix):
                    blocks.append((k,v,min(6,h+1) if k=='head' else h))
    known={b[2].stem for b in blocks if b[0]=='figure'}
    blocks.append(('head','دليل المخططات' if rtl else 'Figure atlas',2))
    legend='I تنفيذ ضمن نطاق محدد · P تصميم مقترح · T نتيجة مثبتة بشروطها · C خطوة مشروطة · U غير مدعوم أو متعذر' if rtl else 'I = implemented within scope. P = proposed. T = theorem under stated premises. C = conditional operation. U = unsupported or unresolved.'
    blocks.append(('p',legend,''))
    for name,en,ar,*_ in FIGURES:
        if name not in known:blocks.append(('figure',ar if rtl else en,ROOT/'diagrams'/f'{name}.svg'))
    pdf=Book(dest.with_suffix('.pdf'),rtl);doc=Word(rtl)
    subtitle=('طبعة بحثية قابلة للتدقيق  ·  هندسة القرار' if rtl else 'Scientific review edition   /   Algorithm research and engineering')
    pdf.cover(title,subtitle)
    doc.t('WOW DI',48,True,'165D52',rtl=False);doc.t(title,55,True,'1B2331',style='Heading1');doc.t(subtitle,27);doc.t('2026-11-28',21,rtl=False);doc.newpage()
    def both(v,sz=10,bold=False,col='233245',**args):
        pdf.t(v,sz,bold,'#'+col,**{k:x for k,x in args.items() if k!='style'})
        doc.t(v,round(sz*2),bold,col,**{k:x for k,x in args.items() if k not in ['gap','indent']})
    both('فهرس المحتويات' if rtl else 'Contents',21,True,'165D52')
    headings={}
    for i,(kind,value,level) in enumerate(blocks):
        if kind=='head':
            anchor='section'+str(i);headings[i]=anchor
            if level<=2:both(value,10,False,'165D52',link='#'+anchor,gap=3)
    pdf.newpage();doc.newpage();fig=0;tab=0
    for i,(kind,value,arg) in enumerate(blocks):
        if kind=='head':
            pdf.y+=8
            both(value,16 if arg<=2 else 12,True,'165D52',dest=headings[i],style=f'Heading{min(6,arg)}')
        elif kind=='p':both(value)
        elif kind=='code':
            for line in value.splitlines():
                both(line or ' ',8.4,False,'254F63',rtl=False,mono=True,gap=3)
        elif kind=='url':both(value,8.2,False,'165D52',rtl=False,link=value)
        elif kind=='table':
            tab+=1
            rows=[];headers=[];j=i+1
            while j<len(blocks) and blocks[j][0]=='row':
                pairs=blocks[j][1]
                if not headers:headers=[h for h,_ in pairs]
                rows.append([cell for _,cell in pairs]);j+=1
            pdf.room(75)
            both(('الجدول ' if rtl else 'Table ')+str(tab),10,True,'165D52')
            if headers:pdf.table(headers,rows);doc.table(headers,rows)
        elif kind=='row':continue
        elif kind=='figure':
            fig+=1
            pdf.room(225)
            both(('الشكل ' if rtl else 'Figure ')+str(fig)+'   /   '+value,11,True,'165D52')
            pdf.figure(arg);doc.figure(arg)
    pdf.finish();doc.write(dest.with_suffix('.docx'))
if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('source',nargs='*',type=Path);args=parser.parse_args()
    for src in args.source or [ROOT/'scientific-specification/specification.md',ROOT/'educational-guide-ar/guide.md']:render(src)
