const CHUNK = 256 * 1024;
export function validateChunk(value, id, offset, first) {
  if (!value || value.id !== id || value.offset !== offset || !Number.isSafeInteger(value.size)
      || value.size < 0 || typeof value.name !== "string" || value.name.length > 255
      || !/^[a-f0-9]{64}$/.test(value.sha256) || typeof value.data !== "string"
      || value.data.length > Math.ceil(CHUNK / 3) * 4
      || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value.data))
    throw new Error("문서 전송 응답 형식이 올바르지 않습니다.");
  if (first && (value.size !== first.size || value.sha256 !== first.sha256 || value.name !== first.name))
    throw new Error("전송 중 문서가 변경됐습니다.");
  const bytes = Uint8Array.from(atob(value.data), character => character.charCodeAt(0));
  const expected = Math.min(CHUNK, value.size - offset);
  if (expected < 0 || bytes.length !== expected || value.end !== (offset + bytes.length === value.size))
    throw new Error("문서 조각이 누락됐습니다.");
  return bytes;
}
export function downloadHeaders(name, size) {
  const safe = name.replace(/[\r\n\x00-\x1f\x7f/\\]/g, "_");
  const encoded = encodeURIComponent(safe).replace(/[!'()*]/g, c => "%" + c.charCodeAt(0).toString(16));
  return { "content-type": "application/octet-stream", "content-length": String(size),
    "content-disposition": "attachment; filename=\"document\"; filename*=UTF-8''" + encoded,
    "cache-control": "no-store", "x-content-type-options": "nosniff", "referrer-policy": "no-referrer" };
}
export async function streamDocument(read, id, release, signal) {
  let first;
  try { first = await read(0); validateChunk(first, id, 0); if(signal?.aborted)throw new Error("전송이 중단됐습니다."); }
  catch (error) { release(); throw error; }
  let offset = 0, initial = first, closed = false;
  let streamController;
  const abort = () => { if(!closed){streamController.error(new Error("PC 연결 또는 전송이 중단됐습니다."));finish();} };
  const finish = () => { if (!closed) { closed = true; signal?.removeEventListener("abort",abort); release(); } };
  const stream = new ReadableStream({
    start(controller) { streamController=controller;signal?.addEventListener("abort",abort,{once:true});if(signal?.aborted)abort(); },
    async pull(controller) {
      if (closed) return;
      try {
        const chunk = initial || await read(offset);
        initial = null;
        if (closed) return;
        const bytes = validateChunk(chunk, id, offset, first);
        offset += bytes.length;
        if (bytes.length) controller.enqueue(bytes);
        if (chunk.end) { controller.close(); finish(); }
      } catch (error) { if (!closed) controller.error(error); finish(); }
    },
    cancel() { finish(); }
  }, { highWaterMark: 0 }); // Request the next chunk only when the browser consumes the last one.
  return new Response(stream, { headers: downloadHeaders(first.name, first.size) });
}
