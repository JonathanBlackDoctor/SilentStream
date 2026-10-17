/* Shared PC-direct and room-controller view; all filenames rendered as text. */
(function (global) {
  function mount(host, adapter) {
    if (!host) return;
    const el = (tag, text) => { const node = document.createElement(tag); if (text) node.textContent = text; return node; };
    const card = el("details"); card.className = "section-card card"; card.style.marginTop = "16px";
    card.append(el("summary", "USB 자료"));
    const note = el("p", "열거나 복사한 PPT·PPTX·PDF를 PC에 보관합니다. 미리보기도 포함될 수 있습니다.");
    note.className = "meta muted"; card.append(note);
    const status = el("p"); status.setAttribute("aria-live","polite"); card.append(status);
    const controls = el("div"); controls.style.cssText="display:flex;gap:8px;flex-wrap:wrap;align-items:center";
    const date = el("input"); date.type="date"; date.setAttribute("aria-label","보관 날짜");
    date.value = new Date(Date.now()+9*3600000).toISOString().slice(0,10);
    const refresh = el("button","새로고침"), toggle=el("button","자동 보관 설정");
    controls.append(date,refresh,toggle); card.append(controls);
    const rows=el("div"),paging=el("div"),previous=el("button","이전"),next=el("button","다음"),count=el("span");
    paging.style.cssText="display:flex;gap:12px;align-items:center;margin-top:12px";
    paging.append(previous,count,next); card.append(rows,paging); host.append(card);
    let data=null,busy=false,skip=0,key="",generation=0,failedKey="";
    function context() {
      const current=adapter.context();
      if (current?.key!==key) { key=current?.key||"";generation++;data=null;skip=0;failedKey="";rows.replaceChildren(); }
      return current;
    }
    function connection() {
      const c=context(),ready=!!(c?.online&&c.supported);
      host.hidden=!c;
      refresh.disabled=toggle.disabled=date.disabled=previous.disabled=next.disabled=!ready||busy;
      if(!c)return false;
      if(!c.online)status.textContent="PC가 오프라인입니다. 연결 후 목록과 다운로드를 사용할 수 있습니다.";
      else if(!c.supported)status.textContent="USB 자료 기능을 사용하려면 PC 앱과 연결 도우미를 업데이트하세요.";
      return ready;
    }
    async function load(manual=false) {
      if(manual)failedKey="";
      if(!connection()||busy||(!manual&&failedKey===key))return;
      const c=context(),version=generation;
      busy=true;connection();
      try{
        const value=await adapter.list(c,date.value,skip);
        if(context()?.key!==c.key||version!==generation)return;
        data=value;render(c);
      }catch(error){if(context()?.key===c.key){status.textContent=error.message;failedKey=c.key;}}
      finally{busy=false;connection();if(data){previous.disabled=skip===0;next.disabled=skip+100>=data.total;}}
    }
    function render(c){
      status.textContent=data.detectorStatus+(data.dropped?" · 감지 누락 "+data.dropped+"건":"");
      toggle.textContent=data.enabled?"자동 보관 끄기":"자동 보관 켜기";
      rows.replaceChildren();count.textContent=data.total+"개";
      if(!data.items?.length)rows.append(el("p","이 날짜에 보관한 자료가 없습니다."));
      const states={queued:"대기",copying:"복사 중",ready:"보관됨",duplicate:"중복 내용",failed:"실패"};
      for(const item of data.items||[]){
        const row=el("div");row.style.cssText="border-bottom:1px solid #bbb6;padding:12px 0;overflow-wrap:anywhere";
        row.append(el("strong",item.name),el("p",(states[item.status]||item.status)+" · "+
          (item.copiedBytes/1048576).toFixed(1)+"/"+(item.size/1048576).toFixed(1)+" MiB"+(item.error?" · "+item.error:"")));
        if(item.status==="ready"||item.status==="duplicate"){
          const download=el("button","다운로드");
          download.onclick=async()=>{
            if(context()?.key!==c.key||!connection())return;
            download.disabled=true;
            try {
              const url=await adapter.download(c,item.id);
              if(context()?.key!==c.key)return;
              const link=el("a");link.href=url;link.rel="noreferrer";link.download=item.name;
              document.body.append(link);link.click();link.remove();
            }catch(error){status.textContent=error.message;}finally{download.disabled=false;}
          };row.append(download);
        }else if(item.status==="failed"){
          const retry=el("button","재시도");
          retry.onclick=async()=>{if(context()?.key!==c.key||!connection())return;try{await adapter.change(c,{operation:"retry",id:item.id});await load(true);}
            catch(error){status.textContent=error.message;}};row.append(retry);
        }
        rows.append(row);
      }
    }
    toggle.onclick=async()=>{
      if(!data||!connection()||busy)return;
      const c=context();toggle.disabled=true;
      try{await adapter.change(c,{operation:"settings",enabled:!data.enabled});await load(true);}
      catch(error){status.textContent=error.message;}finally{connection();}
    };
    date.onchange=()=>{skip=0;load(true);};refresh.onclick=()=>load(true);
    previous.onclick=()=>{skip=Math.max(0,skip-100);load(true);};next.onclick=()=>{skip+=100;load(true);};
    card.ontoggle=()=>{if(card.open)load(true);};
    const timer=setInterval(()=>{const ready=connection();if(ready&&card.open&&!document.hidden)load();},5000);
    global.addEventListener("pagehide",()=>clearInterval(timer),{once:true});
    connection();
  }
  global.SilentStreamUsbDocuments={mount};
})(window);
