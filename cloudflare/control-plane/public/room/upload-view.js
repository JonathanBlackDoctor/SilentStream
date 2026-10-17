/* Shared by the room PWA and the embedded PC controller. No network or device mutations. */
(function (root) {
  "use strict";
  const esc = value => String(value ?? "").replace(/[&<>"']/g, c =>
    ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
  const day = value => new Intl.DateTimeFormat("sv-SE", {timeZone:"Asia/Seoul"}).format(value || new Date());
  const time = (value, date = false) => {
    if (!value || !Number.isFinite(new Date(value).getTime())) return "확인 중";
    return new Date(value).toLocaleString("ko-KR", {timeZone:"Asia/Seoul",
      ...(date ? {month:"numeric",day:"numeric"} : {}), hour:"2-digit",minute:"2-digit",hour12:false});
  };
  function fresh(value, now = Date.now()) {
    const captured = new Date(value?.app?.updatedAtUtc || value?.lastSeenAtUtc || "").getTime();
    return value?.online === true && Number.isFinite(captured) && now - captured >= -30000 && now - captured < 45000;
  }
  // Older apps omitted Date. Only accept the date explicitly present in their title.
  const jobDate = job => job.date || job.title?.match(/\|\s*(\d{4}-\d{2}-\d{2})\s*\|/)?.[1] || "";
  function model(app, options = {}) {
    const date = options.date || day(), now = options.now ?? Date.now(), online = options.fresh === true;
    const queue = app?.queue, jobs = queue?.items || [], selected = jobs.filter(j => jobDate(j) === date);
    const slots = app?.today?.date === date ? app.today.periods || [] : [];
    const slotKey = item => item.slotId || ((item.period ?? item.no) === 0 ? "lunch" : "period-" + (item.period ?? item.no));
    const rows = selected.map(job => {
      let detail = "", label = ({completed:"업로드 완료",uploading:"업로드 중",pending:"업로드 대기",failed:"업로드 실패"})[job.status] || "상태 확인 필요";
      if (job.status === "completed")
        detail = job.uploadCompletedAtUtc ? time(job.uploadCompletedAtUtc, true) + " 완료" : "업로드 완료 시각 없음";
      else if (job.status === "failed") detail = "PC에서 실패 원인 확인 필요";
      else if (!online) detail = "마지막 수신 상태 · 예상 시각 확인 불가";
      else if (queue.pausedUntilUtc && new Date(queue.pausedUntilUtc).getTime() > now)
        detail = time(queue.pausedUntilUtc, true) + " 이후 한도 초기화 대기";
      else if (job.estimatedCompletedAtUtc && new Date(job.estimatedCompletedAtUtc).getTime() > now)
        detail = "약 " + time(job.estimatedCompletedAtUtc, day(new Date(job.estimatedCompletedAtUtc)) !== date) + " 업로드 완료 예상";
      else detail = job.attempts > 0 ? "재시도 대기 · 예상 시각 미정" : "예상 시각 산정 중";
      if (job.status === "uploading" && online && Number.isFinite(job.uploadElapsedSeconds))
        detail += " · " + Math.floor(job.uploadElapsedSeconds / 60) + "분 경과";
      return {label:job.slotLabel || (job.period === 0 ? "점심시간" : job.period + "교시"),
        order:job.slotOrder || slots.find(s => slotKey(s) === slotKey(job))?.slotOrder || job.period || 0,
        status:job.status, stateLabel:label, detail, videoId:job.status === "completed" ? job.videoId : null,
        key:slotKey(job)};
    });
    for (const slot of slots) {
      if (rows.some(r => r.key === slotKey(slot))) continue;
      const start = new Date(date + "T" + slot.start + "+09:00").getTime();
      const end = new Date(date + "T" + slot.end + "+09:00").getTime();
      const recording = online && ["Live","RecordingOnly"].includes(app.state) && app.recording?.file;
      rows.push({label:slot.slotLabel || slot.no + "교시",order:slot.slotOrder || slot.no,
        status:"scheduled",stateLabel:!online?"확인 대기":now<start?"수업 예정":now<end?(recording?"녹화 중":"수업 시간"):"영상 준비 확인",
        detail:!online?"마지막 시간표 · 현재 상태 확인 불가":now<end?
          time(end) + " 수업 종료 · 영상 준비 후 예상 제공":"업로드 큐에 등록된 영상이 아직 없습니다."});
    }
    rows.sort((a,b) => a.order - b.order);
    const latest = jobs.map(jobDate).filter(Boolean).sort().at(-1);
    return {date, rows, latest, available:!!queue,
      completed:rows.filter(r => r.status === "completed").length,
      measuredCount:queue?.measuredCount || 0,
      averageMinutes:queue?.averageDurationSeconds > 0 ? Math.ceil(queue.averageDurationSeconds / 60) : null,
      otherPending:jobs.filter(j => jobDate(j) !== date && ["pending","uploading"].includes(j.status)).length,
      omittedItems:queue?.omittedItems || 0,
      unknownDate:jobs.filter(j => !jobDate(j)).length};
  }
  function render(app, options = {}) {
    const data = model(app, options);
    const note = options.fresh ? "실제 전송 기록에 따른 추정입니다. YouTube 영상 처리 시간은 별도입니다." :
      "저장된 마지막 기록입니다. PC가 다시 연결되면 현재 상태와 예상 시간이 갱신됩니다.";
    return '<div class="upload-overview"><strong>' + (data.available ? data.rows.length ? data.completed + " / " + data.rows.length + " 업로드 완료" : "이 날짜의 업로드 기록 없음" : "업로드 정보 수신 대기") +
      '</strong><span>' + (data.averageMinutes ? "최근 평균 약 " + data.averageMinutes + "분 · " + data.measuredCount + "건" : "실제 업로드 기록 수집 중") +
      '</span></div><div class="upload-list">' + (data.rows.length ? data.rows.map(row =>
        '<div class="upload-row"><div class="upload-slot">' + esc(row.label) +
        '</div><div class="upload-info"><span class="upload-state ' + esc(row.status) + '">' + esc(row.stateLabel) +
        '</span><small>' + esc(row.detail) + '</small></div>' + (row.videoId ?
          '<a class="video-link" target="_blank" rel="noopener" href="https://youtu.be/' + encodeURIComponent(row.videoId) + '">영상 ↗</a>' : "") + '</div>').join("") :
        '<p class="meta">' + (data.available ? "이 날짜에 수신된 교시별 업로드 기록이 없습니다." : "PC 앱을 연결하면 교시별 업로드 정보를 확인할 수 있습니다.") + '</p>') +
      '</div><p class="estimate-note">' + esc(note) + (data.otherPending ? " 다른 날짜 영상 " + data.otherPending + "건도 업로드 대기 중입니다." : "") +
      (data.omittedItems ? " 보관 기록 " + data.omittedItems + "건은 표시 범위를 넘어 생략했습니다." : "") +
      (data.unknownDate ? " 날짜를 확인할 수 없는 이전 기록은 교시에 합산하지 않았습니다." : "") + '</p>';
  }
  root.UploadView = {model, render, fresh, time, day};
})(typeof globalThis === "object" ? globalThis : this);
