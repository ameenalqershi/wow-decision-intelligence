import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const dir=path.dirname(fileURLToPath(import.meta.url));
const out=path.join(dir,'rtl');fs.mkdirSync(out,{recursive:true});
const data=JSON.parse(fs.readFileSync(path.join(dir,'arabic-fixtures.json'),'utf8'));
const nav=['نظرة عامة','نماذج القرارات','رسم القرارات','التحسين','السيناريوهات','المحاكاة','التكاملات','المقاييس','الأدلة','الإعدادات'];
const active=[0,0,0,1,2,1,1,1,1,3,3,3,4,5,3,3,8,8,7,6,9,0,9];
const c={bg:'#10151e',chrome:'#1b2331',sidebar:'#151d29',card:'#1c2735',edge:'#2c3949',text:'#e2e8f0',muted:'#94a3b8',active:'#23463f',accent:'#62e4c6',warn:'#facc70',button:'#16785f'};
const r=(x,y,w,h,fill,stroke='none',rx=4)=>'<rect x="'+x+'" y="'+y+'" width="'+w+'" height="'+h+'" rx="'+rx+'" fill="'+fill+'" stroke="'+stroke+'"/>';
// SVG text anchor=start on RTL text places the run to the left of its logical start.
const t=(x,y,s,size=11,fill=c.text,bold=false,rtl=true)=>'<text x="'+x+'" y="'+y+'" font-size="'+size+'" fill="'+fill+'" font-weight="'+(bold?700:400)+'" direction="'+(rtl?'rtl':'ltr')+'" unicode-bidi="isolate" text-anchor="start">'+s+'</text>';
let i=0;
for(const [id,[title,msg,recovery]] of Object.entries(data)){
 let svg='<svg xmlns="http://www.w3.org/2000/svg" lang="ar" viewBox="0 0 720 460" width="720" height="460" font-family="Segoe UI, Arial, DejaVu Sans, sans-serif">';
 svg+=r(0,0,720,460,c.bg,'none',0)+r(0,0,720,31,c.chrome,'none',0)+r(576,31,144,407,c.sidebar,'none',0)+r(0,438,720,22,c.chrome,'none',0);
 svg+=t(704,19,'استوديو ذكاء القرارات',11,c.text,true)+t(14,19,'WOW DI',10,c.muted,false,false);
 for(let j=0;j<nav.length;j++){const y=57+j*31,a=active[i]===j;
 if(a)svg+=r(581,y-8,134,28,c.active)+r(712,y-8,3,28,'#3ed5b3','none',0);
 svg+=r(694,y,11,11,'none',a?'#5be1c2':'#8292a7',2)+t(685,y+10,nav[j],11,a?c.accent:'#aebacb');}
 svg+=t(562,54,title,15,c.text,true)+t(12,70,id,9,c.muted,false,false)+t(562,70,'عينة تصميمية • العربية',9,c.muted);
 svg+=r(11,82,552,58,c.card,c.edge,5)+t(550,102,'السياق المحدد',9,c.muted)+t(550,120,'لم يتم تأكيد بيانات التشغيل',11);
 // Mirror both primary and inspector placement, not the content or numbers.
 svg+=r(188,152,375,276,'#121b27',c.edge,5)+t(550,172,'بيانات غير متاحة',11,c.text,true);
 svg+=r(200,188,350,102,c.card,c.edge,5)+t(538,211,msg,11,c.warn,true)+t(538,236,'لا يتم استنتاج نتيجة أو قيمة غير مؤكدة',10);
 svg+=r(352,250,186,24,c.button,c.accent)+t(528,266,recovery,10,'#ffffff');
 svg+=t(550,320,'التنقل بلوحة المفاتيح',11,c.text,true)+t(550,344,'انتقل إلى العنصر التالي أو عد للسياق الآمن',10,c.muted)+t(550,368,'يظل ترتيب القراءة منطقياً دون تغيير المعرفات',10,c.muted);
 svg+=r(11,152,167,276,'#1b2533',c.edge,5)+t(165,172,'السياق والتشخيص',11,c.text,true)+t(165,196,'معرف النسخة',9,c.muted)+t(24,213,'revision-17-A',10,c.text,false,false)+t(165,243,'حالة الخدمة',9,c.muted)+t(165,260,'غير مؤكدة',10,c.warn)+t(165,290,'آخر حالة سليمة',9,c.muted)+t(165,307,'تُحفظ عند توفرها',10);
 svg+=r(22,397,145,24,c.card,c.edge)+t(156,413,'تنفيذ غير متاح',10,c.muted);
 svg+=t(706,452,'حالة المحرك: غير مؤكدة',9,c.muted)+t(12,452,'Design fixture / '+id,9,c.muted,false,false)+'</svg>';
 fs.writeFileSync(path.join(out,id+'.svg'),svg+'\n');i++;
}
console.log('Generated '+i+' Arabic RTL unavailable-state design fixtures with isolated LTR identifiers.');
