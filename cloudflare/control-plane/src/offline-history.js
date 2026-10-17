const validDate = value => /^\d{4}-\d{2}-\d{2}$/.test(value || "") &&
  Number.isFinite(Date.parse(value + "T00:00:00Z")) &&
  new Date(value + "T00:00:00Z").toISOString().slice(0,10) === value;
export function mergeUploads(previous, app, capturedAtUtc) {
  capturedAtUtc=Number.isFinite(Date.parse(String(capturedAtUtc||"").slice(0,50)))
    ? new Date(String(capturedAtUtc).slice(0,50)).toISOString() : null;
  const items = new Map((previous?.items || []).map(item => [item.key, item]));
  for (const source of (app?.queue?.items || []).slice(0, 300)) {
    if (!source || typeof source !== "object") continue;
    const date = source.date || source.title?.match(/\|\s*(\d{4}-\d{2}-\d{2})\s*\|/)?.[1];
    if (!validDate(date) || !["pending","uploading","completed","failed"].includes(source.status)) continue;
    const period = Number(source.period);
    if (!Number.isInteger(period) || period < 0 || period > 99) continue;
    const slotId = String(source.slotId || (period === 0 ? "lunch" : "period-" + period)).slice(0,100);
    const key = date + ":" + slotId + ":" + String(source.id || source.title || period).slice(0,200);
    if(source.id)for(const [oldKey,old] of items)
      if(!old.id&&old.date===date&&old.slotId===slotId&&old.title===source.title)items.delete(oldKey);
    const old = items.get(key);
    // Completed transfers remain available if an older queue is restored on the PC.
    if (old?.status === "completed" && source.status !== "completed") continue;
    const timestamp=value=>Number.isFinite(Date.parse(value))?new Date(value).toISOString():null;
    items.set(key, {key, id:source.id?String(source.id).slice(0,100):null,date, period, slotId, slotLabel:String(source.slotLabel || (period + "교시")).slice(0,100),
      slotOrder:Number(source.slotOrder) || period, title:String(source.title || "").slice(0,200),
      status:source.status, videoId:/^[a-zA-Z0-9_-]{1,80}$/.test(source.videoId || "") ? source.videoId : null,
      uploadCompletedAtUtc:timestamp(source.uploadCompletedAtUtc), uploadStartedAtUtc:timestamp(source.uploadStartedAtUtc),
      uploadDurationSeconds:Number(source.uploadDurationSeconds) || null, attempts:Number(source.attempts) || 0,
      capturedAtUtc});
  }
  const days = {...previous?.days};
  if (validDate(app?.today?.date) && Array.isArray(app.today.periods)) {
    days[app.today.date] = app.today.periods.filter(slot=>slot&&typeof slot==="object").slice(0,30).map(slot => ({
      no:Number(slot.no),slotId:String(slot.slotId || "").slice(0,100),
      slotLabel:String(slot.slotLabel || "").slice(0,100),slotOrder:Number(slot.slotOrder),
      start:String(slot.start || "").slice(0,8),end:String(slot.end || "").slice(0,8)}));
  }
  const result={items:[...items.values()].sort((a,b)=>b.date.localeCompare(a.date)).slice(0,1200),
    days:Object.fromEntries(Object.entries(days).sort(([a],[b])=>b.localeCompare(a)).slice(0,90))};
  // Stay below the configured SQLite value limit even with maximum-length Unicode labels.
  while(new TextEncoder().encode(JSON.stringify(result)).length>900000&&result.items.length)
    result.items.splice(Math.max(0,result.items.length-100),100);
  while(new TextEncoder().encode(JSON.stringify(result)).length>900000&&Object.keys(result.days).length)
    delete result.days[Object.keys(result.days).at(-1)];
  return result;
}
