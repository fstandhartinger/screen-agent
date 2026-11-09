namespace ScreenAgent.Core;
// The page functions of the free Chrome toolset. They run in an isolated world named "screen-agent" (Page.createIsolatedWorld): page scripts cannot see or change them, and the elements behind
// the refs are kept there (globalThis.__sa = {g: snapshot generation, m: Map ref number -> element}). A navigation destroys the world, a new snapshot replaces the map: both make old refs stale.
// Only chrome_eval runs the model's own script, in the page's main world.
public static class ChromePageJs
{
    // Verbatim strings (not raw literals): the repo's catch scanner (ErrorsLoggedTests) reads them correctly.
    // (gen, base, all) -> {g,n,title,url,vw,vh,sy,sm,items,cut}. Walks in document order through open shadow roots, slots and same-origin iframes.
    // Visible = not display:none / visibility:hidden / opacity:0 / aria-hidden, non-zero box. Scope (unless all): the viewport plus 50 % above and below.
    public const string Snapshot=@"function(gen,base,all){
const S=globalThis.__sa={g:gen,m:new Map()};
const items=[];let n=base,cut=false,reason='',visited=0;const MAX=4000,started=performance.now();
const budget=(visit=false)=>{if(cut)return false;if(visit&&++visited>20000){cut=true;reason='nodes';}else if(performance.now()-started>=1500){cut=true;reason='time';}return !cut;};
const vw=innerWidth,vh=innerHeight,top=all?-1e9:-vh*0.5,bot=all?1e9:vh*1.5;
const CV={opacityProperty:true,visibilityProperty:true,checkOpacity:true,checkVisibilityCSS:true};
const ROLES=new Set(['button','link','tab','menuitem','menuitemcheckbox','menuitemradio','option','checkbox','radio','switch','textbox','searchbox','combobox','slider','treeitem']);
const NATIVE='a[href],button,input:not([type=hidden]),textarea,select,summary,[role=button],[role=link],[role=tab],[role=menuitem],[role=option],[role=checkbox],[role=radio],[role=switch],[role=textbox],[role=combobox],[role=slider],[contenteditable=""""],[contenteditable=true],[contenteditable=plaintext-only],[onclick],[tabindex]';
const SKIP=new Set(['script','style','noscript','template','head','meta','link','title','datalist','option','optgroup']);
const HEAD=new Set(['h1','h2','h3','h4','h5','h6']);
let buf='';
const push=x=>{if(items.length>=MAX){cut=true;reason='items';return;}items.push(x);};
const flush=pre=>{const t=buf.replace(/\s+/g,' ').trim();buf='';if(t)push((pre||'')+(t.length>160?t.slice(0,159)+'…':t));};
const sq=(s,m)=>{s=String(s||'').replace(/\s+/g,' ').trim();return s.length>m?s.slice(0,m-1)+'…':s;};
const inScope=(r,ox,oy)=>r.bottom+oy>=top&&r.top+oy<=bot&&(all||(r.right+ox>=0&&r.left+ox<=vw));
const roleOf=(el,tag)=>{const r=el.getAttribute('role');if(r)return r;if(tag==='a')return 'link';if(tag==='button'||tag==='summary')return 'button';if(tag==='select')return 'combobox';if(tag==='textarea')return 'textbox';
 if(tag==='input'){const t=el.type;return t==='checkbox'?'checkbox':t==='radio'?'radio':(t==='submit'||t==='button'||t==='reset'||t==='image')?'button':t==='range'?'slider':t==='file'?'file':'textbox';}
 if(el.isContentEditable)return 'textbox';return 'clickable';};
const editable=(el,tag,role)=>tag==='textarea'||tag==='select'||(tag==='input'&&!/^(checkbox|radio|submit|button|reset|image|file)$/.test(el.type))||el.isContentEditable||role==='textbox'||role==='searchbox'||role==='combobox';
const ltxt=(lb,el)=>{let s='';const w=lb.ownerDocument.createTreeWalker(lb,4);for(let t=w.nextNode();t&&s.length<512&&budget(true);t=w.nextNode()){if(!el.contains(t))s+=t.nodeValue+' ';}return s;};
const nameOf=(el,tag,ed,nested)=>{
 if(!budget())return '';
 let s=el.getAttribute('aria-label')||'';
 if(!s){const lb=el.getAttribute('aria-labelledby');if(lb){const root=el.getRootNode();s=lb.split(/\s+/).map(i=>{const e=(root.getElementById&&root.getElementById(i))||el.ownerDocument.getElementById(i);return e?(e.innerText||e.textContent||''):'';}).join(' ');}}
 if(!s&&(tag==='input'||tag==='textarea'||tag==='select')){if(el.labels&&el.labels.length)s=ltxt(el.labels[0],el);if(!s)s=el.placeholder||'';if(!s&&tag==='input'&&/^(submit|button|reset)$/.test(el.type))s=el.value||'';if(!s)s=el.title||el.name||el.id||'';return sq(s,100);}
 if(!s&&ed)s=el.getAttribute('aria-placeholder')||el.getAttribute('data-placeholder')||el.getAttribute('placeholder')||el.title||'';
 if(!s&&!ed)s=nested?(el.title||''):(el.innerText||el.textContent||'');
 if(!s&&!ed&&el.querySelector){const im=el.querySelector('img[alt],svg[aria-label],[aria-label],[title]');if(im)s=im.getAttribute('alt')||im.getAttribute('aria-label')||im.getAttribute('title')||'';}
 if(!s)s=el.title||el.getAttribute('data-testid')||el.getAttribute('name')||el.id||'';
 return sq(s,100);};
const emit=(el,tag,target,nested)=>{
 const role=roleOf(el,tag),ed=editable(el,tag,role);
 const o={r:n,k:role,n:nameOf(el,tag,ed,nested)};
 if(tag==='input'&&el.type==='password'){if(el.value)o.p=el.value.length;}
 else if(tag==='select'){const op=el.selectedOptions&&el.selectedOptions[0];o.v=sq(op?op.label||op.text:'',80);}
 else if(tag==='input'&&(el.type==='checkbox'||el.type==='radio'))o.c=!!el.checked;
 else if(ed&&'value' in el&&tag!=='button')o.v=sq(el.value,80);
 else if(ed)o.v=sq(el.innerText,80);
 const ac=el.getAttribute('aria-checked')||el.getAttribute('aria-pressed');if(ac==='true'||ac==='false')o.c=ac==='true';
 const ax=el.getAttribute('aria-expanded');if(ax==='true'||ax==='false')o.x=ax==='true';
 if(tag==='summary'&&el.parentElement&&el.parentElement.localName==='details')o.x=!!el.parentElement.open;
 if(el.getAttribute('aria-selected')==='true')o.s=true;
 if(el.disabled===true||el.getAttribute('aria-disabled')==='true')o.d=true;
 if(tag==='a'&&el.href){try{const u=new URL(el.href,el.ownerDocument.baseURI);if(u.host&&u.host!==location.host)o.h=u.host;else if(u.protocol!=='http:'&&u.protocol!=='https:')o.h=u.protocol;}catch(e){/* the page forbids or lacks it: the step is skipped, the caller reads the outcome */}}
 S.m.set(n,target||el);n++;push(o);};
const interactive=(el,tag,cs)=>{
 if(tag==='button'||tag==='textarea'||tag==='select'||tag==='summary')return true;
 if(tag==='input')return el.type!=='hidden';
 if(tag==='a'&&el.hasAttribute('href'))return true;
 const role=el.getAttribute('role');if(role&&ROLES.has(role))return true;
 if(el.isContentEditable)return !(el.parentElement&&el.parentElement.isContentEditable);
 if(el.hasAttribute('onclick'))return true;
 if(el.hasAttribute('tabindex')&&el.tabIndex>=0)return true;
 if(cs.cursor==='pointer'){if(tag==='label'&&el.control)return false;const p=el.parentElement;if(p&&getComputedStyle(p).cursor==='pointer')return false;return !el.querySelector(NATIVE);}
 return false;};
const kids=el=>{if(el.shadowRoot)return el.shadowRoot.childNodes;if(el.localName==='slot'){const a=el.assignedNodes({flatten:true});if(a.length)return a;}return el.childNodes;};
const rg=document.createRange();
const walk=function*(el,ox,oy,shownUp){
 if(!budget(true))return;
 const tag=el.localName;
 if(SKIP.has(tag))return;
 if(el.getAttribute('aria-hidden')==='true')return;
 let shown=shownUp;
 const cs=getComputedStyle(el);
 if(tag==='input'&&(el.type==='checkbox'||el.type==='radio'||el.type==='file')&&el.labels&&el.labels.length){
  // a real box hidden behind a styled label (opacity 0, display none, zero size): the label is what a hand clicks
  const r=el.getBoundingClientRect();
  if(!(cs.display!=='none'&&shownUp&&r.width>1&&r.height>1&&(!el.checkVisibility||el.checkVisibility(CV)))){
   const lb=el.labels[0];const lr=lb.getBoundingClientRect();
   if(lr.width>0&&lr.height>0&&inScope(lr,ox,oy)&&(!lb.checkVisibility||lb.checkVisibility(CV))){flush();emit(el,tag,lb,false);}
   return;}
 }
 if(cs.display==='none')return;
 if(el.checkVisibility&&!el.checkVisibility(CV)){if(cs.opacity==='0'||cs.contentVisibility==='hidden')return;shown=cs.visibility==='visible';}
 else if(cs.visibility==='visible')shown=true;
 if(tag==='br'){flush();return;}
 if(tag==='hr'){flush();return;}
 const disp=cs.display;const block=!(disp.startsWith('inline')||disp==='contents');
 if(tag==='img'){if(shown&&el.alt&&!interactive(el,tag,cs)){const r=el.getBoundingClientRect();if(r.width>0&&r.height>0&&inScope(r,ox,oy))buf+=' '+el.alt+' ';return;}if(!interactive(el,tag,cs))return;}
 if(tag==='iframe'||tag==='frame'){
  flush();const r=el.getBoundingClientRect();if(!shown||r.width<=0||r.height<=0||!inScope(r,ox,oy))return;
  let d=null;try{d=el.contentDocument;}catch(e){/* the page forbids or lacks it: the step is skipped, the caller reads the outcome */}
  if(d&&d.body){push('[iframe '+sq(el.title||el.name||'',40)+']');yield walk(d.body,ox+r.left+el.clientLeft,oy+r.top+el.clientTop,true);flush();push('[/iframe]');}
  else{let h='';try{h=new URL(el.src,el.ownerDocument.baseURI).host;}catch(e){/* the page forbids or lacks it: the step is skipped, the caller reads the outcome */}push('[iframe fremder Herkunft'+(h?' '+h:'')+': Inhalt nicht enthalten, mit screenshot und click bedienen]');}
  return;}
 const inter=interactive(el,tag,cs);
 if(inter){
  const r=el.getBoundingClientRect();const has=r.width>0&&r.height>0;
  const nested=tag!=='input'&&tag!=='textarea'&&tag!=='select'&&!el.isContentEditable&&!!el.querySelector(NATIVE);
  if(shown&&has&&inScope(r,ox,oy)){flush();emit(el,tag,null,nested);}
  if(!nested)return;
 }
 if(tag==='svg'||tag==='canvas'||tag==='video'||tag==='audio'||tag==='object'||tag==='embed'||tag==='select'||tag==='textarea')return;
 const head=HEAD.has(tag)?'# ':disp==='list-item'?'• ':'';
 if(block)flush();
 for(const c of kids(el)){
  if(cut)break;
  if(c.nodeType===3){
   if(!budget(true))break;
   if(!shown)continue;const t=c.nodeValue;if(!t||!/\S/.test(t))continue;
   rg.selectNodeContents(c);const r=rg.getBoundingClientRect();
   if((r.width>0||r.height>0)&&inScope(r,ox,oy))buf+=t;
  }else if(c.nodeType===1)yield walk(c,ox,oy,shown);
  else if(!budget(true))break;
 }
 if(block)flush(head);
};
const rootEl=document.body||document.documentElement;
if(rootEl){
 const stack=[walk(rootEl,0,0,true)];
 while(stack.length&&budget()){
  const step=stack[stack.length-1].next();
  if(step.done)stack.pop();else if(stack.length>=200){cut=true;reason='depth';break;}else stack.push(step.value);
 }
}
flush();
const de=document.documentElement;const sh=Math.max(de?de.scrollHeight:0,document.body?document.body.scrollHeight:0);
return {g:gen,n:n-base,title:document.title,url:location.href,vw:vw,vh:vh,sy:Math.round(scrollY),sm:Math.max(0,Math.round(sh-vh)),items:items,cut:cut,reason:reason,visited:visited};
}";
    // (n, g, mode) -> {x,y,how,kind,dis,name}. mode "click": scroll to the centre of the viewport, hit test, and when the element cannot be hit by the mouse: element.click() (how "js").
    // mode "fill": the same, but the fallback is focus() (how "focus"). mode "peek": no scrolling, no fallback. x,y are CSS pixels of the tab's viewport (frame offsets added).
    public const string Locate=@"function(n,g,mode){
const S=globalThis.__sa;if(!S||S.g!==g)return {e:'stale'};const el=S.m.get(n);if(!el||!el.isConnected)return {e:'stale'};
if(mode!=='peek'&&el.scrollIntoView)el.scrollIntoView({block:'center',inline:'center',behavior:'instant'});
const tag=el.localName;
const kind=tag==='select'?'select':tag==='textarea'?'textarea':tag==='input'?'input':el.isContentEditable?'ce':'other';
const editable=!el.disabled&&!el.readOnly&&el.getAttribute('aria-disabled')!=='true'&&(kind==='select'||kind==='textarea'||kind==='ce'||(kind==='input'&&!/^(checkbox|radio|submit|button|reset|image|file|hidden)$/.test(el.type)));
const out={editable:editable,kind:kind,type:String(el.type||''),dis:el.disabled===true||el.getAttribute('aria-disabled')==='true',name:String(el.getAttribute('aria-label')||(el.labels&&el.labels.length&&kind!=='other'?Array.from(el.labels[0].childNodes).filter(c=>c.nodeType===3).map(c=>c.nodeValue).join(' ').trim()||el.labels[0].innerText:'')||(kind==='other'?el.innerText||el.value:'')||el.placeholder||el.title||el.name||el.id||tag).replace(/\s+/g,' ').trim().slice(0,60)};
if(mode==='fill'&&!editable){out.how='none';return out;}
let ox=0,oy=0,w=el.ownerDocument.defaultView,inView=true;
while(w&&w.frameElement){const f=w.frameElement;const r=f.getBoundingClientRect();const c=f.ownerDocument.defaultView.getComputedStyle(f);ox+=r.left+f.clientLeft+(parseFloat(c.paddingLeft)||0);oy+=r.top+f.clientTop+(parseFloat(c.paddingTop)||0);w=f.ownerDocument.defaultView;}
const win=el.ownerDocument.defaultView;const topWin=w||win;
const inside=h=>{for(let i=0;h&&i<400;i++){if(h===el)return true;h=h.parentNode||h.host;}return false;};
const hit=(x,y)=>{if(x<0||y<0||x>=win.innerWidth||y>=win.innerHeight)return false;const root=el.getRootNode();const h=(root.elementFromPoint?root:el.ownerDocument).elementFromPoint(x,y);if(!h)return false;if(inside(h))return true;
 if(h.localName==='label'&&h.control===el)return true;return false;};
const parentHit=(x,y)=>{let w=win;while(w&&w.frameElement){const f=w.frameElement,r=f.getBoundingClientRect(),p=f.ownerDocument.defaultView,c=p.getComputedStyle(f);x+=r.left+f.clientLeft+(parseFloat(c.paddingLeft)||0);y+=r.top+f.clientTop+(parseFloat(c.paddingTop)||0);const h=f.ownerDocument.elementFromPoint(x,y);if(h!==f&&!f.contains(h))return false;w=p;}return true;};
const pts=[];const b=el.getBoundingClientRect();if(b.width>0&&b.height>0)pts.push([b.left+b.width/2,b.top+b.height/2]);
for(const r of el.getClientRects()){if(r.width>0&&r.height>0){pts.push([r.left+r.width/2,r.top+r.height/2]);break;}}
for(const p of pts){const tx=p[0]+ox,ty=p[1]+oy;if(hit(p[0],p[1])&&parentHit(p[0],p[1])&&tx>=0&&ty>=0&&tx<topWin.innerWidth&&ty<topWin.innerHeight){out.x=tx;out.y=ty;out.how='mouse';return out;}}
if(mode==='click'){el.click();out.how='js';}
else if(mode==='fill'){try{el.focus({preventScroll:true});}catch(e){/* the page forbids or lacks it: the step is skipped, the caller reads the outcome */}out.how='focus';}
else out.how='none';
return out;}";
    // (n, g, clear) -> {a: the element has the focus, len: characters in it}. Selects everything (clear) or puts the caret at the end.
    public const string FocusSelect=@"function(n,g,clear){
const S=globalThis.__sa;if(!S||S.g!==g)return {e:'stale'};const el=S.m.get(n);if(!el||!el.isConnected)return {e:'stale'};
const act=()=>{let a=el.ownerDocument.activeElement;while(a&&a.shadowRoot&&a.shadowRoot.activeElement)a=a.shadowRoot.activeElement;return a;};
if(act()!==el){try{el.focus({preventScroll:true});}catch(e){/* the page forbids or lacks it: the step is skipped, the caller reads the outcome */}}
if(el.disabled||el.readOnly||el.getAttribute('aria-disabled')==='true'||act()!==el)return {a:false,len:0};
let len=0;
if(el.isContentEditable){len=(el.innerText||'').replace(/\n$/,'').length;const d=el.ownerDocument;const r=d.createRange();r.selectNodeContents(el);if(!clear)r.collapse(false);const s=d.defaultView.getSelection();s.removeAllRanges();s.addRange(r);}
else if('value' in el){len=String(el.value).length;try{if(clear)el.select();else el.setSelectionRange(len,len);}catch(e){/* the page forbids or lacks it: the step is skipped, the caller reads the outcome */}}
const a=act();return {a:a===el||(!!a&&el.contains(a)),len:len};}";
    public const string FocusProof=@"function(n,g){
const S=globalThis.__sa;if(!S||S.g!==g)return {a:false};const el=S.m.get(n);if(!el||!el.isConnected||el.disabled||el.readOnly)return {a:false};
let a=el.ownerDocument.activeElement;while(a&&a.shadowRoot&&a.shadowRoot.activeElement)a=a.shadowRoot.activeElement;
let w=el.ownerDocument.defaultView;while(w&&w.frameElement){const f=w.frameElement;if(f.ownerDocument.activeElement!==f)return {a:false};w=f.ownerDocument.defaultView;}
return {a:a===el};}";
    // (n, g) -> {v: value (password: null), len, pw}
    public const string ReadValue=@"function(n,g){
const S=globalThis.__sa;if(!S||S.g!==g)return {e:'stale'};const el=S.m.get(n);if(!el||!el.isConnected)return {e:'stale'};
const pw=el.localName==='input'&&el.type==='password';
const v=el.isContentEditable?String(el.innerText||''):('value' in el?String(el.value):String(el.innerText||''));
return {v:pw?null:v.slice(0,60000),len:v.length,pw:pw};}";
    // (n, g, text): the value through the prototype's own setter plus input and change events. For fields Input.insertText cannot type into (date, range, color) and pages that drop typed text.
    // Called from the isolated world, so a framework's own property on the element (React's value tracker lives in the page's world) is not in the way.
    public const string SetValue=@"function(n,g,text){
const S=globalThis.__sa;if(!S||S.g!==g)return {e:'stale'};const el=S.m.get(n);if(!el||!el.isConnected)return {e:'stale'};
if(!('value' in el)||el.disabled||el.readOnly||el.getAttribute('aria-disabled')==='true'||!['input','textarea'].includes(el.localName))return {ok:false};
let p=Object.getPrototypeOf(el),d=null;for(let i=0;p&&i<8&&!d;i++){d=Object.getOwnPropertyDescriptor(p,'value');p=Object.getPrototypeOf(p);}
try{if(d&&d.set)d.set.call(el,text);else el.value=text;}catch(e){/* a field that refuses a value: reported as not set */return {ok:false};}
el.dispatchEvent(new Event('input',{bubbles:true,composed:true}));el.dispatchEvent(new Event('change',{bubbles:true}));
return {ok:true};}";
    // (n, g, text): a <select> by the option's label, then its value, then a label that contains the text.
    public const string SelectOption=@"function(n,g,text){
const S=globalThis.__sa;if(!S||S.g!==g)return {e:'stale'};const el=S.m.get(n);if(!el||!el.isConnected)return {e:'stale'};
if(el.disabled||el.getAttribute('aria-disabled')==='true')return {ok:false};
const t=String(text).trim().toLowerCase();const os=Array.from(el.options||[]);
const lab=o=>String(o.label||o.text||'').trim();
let o=os.find(o=>lab(o).toLowerCase()===t)||os.find(o=>String(o.value).toLowerCase()===t)||os.find(o=>t&&lab(o).toLowerCase().includes(t));
if(!o)return {ok:false,options:os.slice(0,30).map(lab)};
el.selectedIndex=o.index;el.dispatchEvent(new Event('input',{bubbles:true}));el.dispatchEvent(new Event('change',{bubbles:true}));
return {ok:true,label:lab(o)};}";
    // (max) -> {t,u,x,len}
    public const string Read="function(max){const b=document.body;const x=b?String(b.innerText||''):'';return {t:document.title,u:location.href,x:x.slice(0,max),len:x.length};}";
    public const string State="function(){return {r:document.readyState,u:location.href,t:document.title,v:document.visibilityState,f:document.hasFocus()};}";
    // (x, y) page coordinates of a full-page picture -> viewport coordinates, scrolling the point into view when it is outside.
    public const string PagePoint="function(x,y){if(y-scrollY<0||y-scrollY>=innerHeight||x-scrollX<0||x-scrollX>=innerWidth)scrollTo({left:Math.max(0,x-innerWidth/2),top:Math.max(0,y-innerHeight/2),behavior:'instant'});return {x:x-scrollX,y:y-scrollY};}";
}
