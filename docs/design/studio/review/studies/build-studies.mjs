// Proposed studies, not approved screenshot baselines. UI05 uses the official SVG.
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const dir=dirname(fileURLToPath(import.meta.url));
const ref=readFileSync(resolve(dir,'../../../references/01-WOW-DI-Studio.svg'),'utf8');
const chrome=ref.slice(0,ref.indexOf('<text x="158"'));
const nav=['Overview','Decision Models','Decision Graph','Optimization','Scenarios','Simulation','Integrations','Benchmarks','Evidence','Settings'];
const c={bg:'#10151e',chrome:'#1b2331',card:'#1c2735',edge:'#2c3949',text:'#e2e8f0',muted:'#94a3b8',accent:'#62e4c6',active:'#23463f',button:'#16785f',graph:'#121b27',monitor:'#1b2533',warn:'#facc70'};
const esc=s=>String(s).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;');
const r=(x,y,w,h,fill=c.card,stroke=c.edge,rx=5)=>`<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="${rx}" fill="${fill}" stroke="${stroke}"/>`;
const t=(x,y,s,size=10,fill=c.text,weight=400)=>`<text x="${x}" y="${y}" font-size="${size}" fill="${fill}" font-weight="${weight}">${esc(s)}</text>`;
const line=(x1,y1,x2,y2)=>`<path d="M${x1} ${y1} L${x2} ${y2}" stroke="#477d9e" stroke-width="1.5" fill="none"/>`;
const localActions=new Set(['Open locally','Refresh health','Add variable by keyboard','Select paired trials','Open schedule table']);
const button=(x,y,w,label,primary=false,enabled=localActions.has(label))=>'<g data-control="'+esc(label)+'" data-enabled="'+enabled+'">'+r(x,y,w,23,enabled&&primary?c.button:c.card,enabled&&primary?'none':enabled?'#8292a7':c.edge,4)+t(x+8,y+15,label,10,enabled?(primary?'#ffffff':c.text):c.muted)+'</g>';
const field=(x,y,w,label,value)=>t(x,y,label,9,c.muted)+r(x,y+5,w,23,c.bg,'#8292a7',4)+t(x+8,y+20,value);
const table=(x,y,width,headers,rows)=>{
  let out=r(x,y,width,23,c.chrome,c.edge,3);
  const step=width/headers.length;
  headers.forEach((h,i)=>out+=t(x+7+step*i,y+15,h,9,c.muted,700));
  rows.forEach((row,i)=>{out+=r(x,y+23+i*25,width,25,i%2?c.graph:c.card,c.edge,0);row.forEach((v,j)=>out+=t(x+7+step*j,y+39+i*25,v,9));});
  return out;
};
const pane=(title)=>r(157,152,375,276,c.graph)+t(169,171,title,11,c.text,700);
const inspector=(title,items,action)=>r(542,152,167,276,c.monitor)+t(553,172,title,11,c.text,700)+items.map(([label,value],i)=>t(553,193+i*43,label,9,c.muted)+t(553,209+i*43,value,10)).join('')+(action?button(553,397,145,action,true):'');
const empty=(message,action)=>t(169,204,message,11,c.muted)+button(169,225,150,action);
const form=(fields,action)=>fields.map(([label,value],i)=>field(169,192+i*48,345,label,value)).join('')+button(169,397,150,action,true);
const list=(items,y=184)=>items.map(([label,detail],i)=>r(169,y+i*44,350,37,c.card,c.edge,4)+t(178,y+15+i*44,label,11)+t(178,y+28+i*44,detail,9,c.muted)).join('');
const graph=(nodes,edges)=>edges.map(([a,b])=>line(nodes[a][0]+40,nodes[a][1]+12,nodes[b][0]+40,nodes[b][1]+12)).join('')+nodes.map(([x,y,label])=>r(x,y,80,24,'#20374d','#4276a0',4)+t(x+6,y+15,label,9)).join('');
const studies=[
['UI02',0,'Workspace manager','Open and restore','Recent workspace paths','Workspace details',
 list([['Local workspace','Fixture / local / revision-17-A'],['Remote connection','Not configured'],['Restore backup','No selected manifest']])+button(169,328,145,'Open locally',true)+button(325,328,150,'Preview restore'),
 [['Mode','Local (fixture)'],['Backup digest','Unavailable'],['Restore target','Not selected'],['Validation','Not run']],'Inspect manifest'],
['UI03',0,'Overview dashboard','Refresh sources','Recent decisions and jobs','Service health',
 table(169,184,350,['Run','Revision','State'],[['run-7','revision-17-A','Unknown'],['No other runs','—','—']])+t(169,290,'Measured performance: unavailable',10,c.muted)+button(169,310,150,'Inspect selected run'),
 [['Engine','Unavailable'],['Active jobs','Unknown'],['Integration','Not connected'],['Freshness','No observation']],'Refresh health'],
['UI04',1,'Visual model designer','Validate draft','Model canvas · draft','Property inspector',
 graph([[180,195,'Resource'],[300,195,'Variable'],[300,290,'Constraint'],[420,240,'Objective']],[[0,1],[1,2],[1,3]])+button(169,365,160,'Add variable by keyboard')+t(169,411,'Draft only — not saved',10,c.warn),
 [['Selected','Variable (fixture)'],['Type','Not selected'],['Unit','Required'],['Validation','Incomplete']],'Validate draft'],
['UI06',1,'Decision DSL editor','Validate source','Source editor','Diagnostics',
 r(169,184,350,142,c.bg,c.edge,4)+t(179,200,'1  // DML illustrative source',10,c.muted)+t(179,220,'2  // Model revision: revision-17-A',10,c.accent)+t(179,240,'3  // Enter a model declaration',10,c.muted)+t(179,260,'4  ',10,c.muted)+t(169,348,'No compilation result',10,c.muted)+button(169,365,145,'Format source')+button(325,365,150,'Validate source',true),
 [['Document','model.dml'],['Edit version','Draft'],['Source span','Unavailable'],['Compilation','Not run']],'Inspect diagnostic'],
['UI07',1,'Constraint editor','Validate constraint','Constraint expression','Constraint checks',
 form([['Constraint kind','Hard / soft (choose)'],['Expression','Required'],['Unit and bound','Not validated'],['Penalty','Not applicable']],'Validate'),
 [['Dimension','Unknown'],['Bound semantics','Not validated'],['Conflict proof','Unavailable'],['Saved revision','Unchanged']],'Save after validation'],
['UI08',1,'Objective editor','Review contribution','Ordered objectives','Contribution preview',
 table(169,184,350,['Goal','Direction','Weight'],[['Cost','Minimize','Required'],['Secondary','Not selected','—']])+field(169,266,345,'Lexicographic priority','Review before saving')+button(169,320,150,'Preview contribution'),
 [['Objective vector','Unavailable'],['Unit','Not selected'],['Pareto mode','Capability unknown'],['Evidence','Not evaluated']],'Apply validated goal'],
['UI09',1,'Mathematical model inspector','Inspect source','Representation and ledger','Source mapping',
 list([['Source AST','Select source document'],['Typed UDIR','No compiled model'],['Normalized representation','Unavailable'],['Solver representation','Unsupported or not compiled']])+button(169,365,150,'Inspect mapping'),
 [['Row identity','Not selected'],['Original span','Unavailable'],['Transformation','Unavailable'],['Reconstruction','Unavailable']],'Return to source'],
['UI10',3,'Solver configuration','Check eligibility','Capability and budget','Eligibility inspector',
 form([['Solver profile','Auto (proposed)'],['Time budget','Required'],['Memory budget','Required'],['Parallelism','Not selected']],'Check eligibility'),
 [['Adapter manifest','Unavailable'],['Eligibility','Not checked'],['CPU accounting','Unavailable'],['Options','Unvalidated']],'Apply valid budget'],
['UI11',3,'Optimization monitor','Refresh run','Observed run events','Run status',
 table(169,184,350,['Sequence','Event ID','Metric'],[['—','No selected run','Unavailable']])+t(169,253,'Objective convergence',11,c.text,700)+r(169,264,350,88,c.bg,c.edge)+t(180,291,'No observations to plot',10,c.muted)+button(169,368,150,'Refresh snapshot'),
 [['Run','Not selected'],['Termination','Unknown'],['Bound / gap','Unavailable'],['Memory / elapsed','Unavailable']],'Cancel if running'],
['UI12',3,'Algorithm execution visualizer','Inspect components','Component evidence tree','Reuse inspector',
 list([['Full solve baseline','Not selected'],['Component decomposition','No recorded trace'],['Invalidation or reuse','Unavailable'],['Fallback reason','Unavailable']])+button(169,365,150,'Compare baseline'),
 [['Backend events','Unavailable'],['Component ID','Not selected'],['Reuse proof','Unavailable'],['Run evidence','Not selected']],'Inspect evidence'],
['UI13',4,'Scenario comparison studio','Compare revisions','Baseline and alternative','Comparison inspector',
 field(169,190,345,'Baseline revision','Select frozen revision')+field(169,238,345,'Alternative revision','Select frozen revision')+table(169,294,350,['Input','Baseline','Alternative'],[['No delta','—','—']])+button(169,365,150,'Inspect changed inputs'),
 [['Cost delta','Unavailable'],['Risk','Unavailable'],['Compatibility','Not checked'],['Evidence','Not selected']],'Compare compatible'],
['UI14',5,'Simulation studio','Configure replay','Experiment configuration','Seed and result inspector',
 form([['Experiment kind','Discrete event / Monte Carlo'],['Seed','Required'],['Sample budget','Required'],['Distribution','Not validated']],'Validate experiment'),
 [['Actual samples','Unavailable'],['Termination','Not started'],['Quantiles','Unavailable'],['Replay manifest','Not selected']],'Replay validated'],
['UI15',3,'Production scheduling studio','Inspect assignment','Machine schedule','Operation details',
 t(169,189,'Machine / time horizon',10,c.muted)+r(169,200,350,140,c.bg,c.edge)+[0,1,2].map(i=>t(177,219+i*40,'Machine '+(i+1)+' (fixture)',9,c.muted)+r(285,207+i*40,225,27,c.graph,c.edge,3)).join('')+t(169,357,'No verified schedule selected',10,c.muted)+button(169,374,150,'Open schedule table'),
 [['Operation','Not selected'],['Interval','Unavailable'],['Maintenance','Unavailable'],['Feasibility','Unknown']],'Inspect evidence'],
['UI16',3,'Resource allocation visualizer','Filter horizon','Capacity and contributors','Allocation inspector',
 field(169,190,345,'Resource and horizon','Select frozen scope')+table(169,238,350,['Resource','Used','Available'],[['No resource','—','—']])+t(169,308,'Utilization: unavailable',11,c.muted)+button(169,328,150,'Inspect contributors'),
 [['Numerator','Unavailable'],['Denominator','Unavailable'],['Unit','Not selected'],['Provenance','Unavailable']],'Open evidence'],
['UI17',8,'Decision evidence viewer','Verify bundle','Original-model checks','Evidence inspector',
 table(169,184,350,['Row ID','Original check','Outcome'],[['—','No bundle','Unknown']])+list([['Objective vector','Unavailable'],['Certificate','Not supplied']],246)+button(169,375,150,'Verify selected bundle'),
 [['Math status','Unknown'],['Termination','Unavailable'],['Verification','Not run'],['Digest','Unavailable']],'Export evidence'],
['UI18',8,'Explainability studio','Compare alternative','Counterfactual evidence','Explanation inspector',
 field(169,190,345,'Baseline run','Select verified run')+field(169,238,345,'Forced alternative','Not selected')+table(169,294,350,['Binding row','Source','Evidence'],[['—','—','Unavailable']])+button(169,365,150,'Request comparison'),
 [['Sensitivity','Unavailable'],['Proof type','Unknown'],['Budget match','Not checked'],['Cost change','Unavailable']],'Inspect proof'],
['UI19',7,'Performance and benchmark studio','Compare trials','Paired trial observations','Environment manifest',
 table(169,184,350,['Trial','Elapsed','Outcome'],[['No trial','—','Unavailable']])+t(169,261,'No comparable samples selected',10,c.muted)+r(169,273,350,70,c.bg,c.edge)+t(180,298,'Regression chart unavailable',10,c.muted)+button(169,365,150,'Select paired trials'),
 [['Suite','Not selected'],['Hardware','Unavailable'],['Seed','Unavailable'],['Failed trials','Unknown']],'Export manifest'],
['UI20',6,'Integration mapping designer','Preview transform','Source to target mapping','Import validation',
 table(169,184,350,['Source','Target','Unit'],[['No schema','Not mapped','—']])+field(169,260,345,'Transformation','Select approved transform')+button(169,316,150,'Preview rows')+t(169,362,'No import has been committed.',10,c.muted),
 [['Connector','Not selected'],['Schema version','Unavailable'],['Rejected rows','Not evaluated'],['Credentials','Redacted']],'Validate staged import'],
['UI21',9,'Administration and developer portal','Review permissions','Authorized role matrix','Policy inspector',
 table(169,184,350,['Role','Read','Manage'],[['No tenant','Unknown','Unknown']])+list([['SDK documentation','Version not selected'],['Extensions','Manifest unavailable']],246)+button(169,375,150,'Inspect effective role'),
 [['Tenant','Not selected'],['Policy version','Unavailable'],['Authorization','Not checked'],['Audit history','Unavailable']],'Review audit'],
['UI22',0,'Command palette and productivity','Search commands','Registered commands','Command context',
 field(169,190,345,'Search','Type a registered command')+list([['Open workspace','Registered view action'],['Inspect evidence','Requires selected run'],['External shell','Restricted capability']],228)+t(169,398,'Esc: close and restore focus',10,c.muted),
 [['Command ID','Not selected'],['Permission','Not checked'],['Arguments','Not supplied'],['Context','Current workspace']],'Run authorized'],
['UI23',9,'Settings and diagnostics','Apply validated settings','Effective settings','Diagnostics',
 form([['Theme','Dark'],['Language','English'],['Logging level','Not selected'],['Runtime configuration','No valid draft']],'Validate settings'),
 [['Effective version','Unavailable'],['Health','Unavailable'],['Secrets','Redacted'],['Restart required','Unknown']],'Export diagnostics']
];
function navChrome(index){
 let s=chrome.replace('<rect x="5" y="111" width="134" height="28" rx="4" fill="#23463f"/><rect x="5" y="111" width="3" height="28" rx="0" fill="#3ed5b3"/>','').replace('y="119" width="11" height="11" rx="2" fill="none" stroke="#5be1c2"','y="119" width="11" height="11" rx="2" fill="none" stroke="#8292a7"').replace('y="129" font-size="11" fill="#62e4c6"','y="129" font-size="11" fill="#aebacb"');
 const y=57+31*index;
 s=s.replace(`<rect x="15" y="${y}"`,r(5,y-8,134,28,c.active,'none',4)+r(5,y-8,3,28,'#3ed5b3','none',0)+`<rect x="15" y="${y}"`);
 s=s.replace(`y="${y}" width="11" height="11" rx="2" fill="none" stroke="#8292a7"`,`y="${y}" width="11" height="11" rx="2" fill="none" stroke="#5be1c2"`).replace(`y="${y+10}" font-size="11" fill="#aebacb"`,`y="${y+10}" font-size="11" fill="#62e4c6"`);
 return s;
}
for(const [id,index,title,action,main,side,content,details,sideAction] of studies){
 let svg=navChrome(index).replace('><rect width="720"',`><title>${esc(id+' '+title+' — proposed design fixture')}</title><rect width="720"`);
 svg+=t(158,54,title,15,c.text,700)+t(158,70,'Design fixture / '+nav[index]+' / '+id,9,c.muted);
 svg+=r(157,82,552,58)+t(169,100,'Selected context',9,c.muted)+t(169,119,'No authoritative runtime data loaded',11)+button(543,100,155,action,true,['Open and restore','Refresh sources','Search commands','Filter horizon','Configure replay'].includes(action));
 svg+=pane(main)+content+inspector(side,details,sideAction);
 svg+=r(0,438,720,22,c.chrome,'none',0)+t(12,452,'Engine status: unavailable',9,c.muted)+t(562,452,'Local workspace | design fixture',9,c.muted)+'</svg>';
 writeFileSync(resolve(dir,id+'.svg'),svg+'\n');
}
console.log('Generated '+studies.length+' proposed module studies; UI01 reviewed separately, UI05 unchanged.');
