"""Build an editable Office Open XML package with explicit bidirectional text."""
from zipfile import ZipFile,ZIP_DEFLATED
from xml.sax.saxutils import escape,quoteattr
from tempfile import TemporaryDirectory
from pathlib import Path
from publication_pdf import figure_png, draw_height
W='http://schemas.openxmlformats.org/wordprocessingml/2006/main'
R='http://schemas.openxmlformats.org/officeDocument/2006/relationships'
PKG='http://schemas.openxmlformats.org/package/2006/relationships'
DP='http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing'
A='http://schemas.openxmlformats.org/drawingml/2006/main'
PIC='http://schemas.openxmlformats.org/drawingml/2006/picture'
ASVG='http://schemas.microsoft.com/office/drawing/2016/SVG/main'
HEAD='<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
def run(value,size,color,bold,rtl,mono):
    family='DejaVu Sans Mono' if mono else 'DejaVu Sans'
    return (f'<w:r><w:rPr><w:rFonts w:ascii="{family}" w:hAnsi="{family}" w:cs="{family}"/>'
            +('<w:b/><w:bCs/>' if bold else '')+f'<w:color w:val="{color}"/><w:sz w:val="{size}"/><w:szCs w:val="{size}"/>'
            +('<w:rtl w:val="1"/><w:lang w:bidi="ar-SA"/>' if rtl else '')+'</w:rPr>'
            +'<w:t xml:space="preserve">'+escape(value)+'</w:t></w:r>')
class Word:
    def __init__(self,rtl):
        self.rtl=rtl;self.body=[];self.rels=[];self.pictures={};self.bookmarks=0
    def rel(self,typ,target,external=False):
        name=f'rId{len(self.rels)+1}'
        self.rels.append(f'<Relationship Id="{name}" Type="{R}/{typ}" Target={quoteattr(target)}'+(' TargetMode="External"' if external else '')+'/>')
        return name
    def t(self,value,size=21,bold=False,color='233245',rtl=None,link=None,dest=None,style=None,mono=False):
        if rtl is None:rtl=self.rtl
        ppr='<w:pPr>'
        if style:ppr+=f'<w:pStyle w:val="{style}"/><w:keepNext/>'
        if rtl:ppr+='<w:bidi/>'
        ppr+='<w:spacing w:after="95"/><w:jc w:val="'+('right' if rtl else 'left')+'"/></w:pPr>'
        content=run(value,size,color,bold,rtl,mono)
        if dest:
            self.bookmarks+=1
            content=f'<w:bookmarkStart w:id="{self.bookmarks}" w:name="{dest}"/>'+content+f'<w:bookmarkEnd w:id="{self.bookmarks}"/>'
        if link:
            attr=('w:anchor='+quoteattr(link[1:])) if link.startswith('#') else ('r:id='+quoteattr(self.rel('hyperlink',link,True)))
            content=f'<w:hyperlink {attr}>'+content+'</w:hyperlink>'
        self.body.append('<w:p>'+ppr+content+'</w:p>')
    def newpage(self):
        self.body.append('<w:p><w:r><w:br w:type="page"/></w:r></w:p>')
    def table(self,headers,rows):
        n=len(headers)
        if not n:return
        cw=9900//n
        body='<w:tbl><w:tblPr><w:tblW w:w="9900" w:type="dxa"/><w:tblLayout w:type="fixed"/>'+('<w:bidiVisual/>' if self.rtl else '')+'</w:tblPr><w:tblGrid>'+''.join(f'<w:gridCol w:w="{cw}"/>' for _ in headers)+'</w:tblGrid>'
        for i,row in enumerate([headers]+rows):
            body+='<w:tr><w:trPr>'+('<w:tblHeader/>' if i==0 else '')+'</w:trPr>'
            for v in row:
                shade='165D52' if i==0 else ('EAF3F1' if i%2 else 'F5F7FA')
                body+=f'<w:tc><w:tcPr><w:tcW w:w="{cw}" w:type="dxa"/><w:shd w:fill="{shade}"/></w:tcPr><w:p><w:pPr>'+('<w:bidi/><w:jc w:val="right"/>' if self.rtl else '')+'<w:spacing w:after="85" w:before="85"/></w:pPr>'+run(v,17 if n>4 else 18,'FFFFFF' if i==0 else '233245',i==0,self.rtl,False)+'</w:p></w:tc>'
            body+='</w:tr>'
        self.body.append(body+'</w:tbl>')
    def figure(self,path):
        name=path.stem
        if name not in self.pictures:
            self.pictures[name]=(path,self.rel('image','media/'+name+'.png'),self.rel('image','media/'+name+'.svg'))
        _,png,svg=self.pictures[name];cx=5950000;cy=int(draw_height(path,cx));uid=len(self.body)+1
        self.body.append(f'''<w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="{cx}" cy="{cy}"/><wp:docPr id="{uid}" name="{name}"/><a:graphic><a:graphicData uri="{PIC}"><pic:pic><pic:nvPicPr><pic:cNvPr id="0" name="{name}"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="{png}"><a:extLst><a:ext uri="{{96DAC541-7B7A-43D3-8B79-37D633B846F1}}"><asvg:svgBlip r:embed="{svg}"/></a:ext></a:extLst></a:blip><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>''')
    def write(self,dest):
        style=self.rel('styles','styles.xml');footer=self.rel('footer','footer1.xml')
        props=f'<w:sectPr><w:footerReference w:type="default" r:id="{footer}"/><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1000" w:bottom="950" w:left="1000" w:right="1000" w:header="500" w:footer="430"/></w:sectPr>'
        document=HEAD+f'<w:document xmlns:w="{W}" xmlns:r="{R}" xmlns:wp="{DP}" xmlns:a="{A}" xmlns:pic="{PIC}" xmlns:asvg="{ASVG}"><w:body>'+''.join(self.body)+props+'</w:body></w:document>'
        defaults=''.join(f'<Default Extension="{ext}" ContentType="{ct}"/>' for ext,ct in [('rels','application/vnd.openxmlformats-package.relationships+xml'),('xml','application/xml'),('png','image/png'),('svg','image/svg+xml')])
        overrides=''.join(f'<Override PartName="/word/{k}.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.{v}+xml"/>' for k,v in [('document','document.main'),('styles','styles'),('footer1','footer')])
        styles=''.join(f'<w:style w:type="paragraph" w:styleId="Heading{i}"><w:name w:val="heading {i}"/><w:qFormat/><w:pPr><w:keepNext/><w:outlineLvl w:val="{i-1}"/></w:pPr></w:style>' for i in range(1,7))
        with ZipFile(dest,'w',ZIP_DEFLATED) as z,TemporaryDirectory() as tmp:
            z.writestr('[Content_Types].xml',HEAD+'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'+defaults+overrides+'</Types>')
            z.writestr('_rels/.rels',HEAD+f'<Relationships xmlns="{PKG}"><Relationship Id="rId1" Type="{R}/officeDocument" Target="word/document.xml"/></Relationships>')
            z.writestr('word/document.xml',document)
            z.writestr('word/styles.xml',HEAD+f'<w:styles xmlns:w="{W}">'+styles+'</w:styles>')
            z.writestr('word/footer1.xml',HEAD+f'<w:ftr xmlns:w="{W}"><w:p><w:pPr><w:jc w:val="center"/></w:pPr>'+run('WOW DI    /    UADIE     ·     ',17,'37566A',False,False,False)+'<w:fldSimple w:instr="PAGE"/></w:p></w:ftr>')
            z.writestr('word/_rels/document.xml.rels',HEAD+f'<Relationships xmlns="{PKG}">'+''.join(self.rels)+'</Relationships>')
            for name,(src,_,_) in self.pictures.items():
                target=Path(tmp)/(name+'.png');figure_png(src,target)
                z.write(src,'word/media/'+name+'.svg');z.write(target,'word/media/'+name+'.png')
