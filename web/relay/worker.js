// AutoZapret — внешний релей для Telegram (Cloudflare Worker).
//
// Что делает: принимает WebSocket от программы, открывает TCP к Telegram и
// перекладывает байты в обе стороны. Шифрование целиком на стороне клиента —
// воркер не видит ни TLS, ни MTProto, ни секрета прокси.
//
// Почему это нужно: в сетях, где `kws*.web.telegram.org` блокируется по адресу,
// прямой путь не работает вообще. Релей меняет адрес назначения — трафик идёт
// на *.workers.dev, который не заблокирован.
//
// Установка: dash.cloudflare.com -> Compute -> Workers & Pages -> Create ->
// "Start with Hello World" -> Deploy -> Edit code -> вставить этот файл ->
// Deploy. Адрес вида cdn.имя.workers.dev вставить в программе в поле
// «Внешний релей» и нажать «Проверить».
//
// Необязательно: Settings -> Variables and Secrets -> добавить переменную
// KEY со случайным значением. Тогда без `?key=...` воркером не сможет
// пользоваться кто угодно — без этого он открытый прокси для всего интернета.

import { connect } from "cloudflare:sockets";

// Cloudflare ограничивает размер сообщения WebSocket; 32 КБ — безопасно.
const MAX_CHUNK = 32 * 1024;

// Приведение кадра к байтам.
//
// Здесь была главная ловушка первой версии: `data instanceof ArrayBuffer`
// в рантайме Workers может дать false (объект приходит из другого realm или
// как Uint8Array/Blob), и тогда TextEncoder превращал БИНАРНЫЙ кадр в строку
// "[object ArrayBuffer]". В Telegram уходил текст вместо TLS, Telegram отвечал
// "400 Bad Request" от своего nginx, и со стороны это выглядело как «воркер
// не работает», хотя TCP был открыт правильно.
//
// Поэтому проверяем тремя способами, а неизвестный тип честно считаем ошибкой.
function toBytes(data) {
  if (typeof data === "string") return new TextEncoder().encode(data);
  if (data instanceof ArrayBuffer) return new Uint8Array(data);

  // ArrayBuffer.isView работает и для чужих realm, и для любого TypedArray
  if (ArrayBuffer.isView(data)) return new Uint8Array(data.buffer, data.byteOffset, data.byteLength);

  if (data && typeof data.arrayBuffer === "function") {
    return data.arrayBuffer().then((ab) => new Uint8Array(ab));
  }

  return null;
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    // ---- диагностика без WebSocket: /?probe=host:port
    //
    // Именно здесь видно НАСТОЯЩУЮ причину отказа. Если открыть этот адрес
    // в браузере, воркер скажет, разрешил ли Cloudflare соединение и почему
    // нет — без догадок вроде «Stream was cancelled».
    if (url.searchParams.has("probe")) {
      const target = url.searchParams.get("probe") || "kws2.web.telegram.org:443";
      const cut = target.lastIndexOf(":");
      const host = cut > 0 ? target.slice(0, cut) : target;
      const port = cut > 0 ? parseInt(target.slice(cut + 1), 10) : 443;

      try {
        const probe = connect({ hostname: host, port });
        const info = await probe.opened;
        try { probe.close(); } catch (e) {}
        return new Response("OK " + host + ":" + port + " " + JSON.stringify(info) + "\n", { status: 200 });
      } catch (e) {
        return new Response("FAIL " + host + ":" + port + " -> " + (e && e.message ? e.message : String(e)) + "\n", { status: 200 });
      }
    }

    if ((request.headers.get("Upgrade") || "").toLowerCase() !== "websocket") {
      return new Response("Expected Upgrade: websocket\n", { status: 426 });
    }

    const host = url.searchParams.get("host");
    const port = parseInt(url.searchParams.get("port") || "443", 10);

    // Если задан KEY — чужой трафик через воркер не пойдёт.
    if (env && env.KEY && url.searchParams.get("key") !== env.KEY) {
      return new Response("forbidden\n", { status: 403 });
    }

    if (!host || !(port > 0 && port < 65536)) {
      return new Response("missing host/port\n", { status: 400 });
    }

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    server.accept();

    let socket;

    try {
      socket = connect({ hostname: host, port });

      // Без этого ожидания ошибка соединения приходит как «Stream was
      // cancelled», и починить её невозможно: непонятно, что именно
      // запретил Cloudflare.
      await socket.opened;
    } catch (e) {
      const why = "connect " + host + ":" + port + " -> " + (e && e.message ? e.message : String(e));

      // Причина уходит в close-кадр: программа покажет её человеку.
      try { server.close(1011, why.slice(0, 120)); } catch (x) {}

      return new Response(null, { status: 101, webSocket: client });
    }

    const writer = socket.writable.getWriter();
    const reader = socket.readable.getReader();

    let closed = false;
    let pending = Promise.resolve();

    const shutdown = (why) => {
      if (closed) return;
      closed = true;

      try { reader.cancel(); } catch (e) {}
      try { socket.close(); } catch (e) {}
      try { server.close(1000, why ? String(why).slice(0, 120) : ""); } catch (e) {}
    };

    server.addEventListener("message", (event) => {
      const data = event.data;

      // Записи сериализуем: параллельный writer.write переставляет куски,
      // и TLS-поток на той стороне превращается в мусор.
      pending = pending
        .then(() => toBytes(data))
        .then((bytes) => {
          if (bytes === null) {
            // Неизвестный тип кадра. Отправить «что-нибудь» — значит испортить
            // поток и получить невнятную ошибку на той стороне.
            throw new Error("unexpected frame type: " + Object.prototype.toString.call(data));
          }

          return bytes.length ? writer.write(bytes) : undefined;
        })
        .catch((e) => shutdown("write: " + (e && e.message ? e.message : e)));
    });

    server.addEventListener("close", () => shutdown("client closed"));
    server.addEventListener("error", () => shutdown("client error"));

    (async () => {
      try {
        while (!closed) {
          const { done, value } = await reader.read();

          if (done) break;

          if (value && value.byteLength) {
            // Режем крупные куски: лимит на сообщение WebSocket меньше, чем
            // может отдать TCP.
            for (let offset = 0; offset < value.byteLength; offset += MAX_CHUNK) {
              server.send(value.subarray(offset, Math.min(offset + MAX_CHUNK, value.byteLength)));
            }
          }
        }

        shutdown("remote closed");
      } catch (e) {
        shutdown("read: " + (e && e.message ? e.message : e));
      }
    })();

    return new Response(null, { status: 101, webSocket: client });
  },
};