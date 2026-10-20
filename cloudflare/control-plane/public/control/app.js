
const shutdownDrafts={};
function shutdownTarget(value){
  if(!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value))throw Error('종료 날짜와 시각을 선택하세요.');
  const date=new Date(value+':00+09:00'),ms=date.getTime();
  if(!Number.isFinite(ms)||new Date(ms+9*3600000).toISOString().slice(0,16)!==value)throw Error('올바른 날짜와 시각을 선택하세요.');
  if(ms-Date.now()<180000||ms-Date.now()>30*86400000)throw Error('현재부터 3분 이후, 30일 이내의 시각을 선택하세요.');
  return date.toISOString();
}
function shutdownPanel(value){
  const app=value.app||{},state=app.shutdownSchedule,room=value.roomId,cloud=value.cloudShutdownRequest;
  if(value.role!=="admin")return "";
  const supported=app.shutdownScheduleControl&&app.remoteShutdownScheduleCommands;
  const localActive=state&&(["pending","executing"].includes(state.status)||state.status==="scheduled"&&new Date(state.targetUtc)>new Date());
  const cloudActive=cloud&&["queued","dispatching"].includes(cloud.state);
  const labels={pending:"PC 예약됨",executing:"녹화 마감·안전 점검 중",scheduled:"예약 시각까지 종료 대기",cancelled:"취소됨",failed:"실패·확인 필요",missed:"예약 시각 지남"};
  const cloudLabels={queued:"PC 연결 후 등록 대기",dispatching:"PC 전달 중 · 결과 확인 필요",applied:"PC 등록 요청 전달 완료",cancelled:"클라우드 요청 취소됨",expired:"등록 기한 만료",failed:"등록 실패 · 상태 확인 필요"};
  const disabled=value.registered===false?"disabled":"",createDisabled=disabled||cloudActive||localActive||value.online&&!supported?"disabled":"";
  const format=value=>new Date(value).toLocaleString("ko-KR",{timeZone:"Asia/Seoul"});
  const cloudInfo=cloud?'<p class="meta" role="status"><b>'+esc(cloudLabels[cloud.state]||cloud.state)+'</b> · '+esc(format(cloud.targetUtc))+'<br>'+esc(cloud.message)+'</p>':"";
  return '<section class="section-card shutdown"><h2>PC 전원</h2><p class="meta">한국 시간 · 1회 예약. PC가 꺼져 있어도 요청을 저장할 수 있습니다.</p>'+cloudInfo+
    (state?'<p class="meta" role="status">마지막 PC 기록: '+esc(labels[state.status]||state.status)+' · '+esc(format(state.targetUtc))+'<br>'+esc(state.message)+'</p>':"")+
    (!supported&&value.online?'<p class="meta">PC 앱과 연결 도우미 업데이트 후 PC 예약을 사용할 수 있습니다.</p>':"")+
    '<div class="actions"><button class="danger" '+(value.online&&app.quietShutdownControl&&!localActive&&!cloudActive&&!app.windowsShutdownPending?"":"disabled")+' data-action="system.shutdown" data-room="'+esc(room)+'">지금 PC 종료</button></div>'+
    '<label for="shutdown-'+esc(room)+'">종료 날짜·시각 (한국 시간)</label><input style="display:block;width:100%;margin:10px 0" id="shutdown-'+esc(room)+'" data-shutdown-input="'+esc(room)+'" type="datetime-local" value="'+esc(shutdownDrafts[room]||"")+'" '+disabled+'>'+
    '<div class="actions"><button '+createDisabled+' data-shutdown-create="'+esc(room)+'">'+(value.online?"종료 예약":"연결 후 종료 예약 등록")+'</button>'+
    (cloud?.state==="queued"?'<button '+disabled+' data-shutdown-cancel="'+esc(room)+'" data-schedule-id="'+esc(cloud.id)+'">연결 대기 요청 취소</button>':localActive?'<button '+(value.online&&supported?"":"disabled")+' data-shutdown-cancel="'+esc(room)+'" data-schedule-id="'+esc(state.id)+'">PC 예약 취소</button>':"")+
    '</div><p class="meta">오프라인 요청은 아직 PC에 등록된 예약이 아닙니다. 종료 3분 전까지 연결되지 않으면 만료됩니다. PC 등록 후에는 휴대폰을 닫아도 실행하며, 종료 2분 전부터 녹화·방송을 마감합니다. 예약 시각에 별도 대기 안내 없이 종료합니다. 안전 점검 실패 시 종료하지 않습니다.</p></section>';
}
async function changeShutdown(room,id){
  try{
    const targetUtc=id?null:shutdownTarget($('shutdown-'+room).value);
    const question=id?'PC 종료 예약을 취소할까요?':new Date(targetUtc).toLocaleString('ko-KR',{timeZone:'Asia/Seoul'})+' (한국 시간)에 PC를 종료할까요? 종료 2분 전부터 녹화와 방송을 마감합니다.';
    if(!confirm(room.toUpperCase()+': '+question))return;
    const action=id?'system.shutdown.schedule.cancel':'system.shutdown.schedule';
    const result=await api('/api/control/rooms/'+encodeURIComponent(room)+'/actions/'+action,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(id?{id}:{targetUtc})});
    let local={};try{local=JSON.parse(result.body||'{}')}catch{}
    show(local.message||result.error||(result.ok?'예약 요청을 처리했습니다.':'예약 처리에 실패했습니다.'),!result.ok);
    if(result.ok)await load();
  }catch(error){if(error.message!=='AUTH_REQUIRED')show(error.message,true)}
}

const $=id=>document.getElementById(id),roomsEl=$("rooms"),notice=$("notice"),detail=$("detail");
const roomIds=["m111","m112","m113","baek202"];
let session=null,devices=[];
function esc(value){return String(value??"").replace(/[&<>"']/g,character=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[character]))}
function roomName(room){return session?.roomNames?.[room]||room.toUpperCase()}
function show(message,error=false){notice.textContent=message;notice.className="notice show";notice.style.background=error?"#fee6e4":"#e5f0ff";notice.style.color=error?"#9e2822":"#174f96";setTimeout(()=>notice.className="notice",5000)}
function summary(device){const app=device.app||{},audio=device.audio||{};const state=app.state||app.streamState||app.status||"상태 수집 대기";return `${state}${audio.micWarning||audio.warning?" · 마이크 확인 필요":""}`}
function render(){if(!devices.length){roomsEl.innerHTML='<div class="empty">배정된 강의실이 없습니다.</div>';return}roomsEl.innerHTML=devices.map(device=>{const admin=device.role==="admin",last=device.lastSeenAtUtc?new Date(device.lastSeenAtUtc).toLocaleString():"접속 기록 없음",display=roomName(device.roomId),roomApp=session?.roomApps?.[device.roomId]||"";return `<article class="card"><div class="top"><div class="room">${esc(display)}</div><span class="pill ${device.online?"on":"off"}">${device.online?"온라인":"오프라인"}</span></div><div class="summary">${esc(device.registered?summary(device):"설치되지 않음")}</div><div class="meta">${esc(device.machineName||"기기 미등록")}<br>마지막 접속: ${esc(last)}</div><div class="actions"><a class="btn" href="/install/${encodeURIComponent(device.roomId)}">강의실 PC 설치</a>${roomApp?`<a class="btn" href="${esc(roomApp)}" target="_blank" rel="noopener">${esc(display)} 담당자 앱</a>`:""}<button data-detail="${esc(device.roomId)}">상세</button><button class="primary" ${device.online?"":"disabled"} data-action="live.start" data-room="${esc(device.roomId)}">송출 시작</button><button ${device.online?"":"disabled"} data-action="live.retry" data-room="${esc(device.roomId)}">재연결</button><button ${device.online?"":"disabled"} data-action="inspection.rerun" data-room="${esc(device.roomId)}">자동 점검</button><button ${device.online?"":"disabled"} data-action="app.restart" data-room="${esc(device.roomId)}">앱 재시작</button><button class="warn" ${device.online?"":"disabled"} data-action="live.stopBroadcast" data-room="${esc(device.roomId)}" data-confirm="방송만 중지할까요?">방송만 중지</button>${admin?`<button class="danger" ${device.online?"":"disabled"} data-action="live.stopAll" data-room="${esc(device.roomId)}" data-confirm="앱과 방송을 모두 중지할까요?">전체 중지</button>`:""}</div>${shutdownPanel(device)}</article>`}).join("")}
async function api(path,options){const response=await fetch(path,options);if(response.status===401){location.replace(`/auth/login?returnTo=${encodeURIComponent(location.pathname+location.search)}`);throw Error("AUTH_REQUIRED")}let data=null;try{data=await response.json()}catch{}if(!response.ok){let local;try{local=JSON.parse(data?.body||"{}")}catch{}throw Error(local?.message||data?.message||data?.error||`HTTP ${response.status}`);}return data}
async function load(){try{session=await api("/api/control/session");document.body.classList.remove("auth-pending");$("who").textContent=`${session.email} · ${session.admin?"관리자":"호실 담당자"}`;devices=await api("/api/control/rooms");render();if(session.admin)await loadMembers()}catch(error){if(error.message!=="AUTH_REQUIRED"){document.body.classList.remove("auth-pending");roomsEl.innerHTML=`<div class="empty">${esc(error.message)}</div>`}}}
const dangerousActions={
  "system.shutdown":"지금 PC를 종료할까요? 녹화와 방송을 저장·마감하고 안전 점검 후 종료합니다. 실행 후에는 취소할 수 없으며, 다른 프로그램의 저장하지 않은 내용은 사라질 수 있습니다.",
  "app.restart":"PC 앱을 재시작할까요? 진행 중인 방송과 녹화가 잠시 중단될 수 있습니다.",
  "live.stopBroadcast":"방송만 중지할까요? 백업 녹화는 계속됩니다.",
  "live.stopAll":"방송과 녹화를 모두 중지할까요?",
  "live.retry":"방송을 재연결할까요? 송출이 잠시 중단될 수 있습니다.",
  "inspection.rerun":"자동 점검을 다시 실행할까요? PC 상태에 따라 복구 작업이 실행될 수 있습니다."
};
async function act(room,action,confirmation){confirmation=dangerousActions[action]||confirmation;if(confirmation&&!confirm(`${roomName(room)}: ${confirmation}`))return;try{const result=await api(`/api/control/rooms/${encodeURIComponent(room)}/actions/${encodeURIComponent(action)}`,{method:"POST",headers:{"content-type":"application/json"},body:"{}"});show(result.ok?"명령을 실행했습니다.":result.error||"명령을 완료하지 못했습니다.",!result.ok);setTimeout(load,700)}catch(error){show(error.message,true)}}
function showDetails(room){const device=devices.find(value=>value.roomId===room);$("detailTitle").textContent=`${roomName(room)} 상세 상태`;$("detailBody").textContent=JSON.stringify(device,null,2);detail.showModal()}
async function loadMembers(){await loadDriveFolders();const members=await api("/api/control/members");$("members").classList.add("show");$("roomChecks").innerHTML=roomIds.map(room=>`<label><input type="checkbox" value="${room}" checked> ${roomName(room)}</label>`).join("");$("memberList").innerHTML=members.map(member=>`<div class="member-row"><span><b>${esc(member.email)}</b> · ${member.role==="admin"?"관리자":member.rooms.map(room=>roomName(room)).join(", ")}</span>${member.email===session.email&&session.owner?"<span>최초 소유자</span>":`<button class="danger" data-delete="${encodeURIComponent(member.email)}">삭제</button>`}</div>`).join("")}
roomsEl.addEventListener("click",event=>{const button=event.target.closest("button");if(!button)return;if(button.dataset.detail)showDetails(button.dataset.detail);if(button.dataset.action)act(button.dataset.room,button.dataset.action,button.dataset.confirm)});
$("members").addEventListener("click",async event=>{const button=event.target.closest("button[data-delete]");if(!button||!confirm("이 사용자의 권한을 삭제할까요?"))return;try{await api(`/api/control/members/${button.dataset.delete}`,{method:"DELETE"});await loadMembers()}catch(error){show(error.message,true)}});
$("memberRole").addEventListener("change",()=>{$("roomChecks").style.opacity=$("memberRole").value==="admin"?".45":"1"});
$("memberForm").addEventListener("submit",async event=>{event.preventDefault();if(!confirm("이 사용자의 호실·관리자 권한을 저장할까요?"))return;const role=$("memberRole").value,rooms=[...$("roomChecks").querySelectorAll("input:checked")].map(input=>input.value);try{await api("/api/control/members",{method:"POST",headers:{"content-type":"application/json"},body:JSON.stringify({email:$("memberEmail").value,role,rooms})});$("memberEmail").value="";show("권한을 저장했습니다.");await loadMembers()}catch(error){show(error.message,true)}});
$("closeDetail").addEventListener("click",()=>detail.close());
addEventListener("pagehide",()=>document.body.classList.add("auth-pending"));
addEventListener("pageshow",event=>{if(event.persisted)location.reload()});
async function loadDriveFolders(){
  $("driveSettings")?.classList.add("show");const host=$("driveFolders");if(!host||host.querySelector(":focus"))return;
  const values=await Promise.all(roomIds.map(room=>api("/api/control/rooms/"+room+"/usb-drive")));
  host.replaceChildren();
  for(const value of values){
    const form=document.createElement("form");form.className="drive-form";form.dataset.driveRoom=value.roomId;
    const title=document.createElement("b");title.textContent=roomName(value.roomId);
    const account=document.createElement("input");account.type="email";account.required=true;account.name="account";account.placeholder="공용 소유 계정";account.setAttribute("aria-label",roomName(value.roomId)+" 공용 소유 계정");account.value=value.account||"";
    const folder=document.createElement("input");folder.required=true;folder.name="folderId";folder.placeholder="기기 폴더 ID";folder.setAttribute("aria-label",roomName(value.roomId)+" Drive 폴더 ID");folder.value=value.folderId||"";
    const reader=document.createElement("input");reader.type="email";reader.name="readerEmail";reader.placeholder="담당자 미정";reader.setAttribute("aria-label",roomName(value.roomId)+" 자료 담당자");reader.value=value.readerEmail||"";
    if(value.revision){account.readOnly=folder.readOnly=true;}
    const save=document.createElement("button");save.type="submit";save.textContent="자료 담당자 저장";
    const state=document.createElement("p");state.textContent=value.message;state.style.gridColumn="1/-1";
    form.append(title,account,folder,reader,save,state);
    form.onsubmit=async event=>{
      event.preventDefault();save.disabled=true;
      try{await api("/api/control/rooms/"+value.roomId+"/usb-drive",{method:"PUT",headers:{"content-type":"application/json"},
        body:JSON.stringify({account:account.value,folderId:folder.value,readerEmail:reader.value})});
        show("자료 담당자를 저장했습니다. PC에서 Google 권한 확인 후 바로가기가 활성화됩니다.");
        save.disabled=false;save.focus();save.blur();await loadDriveFolders();
      }catch(error){show(error.message,true);save.disabled=false;}
    };
    host.append(form);
  }
}

load();setInterval(()=>{if(!document.activeElement?.dataset?.shutdownInput)load()},10000);

roomsEl.addEventListener('input',event=>{if(event.target.dataset.shutdownInput)shutdownDrafts[event.target.dataset.shutdownInput]=event.target.value});
roomsEl.addEventListener('click',event=>{const button=event.target.closest('button');if(button?.dataset.shutdownCreate)changeShutdown(button.dataset.shutdownCreate,null);else if(button?.dataset.shutdownCancel)changeShutdown(button.dataset.shutdownCancel,button.dataset.scheduleId)});
