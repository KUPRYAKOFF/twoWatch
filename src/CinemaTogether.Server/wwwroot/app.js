"use strict";
const $ = (id) => document.getElementById(id);
const video = $("video");
let me,
  hub,
  snapshot,
  library = [],
  currentId,
  loadGeneration = 0,
  reportedBuffer = null,
  offset = 0,
  latency = 0,
  toastTimer,
  online = false,
  lastRevision = -1;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
function toast(message) {
  $("toast").textContent = String(message);
  $("toast").hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => ($("toast").hidden = true), 6000);
}
async function api(path, body, extra = {}) {
  const response = await fetch(path, {
    ...extra,
    ...(body === undefined
      ? {}
      : {
          method: "POST",
          headers: { "Content-Type": "application/json", ...extra.headers },
          body: JSON.stringify(body),
        }),
  });
  const value = await response.json().catch(() => ({}));
  if (!response.ok)
    throw new Error(
      value.error ||
        (response.status === 429
          ? "Слишком много запросов. Повторите через минуту."
          : response.status === 401
            ? "Войдите в комнату заново."
            : "Не удалось выполнить действие."),
    );
  return value;
}
function run(action) {
  return (...args) =>
    Promise.resolve()
      .then(() => action(...args))
      .catch((error) => toast(error.message));
}
function invoke(method, ...args) {
  if (!online) return Promise.reject(new Error("Нет соединения с сервером."));
  return hub.invoke(method, ...args);
}
function duration(seconds) {
  if (!Number.isFinite(seconds)) return "0:00";
  seconds = Math.max(0, Math.floor(seconds));
  return (
    (seconds >= 3600
      ? Math.floor(seconds / 3600) +
        ":" +
        String(Math.floor(seconds / 60) % 60).padStart(2, "0")
      : Math.floor(seconds / 60)) +
    ":" +
    String(seconds % 60).padStart(2, "0")
  );
}
function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}
function empty(target, text) {
  target.replaceChildren(element("div", "empty", text));
}
function canControl() {
  const p = snapshot?.participants.find((p) => p.id === me?.id);
  return online && !!p && (p.isHost || p.canControl);
}
function overlay(title, text, unlock = false) {
  $("preparation-progress").hidden = true;
  $("player-overlay").hidden = false;
  $("overlay-title").textContent = title;
  $("overlay-text").textContent = text;
  $("unlock").hidden = !unlock;
}
async function buffer(value) {
  if (reportedBuffer === value || !online || !currentId) return;
  reportedBuffer = value;
  try {
    await invoke("Buffer", value);
  } catch {
    reportedBuffer = null;
  }
}
function view(name) {
  document
    .querySelectorAll(".view")
    .forEach((v) => (v.hidden = v.id !== "view-" + name));
  document
    .querySelectorAll(".nav")
    .forEach((n) => n.classList.toggle("active", n.dataset.view === name));
  if (name === "history") run(loadHistory)();
}
document
  .querySelectorAll("[data-view]")
  .forEach((button) =>
    button.addEventListener("click", () => view(button.dataset.view)),
  );

async function refreshLibrary() {
  library = await api("/api/library");
  $("library-count").textContent = library.length;
  renderLibrary();
}
function mediaCard(item) {
  const card = element("article", "media-card"),
    poster = element("div", "poster"),
    play = element("button", "", "▶");
  play.title = me.isHost ? "Смотреть: " + item.title : item.title;
  play.disabled = !me.isHost;
  play.addEventListener(
    "click",
    run(async () => {
      await invoke("Select", item.id);
      view("cinema");
    }),
  );
  poster.append(play, element("small", "", duration(item.duration)));
  const body = element("div", "card-body");
  if (me.isHost) {
    const add = element("button", "add-queue", "+");
    add.title = "Добавить в очередь";
    add.addEventListener(
      "click",
      run(() => invoke("Enqueue", item.id)),
    );
    body.append(add);
  }
  body.append(
    element("strong", "", item.title),
    element(
      "p",
      "",
      `${item.height ? item.height + "p · " : ""}${item.videoCodec.toUpperCase()} · ${(item.size / 1024 ** 3).toFixed(2)} ГБ`,
    ),
  );
  card.append(poster, body);
  return card;
}
function renderLibrary() {
  const filtered = library.filter((item) =>
    item.title
      .toLocaleLowerCase()
      .includes($("search").value.toLocaleLowerCase()),
  );
  for (const [id, items] of [
    ["library", filtered],
    ["featured", library.slice(0, 3)],
  ]) {
    const target = $(id);
    target.replaceChildren(...items.map(mediaCard));
    if (!items.length)
      empty(
        target,
        library.length
          ? "Ничего не найдено"
          : "Добавьте первый фильм в desktop-приложении",
      );
  }
  renderQueue();
}
function renderQueue() {
  const target = $("queue");
  const items = snapshot?.queue || [];
  $("queue-count").textContent = items.length;
  target.replaceChildren(
    ...items.map((id, index) => {
      const row = element("div", "item-row");
      row.append(
        element("span", "", String(index + 1).padStart(2, "0")),
        element(
          "strong",
          "",
          library.find((item) => item.id === id)?.title || "Видео удалено",
        ),
      );
      if (me.isHost) {
        const button = element("button", "text-button", "Убрать");
        button.addEventListener(
          "click",
          run(() => invoke("RemoveQueued", index)),
        );
        row.append(button);
      }
      return row;
    }),
  );
  if (!items.length) empty(target, "Здесь появятся фильмы на ваш вечер");
  $("next").hidden = !me.isHost;
}
async function loadHistory() {
  const items = await api("/api/history");
  $("history").replaceChildren(
    ...items.map((item) => {
      const row = element("div", "item-row");
      row.append(
        element("strong", "", item.title),
        element("span", "", new Date(item.watchedAt).toLocaleString("ru-RU")),
      );
      return row;
    }),
  );
  if (!items.length) empty($("history"), "Ваши совместные истории ещё впереди");
}
function renderPeople() {
  const people = snapshot.participants;
  $("people-count").textContent = people.length;
  $("people").replaceChildren(
    ...people.map((person) => {
      const row = element("div", "person"),
        main = element("div", "person-main"),
        info = element("div");
      const role = person.buffering
        ? "Буферизация…"
        : person.isHost
          ? "Организатор"
          : person.canControl
            ? "Может управлять"
            : "Зритель";
      info.append(
        element("strong", "", person.name),
        element(
          "small",
          "",
          role + (person.voiceConnected ? " · ♬ В голосе" : ""),
        ),
      );
      main.append(
        element("span", "avatar", person.name.slice(0, 1).toUpperCase()),
        info,
      );
      row.append(main);
      if (me.isHost && !person.isHost) {
        const actions = element("div", "person-actions"),
          grant = element(
            "button",
            "",
            person.canControl ? "Забрать управление" : "Разрешить управление",
          ),
          kick = element("button", "", "Отключить");
        grant.addEventListener(
          "click",
          run(() => invoke("Grant", person.id, !person.canControl)),
        );
        kick.addEventListener(
          "click",
          run(() => invoke("Kick", person.id)),
        );
        actions.append(grant, kick);
        row.append(actions);
      }
      return row;
    }),
  );
  const allowed = canControl();
  for (const id of ["play", "seek", "rate"])
    $(id).disabled = !allowed || !snapshot.playback.videoId;
  $("control-hint").textContent = allowed
    ? "Вы можете управлять просмотром"
    : "Управляет организатор";
}
const seenMessages = new Set();
function message(item) {
  if (seenMessages.has(item.id)) return;
  seenMessages.add(item.id);
  const node = element("article", "message"),
    head = element("div", "message-head");
  head.append(
    element("span", "avatar", item.name.slice(0, 1).toUpperCase()),
    element("strong", "", item.name),
    element(
      "time",
      "",
      new Date(item.sentAt).toLocaleTimeString("ru-RU", {
        hour: "2-digit",
        minute: "2-digit",
      }),
    ),
  );
  node.append(head, element("p", "", item.text));
  $("messages").append(node);
  while ($("messages").children.length > 100) $("messages").firstChild.remove();
  $("messages").scrollTop = $("messages").scrollHeight;
}

async function loadMedia(id) {
  const generation = ++loadGeneration;
  reportedBuffer = null;
  video.pause();
  video.removeAttribute("src");
  video.load();
  if (!id) {
    overlay("Вечер начинается здесь", "Организатор скоро выберет фильм");
    return;
  }
  let item = library.find((item) => item.id === id);
  if (!item) {
    await refreshLibrary();
    if (generation !== loadGeneration) return;
    item = library.find((item) => item.id === id);
  }
  if (!item) {
    overlay("Видео недоступно", "Попросите организатора выбрать другой файл");
    return;
  }
  $("video-title").textContent = item.title;
  $("video-meta").textContent =
    `${duration(item.duration)} · ${item.width} × ${item.height} · ${item.videoCodec.toUpperCase()}`;
  $("quality").value = "original";
  $("audio").replaceChildren(
    new Option("По умолчанию", ""),
    ...item.tracks
      .filter((t) => t.type === "audio")
      .map(
        (t) =>
          new Option(
            `${t.language || "Звук"} ${t.title || t.codec} (#${t.index})`,
            String(t.index),
          ),
      ),
  );
  $("subtitles").replaceChildren(
    new Option("Выключены", ""),
    ...item.tracks
      .filter((t) => t.type === "subtitle")
      .map(
        (t) =>
          new Option(
            `${t.language || "Субтитры"} ${t.title || t.codec}`,
            String(t.index),
          ),
      ),
  );
  video.querySelectorAll("track").forEach((t) => t.remove());
  await loadSource(item, generation);
}
async function loadSource(item, generation = ++loadGeneration, force = false) {
  video.pause();
  video.removeAttribute("src");
  video.load();
  await buffer(true);
  overlay(
    "Подготавливаем видео",
    "Это может занять несколько минут. Дождитесь готовности всех участников.",
  );
  const quality = $("quality").value,
    audio = $("audio").value;
  try {
    let source = "/api/stream/" + item.id;
    if (!item.directPlay || quality !== "original" || audio !== "" || force) {
      let status = await api("/api/prepare/" + item.id, {
        quality,
        audio: audio === "" ? null : Number(audio),
      });
      while (status.state !== "ready") {
        if (generation !== loadGeneration) return;
        if (status.state === "failed") throw new Error(status.error);
        const percent = Number.isFinite(status.percent) ? status.percent : null;
        const eta =
          Number.isFinite(status.remainingSeconds) && percent < 99
            ? ` Примерно осталось ${duration(status.remainingSeconds)}.`
            : "";
        overlay(
          status.state === "queued"
            ? "Видео в очереди на подготовку"
            : percent >= 99
              ? "Завершаем подготовку файла"
              : percent === null
                ? "Готовим видео для браузера"
                : `Подготовка видео · ${percent.toLocaleString("ru-RU", { maximumFractionDigits: 1 })}%`,
          status.state === "queued"
            ? "Ожидаем завершения другой подготовки."
            : (status.copyVideo
                ? "Копируем видео без перекодирования и готовим звук."
                : "Преобразуем видео для браузера.") + eta,
        );
        if (status.state === "converting") {
          $("preparation-progress").hidden = false;
          if (percent === null)
            $("preparation-progress").removeAttribute("value");
          else $("preparation-progress").value = percent;
        }
        await sleep(1500);
        if (generation !== loadGeneration) return;
        status = await api("/api/conversions/" + status.id);
      }
      source = "/api/stream/converted/" + status.id;
    }
    if (generation !== loadGeneration) return;
    video.src = source;
    video.load();
  } catch (error) {
    if (generation !== loadGeneration) return;
    overlay(
      "Не удалось открыть видео",
      error.message +
        " Выберите другое качество или попросите организатора проверить файл.",
    );
    toast(error.message);
  }
}
function syncPlayback() {
  const state = snapshot?.playback;
  if (!state?.videoId || !video.getAttribute("src") || video.readyState < 2)
    return;
  const expected = Math.max(
    0,
    state.position +
      (state.isPlaying
        ? (Math.max(0, Date.now() + offset - state.updatedAt) / 1000) *
          state.rate
        : 0),
  );
  const target = Number.isFinite(video.duration)
    ? Math.min(expected, video.duration)
    : expected;
  const drift = target - video.currentTime;
  if (Math.abs(drift) > 0.5) {
    video.currentTime = target;
    video.playbackRate = state.rate;
  } else
    video.playbackRate =
      state.isPlaying && Math.abs(drift) > 0.1
        ? state.rate * (1 + Math.max(-0.05, Math.min(0.05, drift * 0.15)))
        : state.rate;
  $("rate").value = String(state.rate);
  $("play").textContent = state.isPlaying ? "Ⅱ" : "▶";
  if (state.isPlaying && video.paused && online)
    video
      .play()
      .catch(() =>
        overlay(
          "Готовы смотреть?",
          "Браузер просит разрешить воспроизведение со звуком.",
          true,
        ),
      );
  if (!state.isPlaying && !video.paused) video.pause();
  $("sync").textContent = state.isBuffering
    ? "Кто-то загружает видео…"
    : Math.abs(drift) > 0.5
      ? "Корректируем позицию"
      : "✓ Синхронизировано";
  if (state.isBuffering)
    overlay(
      "Подождём всех",
      "Участник загружает видео. Просмотр продолжится автоматически.",
    );
  else if (!video.paused || !state.isPlaying) $("player-overlay").hidden = true;
}
async function receive(state) {
  if (state.playback.revision < lastRevision) return;
  lastRevision = state.playback.revision;
  snapshot = state;
  state.messages.forEach(message);
  renderPeople();
  renderQueue();
  window.dispatchEvent(
    new CustomEvent("cinema-people", { detail: state.participants }),
  );
  if (currentId !== state.playback.videoId) {
    currentId = state.playback.videoId;
    await loadMedia(currentId);
  } else {
    if (video.readyState >= 3 && reportedBuffer !== false) await buffer(false);
    syncPlayback();
  }
}
function connectionStatus(text, ok) {
  $("connection").replaceChildren(element("i"), document.createTextNode(text));
  $("connection").classList.toggle("online", ok);
}
async function ping() {
  if (!online) return;
  const start = Date.now();
  const serverTime = await invoke("Ping");
  latency = Date.now() - start;
  offset = serverTime - (start + Date.now()) / 2;
  connectionStatus(
    latency < 500
      ? `На связи · ${latency} мс`
      : `Слабое соединение · ${latency} мс`,
    true,
  );
}
async function start() {
  me = await api("/api/me");
  $("login").hidden = true;
  $("app").hidden = false;
  $("my-name").textContent = me.name;
  $("my-avatar").textContent = me.name.slice(0, 1).toUpperCase();
  $("my-role").textContent = me.isHost ? "Организатор" : "Зритель";
  $("room-name").textContent = me.roomName;
  await refreshLibrary();
  hub = new signalR.HubConnectionBuilder()
    .withUrl("/watch")
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Error)
    .build();
  hub.on("State", (state) => run(receive)(state));
  hub.on("Message", message);
  window.dispatchEvent(new CustomEvent("cinema-connected", { detail: hub }));
  hub.onreconnecting(() => {
    online = false;
    window.dispatchEvent(new Event("cinema-offline"));
    reportedBuffer = null;
    video.pause();
    connectionStatus("Восстанавливаем связь…", false);
    if (snapshot) renderPeople();
    overlay("Связь прервалась", "Пробуем подключиться к организатору…");
  });
  hub.onreconnected(
    run(async () => {
      online = true;
      await ping();
      await receive(await invoke("GetState"));
    }),
  );
  hub.onclose(() => {
    online = false;
    window.dispatchEvent(new Event("cinema-offline"));
    video.pause();
    connectionStatus("Нет соединения", false);
    overlay(
      "Соединение завершено",
      "Сервер выключен или сессия завершена. Обновите страницу для входа.",
    );
    if (snapshot) renderPeople();
  });
  await hub.start();
  online = true;
  await ping();
  await receive(await invoke("GetState"));
}
$("join-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  $("join-button").disabled = true;
  $("login-error").textContent = "";
  try {
    await api("/api/auth/join", {
      name: $("name").value,
      password: $("password").value,
    });
    $("password").value = "";
    await start();
  } catch (error) {
    $("login-error").textContent = error.message;
  } finally {
    $("join-button").disabled = false;
  }
});
$("chat-form").addEventListener(
  "submit",
  run(async (event) => {
    event.preventDefault();
    const text = $("message").value.trim();
    if (!text) return;
    await invoke("Chat", text);
    $("message").value = "";
  }),
);
$("play").addEventListener(
  "click",
  run(() =>
    invoke("Control", snapshot.playback.isPlaying ? "pause" : "play", 0),
  ),
);
$("seek").addEventListener(
  "change",
  run(() =>
    invoke(
      "Control",
      "seek",
      (Number($("seek").value) / 1000) *
        (library.find((i) => i.id === currentId)?.duration || 0),
    ),
  ),
);
$("rate").addEventListener(
  "change",
  run(() => invoke("Control", "rate", Number($("rate").value))),
);
$("volume").addEventListener("input", () => {
  video.volume = Number($("volume").value);
  video.muted = video.volume === 0;
});
$("mute").addEventListener("click", () => {
  video.muted = !video.muted;
  $("mute").textContent = video.muted ? "∅" : "♪";
});
$("fullscreen").addEventListener(
  "click",
  run(() =>
    document.fullscreenElement
      ? document.exitFullscreen()
      : $("player").requestFullscreen(),
  ),
);
$("unlock").addEventListener(
  "click",
  run(async () => {
    await video.play();
    $("player-overlay").hidden = true;
    syncPlayback();
  }),
);
for (const id of ["quality", "audio"])
  $(id).addEventListener(
    "change",
    run(() => {
      const item = library.find((i) => i.id === currentId);
      if (item) return loadSource(item);
    }),
  );
$("subtitles").addEventListener("change", () => {
  video.querySelectorAll("track").forEach((t) => t.remove());
  const value = $("subtitles").value;
  if (!value || !currentId) return;
  const track = document.createElement("track");
  track.kind = "subtitles";
  track.label = "Субтитры";
  track.srclang = "ru";
  track.src = `/api/subtitles/${currentId}/${value}`;
  track.default = true;
  track.addEventListener("error", () =>
    toast(
      "Эти субтитры не удалось преобразовать в текст. Графические субтитры не поддерживаются.",
    ),
  );
  video.append(track);
  track.track.mode = "showing";
});
$("copy-link").addEventListener(
  "click",
  run(async () => {
    try {
      await navigator.clipboard.writeText(location.origin);
      toast(
        "Ссылка скопирована. Если это localhost, используйте адрес из desktop-приложения.",
      );
    } catch {
      toast("Ссылка комнаты: " + location.origin);
    }
  }),
);
$("logout").addEventListener(
  "click",
  run(async () => {
    await api("/api/auth/logout", {});
    await hub?.stop();
    location.reload();
  }),
);
$("refresh-library").addEventListener("click", run(refreshLibrary));
$("search").addEventListener("input", renderLibrary);
$("next").addEventListener(
  "click",
  run(() => invoke("Next")),
);
for (const [tab, panel, otherTab, otherPanel] of [
  ["chat-tab", "chat-panel", "people-tab", "people-panel"],
  ["people-tab", "people-panel", "chat-tab", "chat-panel"],
])
  $(tab).addEventListener("click", () => {
    $(tab).classList.add("selected");
    $(otherTab).classList.remove("selected");
    $(panel).hidden = false;
    $(otherPanel).hidden = true;
  });
video.addEventListener("waiting", () => {
  if (video.readyState < 3) buffer(true);
});
video.addEventListener("canplay", () => {
  buffer(false);
  syncPlayback();
});
video.addEventListener("seeked", () => {
  if (video.readyState >= 3) buffer(false);
});
video.addEventListener("error", () => {
  if (video.getAttribute("src"))
    overlay(
      "Браузер не смог открыть видео",
      "Попробуйте выбрать 720p: FFmpeg подготовит совместимый MP4.",
    );
});
video.addEventListener("ended", () => {
  if (canControl()) run(() => invoke("Control", "pause", 0))();
});
setInterval(() => {
  if (!currentId) return;
  $("time").textContent =
    duration(video.currentTime) +
    " / " +
    duration(library.find((i) => i.id === currentId)?.duration || 0);
  if (video.duration)
    $("seek").value = String((video.currentTime / video.duration) * 1000);
  if (online) syncPlayback();
}, 500);
setInterval(() => run(ping)(), 5000);
run(async () => {
  const token = new URLSearchParams(location.hash.slice(1)).get("host");
  if (token) {
    history.replaceState(null, "", location.pathname);
    await api(
      "/api/auth/host",
      {},
      { headers: { Authorization: "Bearer " + token } },
    );
  }
  try {
    await start();
  } catch (error) {
    $("login").hidden = false;
    $("app").hidden = true;
    if (token) $("login-error").textContent = error.message;
  }
})();
