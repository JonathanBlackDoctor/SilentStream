import { FEED_URL, selectRecent } from './data.mjs';
const list = document.querySelector('#playlists'), status = document.querySelector('#status');
const copyStatus = document.querySelector('#copy-status');
const copyDialog = document.querySelector('#copy-dialog'), copyUrl = document.querySelector('#copy-url');
let loading = false, current = null, renderedSignature = null;
const playIcon = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 2.3v11.4L13 8z"/></svg>';
const copyIcon = '<svg viewBox="0 0 20 20" aria-hidden="true"><rect x="7" y="7" width="10" height="11" rx="2"/><path d="M12 7V4a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h3"/></svg>';
async function copyLink(item, button) {
  if (button.disabled) return;
  button.disabled = true;
  copyStatus.textContent = '';
  try {
    await navigator.clipboard.writeText(item.url);
    button.querySelector('span').textContent = '복사됨';
    button.setAttribute('aria-label', `${item.date} 재생목록 링크 복사됨`);
    button.classList.add('copied');
    copyStatus.textContent = `${item.date} 재생목록 링크를 복사했습니다.`;
    setTimeout(() => {
      button.querySelector('span').textContent = '복사';
      button.setAttribute('aria-label', `${item.date} 재생목록 링크 복사`);
      button.classList.remove('copied');
    }, 2200);
  } catch {
    copyUrl.value = item.url;
    copyDialog.showModal();
    copyUrl.focus(); copyUrl.select();
    copyStatus.textContent = '자동 복사가 허용되지 않았습니다. 링크를 직접 복사해 주세요.';
  } finally { button.disabled = false; }
}
function render(payload) {
  const items = selectRecent(payload);
  // Keep a focused control in place when the periodic feed returns the same links.
  const signature = JSON.stringify(items);
  if (signature === renderedSignature) {
    current = payload; return;
  }
  list.replaceChildren();
  for (const [index, item] of items.entries()) {
    const date = new Date(item.date + 'T12:00:00+09:00');
    const row = document.createElement('article'); row.className = 'playlist' + (index === 0 ? ' featured' : '');
    const day = new Intl.DateTimeFormat('ko-KR', {weekday:'long',timeZone:'Asia/Seoul'}).format(date);
    row.innerHTML = `<div class="date"><time datetime="${item.date}">${item.date.slice(5).replace('-','.')}</time><div class="date-detail"><span>${day}</span>${index === 0 ? '<span class="latest">최신</span>' : ''}</div></div><div class="actions"></div>`;
    const actions = row.querySelector('.actions');
    const copy = document.createElement('button'); copy.type = 'button'; copy.className = 'copy';
    copy.innerHTML = `${copyIcon}<span>복사</span>`;
    copy.setAttribute('aria-label', `${item.date} 재생목록 링크 복사`);
    copy.addEventListener('click', () => copyLink(item, copy));
    const link = document.createElement('a'); link.className = 'play';
    link.href = item.url; link.target = '_blank'; link.rel = 'noopener noreferrer';
    link.setAttribute('aria-label', `${item.date} ${day} M112 재생목록 — YouTube에서 새 창으로 열기`);
    link.innerHTML = playIcon;
    actions.append(copy, link); list.append(row);
  }
  if (!items.length) { const message = document.createElement('p'); message.className = 'empty'; message.textContent = '아직 재생목록이 없습니다.'; list.append(message); }
  document.querySelector('#count').textContent = String(items.length).padStart(2,'0');
  list.setAttribute('aria-busy','false');
  current = payload;
  renderedSignature = signature;
}
function checkedAt(payload) {
  const date = new Date(payload.updatedAtUtc);
  return payload.updatedAtUtc && Number.isFinite(+date) ? new Intl.DateTimeFormat('ko-KR',{month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hour12:false,timeZone:'Asia/Seoul'}).format(date) : '';
}
async function update() {
  if (loading) return; loading = true;
  try {
    const response = await fetch(FEED_URL, {cache:'no-store',credentials:'same-origin',signal:AbortSignal.timeout(10000)});
    if (!response.ok) throw new Error('조회 실패');
    const payload = await response.json(); render(payload);
    try { localStorage.setItem('m112-playlists-v1',JSON.stringify(payload)); } catch {}
    status.hidden = true; status.textContent = '';
  } catch {
    status.hidden = false;
    if (current) status.textContent = ['저장본',checkedAt(current)].filter(Boolean).join(' · ');
    else { list.setAttribute('aria-busy','false'); list.innerHTML = '<p class="empty">연결을 확인해 주세요.</p>'; status.textContent = ''; status.hidden = true; }
  } finally { loading = false; }
}
try { const cached = JSON.parse(localStorage.getItem('m112-playlists-v1')); if(cached) render(cached); } catch {}
if (!current) {
  try { const seed = await fetch('./playlists.json',{cache:'no-store'}); if(seed.ok) render(await seed.json()); } catch {}
}
document.addEventListener('visibilitychange',()=> { if(!document.hidden) { if(current) render(current); update(); } });
window.addEventListener('online',update);
setInterval(()=> { if(!document.hidden) { if(current) render(current); update(); } }, 60000);
update();
