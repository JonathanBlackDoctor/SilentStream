/* Shared PC-direct and room-controller view; all filenames rendered as text. */
(function (global) {
  function mount(host, adapter) {
    if (!host) return;
    const el = (tag, text) => { const node = document.createElement(tag); if (text) node.textContent = text; return node; };
    if (!document.getElementById("usb-documents-style")) {
      const style=el("style");style.id="usb-documents-style";
      style.textContent='.usb-documents>.fold-body{padding:0 20px 20px}.usb-documents .usb-controls{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px;align-items:center}.usb-documents .usb-controls input{grid-column:1/-1;width:100%;min-width:0}.usb-documents button,.usb-documents input{width:100%;min-width:0;min-height:44px;margin:0;font:inherit;font-size:12px}.usb-documents .usb-paging{display:grid;grid-template-columns:minmax(0,1fr) auto minmax(0,1fr);gap:12px;align-items:center;margin-top:16px}.usb-documents .usb-row{border-bottom:1px solid var(--line,#bbb6);padding:16px 0;overflow-wrap:anywhere}.usb-documents .usb-row strong{font-size:13px}.usb-documents .usb-row p,.usb-documents .usb-status{font-size:12px;line-height:1.7;color:var(--muted)}.usb-documents .usb-row button{width:auto;min-width:100px}.usb-documents .meta{line-height:1.7}.usb-documents summary:focus-visible,.usb-documents button:focus-visible,.usb-documents input:focus-visible{outline:3px solid #7fae99;outline-offset:3px}';
      document.head.append(style);
    }
    const card = el("details"); card.className = "section-card card usb-documents"; card.style.marginTop = "16px";
    card.append(el("summary", "USB 자료"));
    const body=el("div");body.className="fold-body";card.append(body);
    const note = el("p", "열거나 복사한 PPT·PPTX·PDF를 PC에 보관합니다. 미리보기도 포함될 수 있습니다. 로컬 보관본은 7일 후 자동 삭제하며 Drive 파일은 유지합니다. PC 앱 0.4.15 이상에서 적용됩니다.");
    note.className = "meta muted"; body.append(note);
    const driveLink=el("a","Google Drive 자료 열기");driveLink.target="_blank";driveLink.rel="noopener noreferrer";driveLink.hidden=true;
    driveLink.style.cssText="display:none;padding:12px;border-radius:8px;background:#e5f0ff;color:#174f96;text-decoration:none;font-size:13px;font-weight:bold";
    const drivePending=el("button","Google Drive 자료 열기");drivePending.disabled=true;drivePending.hidden=!adapter.drive;
    const driveNote=el("p");driveNote.className="usb-status";body.append(driveLink,drivePending,driveNote);
    let driveKey="",driveBusy=false,driveAt=0;
    async function loadDrive(manual=false){
      if(!adapter.drive)return;
      const c=context();if(!c||driveBusy||!manual&&driveKey===c.key&&Date.now()-driveAt<30000)return;
      driveBusy=true;const version=generation;
      try{
        const value=await adapter.drive(c);
        if(context()?.key!==c.key||version!==generation)return;
        driveKey=c.key;driveAt=Date.now();
        drivePending.hidden=!!value.url;driveLink.hidden=!value.url;driveLink.style.display=value.url?"inline-block":"none";
        if(value.url)driveLink.href=value.url;else driveLink.removeAttribute("href");
        driveNote.textContent=(value.readerEmail?"자료 담당자: "+value.readerEmail+" · ":"")+value.message;
      }catch(error){if(context()?.key===c.key)driveNote.textContent=error.message;}
      finally{driveBusy=false;}
    }
    const status = el("p"); status.className="usb-status";status.setAttribute("aria-live","polite"); body.append(status);
    const controls = el("div"); controls.className="usb-controls";
    const date = el("input"); date.type="date"; date.setAttribute("aria-label","보관 날짜");
    date.value = new Date(Date.now()+9*3600000).toISOString().slice(0,10);
    const refresh = el("button","새로고침"), toggle=el("button","자동 보관 설정");
    controls.append(date,refresh,toggle); body.append(controls);
    const rows=el("div"),paging=el("div"),previous=el("button","이전"),next=el("button","다음"),count=el("span");
    paging.className="usb-paging";
    paging.append(previous,count,next); body.append(rows,paging); host.append(card);
    let data=null,busy=false,skip=0,key="",generation=0,failedKey="";
    function context() {
      const current=adapter.context();
      if (current?.key!==key) { key=current?.key||"";generation++;driveKey="";drivePending.hidden=!adapter.drive;driveLink.hidden=true;driveLink.style.display="none";driveLink.removeAttribute("href");driveNote.textContent="";data=null;skip=0;failedKey="";rows.replaceChildren();count.textContent=""; }
      return current;
    }
    function connection() {
      const c=context(),ready=!!(c?.online&&c.supported);
      host.hidden=!c;
      refresh.disabled=toggle.disabled=date.disabled=previous.disabled=next.disabled=!ready||busy;
      toggle.disabled=toggle.disabled||!data;
      previous.disabled=previous.disabled||!data||skip===0;
      next.disabled=next.disabled||!data||skip+100>=data.total;
      for(const button of rows.querySelectorAll('button'))button.disabled=!ready||busy;
      if(!c)return false;
      if(!c.online)status.textContent="PC가 오프라인입니다. 연결 후 목록과 다운로드를 사용할 수 있습니다.";
      else if(!c.supported)status.textContent="USB 자료 기능을 사용하려면 PC 앱과 연결 도우미를 업데이트하세요.";
      return ready;
    }
    async function load(manual=false) {
      loadDrive(manual);
      if(manual)failedKey="";
      if(!connection()||busy||(!manual&&failedKey===key))return;
      const c=context(),version=generation;
      busy=true;connection();
      try{
        const value=await adapter.list(c,date.value,skip);
        if(context()?.key!==c.key||version!==generation)return;
        data=value;render(c);
      }catch(error){if(context()?.key===c.key){status.textContent=error.message;failedKey=c.key;}}
      finally{busy=false;connection();}
    }
    function render(c){
      status.textContent=data.detectorStatus+(data.dropped?" · 감지 누락 "+data.dropped+"건":"");
      toggle.textContent=data.enabled?"자동 보관 끄기":"자동 보관 켜기";
      rows.replaceChildren();count.textContent=data.total+"개";
      if(!data.items?.length)rows.append(el("p","이 날짜에 보관한 자료가 없습니다."));
      const states={queued:"대기",copying:"복사 중",ready:"보관됨",duplicate:"중복 내용",failed:"실패"};
      for(const item of data.items||[]){
        const row=el("div");row.className="usb-row";
        row.append(el("strong",item.name),el("p",(states[item.status]||item.status)+" · "+
          (item.copiedBytes/1048576).toFixed(1)+"/"+(item.size/1048576).toFixed(1)+" MiB"+(item.error?" · "+item.error:"")));
        if(item.status==="ready"||item.status==="duplicate"){
          const download=el("button","다운로드");
          download.onclick=async()=>{
            if(context()?.key!==c.key||!connection())return;
            busy=true;connection();
            try {
              const url=await adapter.download(c,item.id);
              if(context()?.key!==c.key)return;
              const link=el("a");link.href=url;link.rel="noreferrer";link.download=item.name;
              document.body.append(link);link.click();link.remove();
            }catch(error){status.textContent=error.message;}finally{busy=false;connection();}
          };row.append(download);
        }else if(item.status==="failed"){
          const retry=el("button","재시도");
          retry.onclick=async()=>{if(context()?.key!==c.key||!connection()||busy)return;
            busy=true;connection();let changed=false;
            try{await adapter.change(c,{operation:"retry",id:item.id});changed=true;}
            catch(error){status.textContent=error.message;}finally{busy=false;connection();}
            if(changed&&context()?.key===c.key)await load(true);};row.append(retry);
        }
        rows.append(row);
      }
    }
    toggle.onclick=async()=>{
      if(!data||!connection()||busy)return;
      const c=context();busy=true;connection();let changed=false;
      try{await adapter.change(c,{operation:"settings",enabled:!data.enabled});changed=true;}
      catch(error){status.textContent=error.message;}finally{busy=false;connection();}
      if(changed&&context()?.key===c.key)await load(true);
    };
    date.onchange=()=>{skip=0;load(true);};refresh.onclick=()=>load(true);
    previous.onclick=()=>{skip=Math.max(0,skip-100);load(true);};next.onclick=()=>{skip+=100;load(true);};
    card.ontoggle=()=>{if(card.open)load(true);};
    const timer=setInterval(()=>{const ready=connection();if(card.open&&!document.hidden){loadDrive();if(ready)load();}},5000);
    global.addEventListener("pagehide",()=>clearInterval(timer),{once:true});
    connection();
  }
  global.SilentStreamUsbDocuments={mount};
})(window);
