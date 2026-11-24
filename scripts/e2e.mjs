import { chromium } from "playwright";
import assert from "node:assert/strict";
import { spawn, execFileSync } from "node:child_process";
import { mkdtemp, mkdir, rm } from "node:fs/promises";
import { existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import net from "node:net";
import { startHttpsProxy } from "./https-proxy.mjs";

const dotnet = process.env.DOTNET_COMMAND || "dotnet";
const scratch = await mkdtemp(join(tmpdir(), "cinema-e2e-"));
const viaProxy = process.env.CINEMA_HTTPS_PROXY === "1";
const output = resolve("artifacts/e2e", viaProxy ? "https-proxy" : "direct");
await mkdir(output, { recursive: true });
const probe = net.createServer();
await new Promise((r) => probe.listen(0, "127.0.0.1", r));
const port = probe.address().port;
await new Promise((r) => probe.close(r));
const origin = `http://127.0.0.1:${port}`;
const film = join(scratch, "Общий вечер.webm");
execFileSync("ffmpeg", [
  "-nostdin",
  "-v",
  "error",
  "-f",
  "lavfi",
  "-i",
  "testsrc2=size=640x360:rate=24:duration=40",
  "-f",
  "lavfi",
  "-i",
  "sine=frequency=440:duration=40",
  "-c:v",
  "libvpx",
  "-threads",
  "2",
  "-deadline",
  "realtime",
  "-cpu-used",
  "8",
  "-b:v",
  "700k",
  "-c:a",
  "libopus",
  film,
]);
const child = spawn(
  dotnet,
  ["src/CinemaTogether.Server/bin/Debug/net10.0/CinemaTogether.Server.dll"],
  {
    env: {
      ...process.env,
      CINEMA_DATA_DIR: join(scratch, "data"),
      CINEMA_PORT: String(port),
      CINEMA_PASSWORD: "secret1",
      CINEMA_HOST_SECRET: "e".repeat(64),
      CINEMA_IMPORT: film,
    },
    stdio: ["ignore", "pipe", "pipe"],
  },
);
let log = "";
child.stdout.on("data", (d) => (log += d));
child.stderr.on("data", (d) => (log += d));
let browser, proxy;
const observedPages = [];
const pause = (ms) => new Promise((r) => setTimeout(r, ms));
async function until(action, description, timeout = 20000) {
  const end = Date.now() + timeout;
  let error;
  while (Date.now() < end) {
    try {
      const value = await action();
      if (value) return value;
    } catch (e) {
      error = e;
    }
    await pause(150);
  }
  throw new Error(description + (error ? ": " + error.message : ""));
}
try {
  await until(
    async () => (await fetch(origin + "/api/health")).ok,
    "Server did not start",
  );
  if (viaProxy) proxy = await startHttpsProxy(port, scratch);
  const viewerOrigin = proxy?.origin || origin;
  browser = await chromium.launch({
    executablePath:
      process.env.CHROMIUM_PATH ||
      (existsSync("/usr/bin/chromium") ? "/usr/bin/chromium" : undefined),
    headless: true,
    args: [
      "--no-sandbox",
      "--autoplay-policy=no-user-gesture-required",
      "--use-fake-device-for-media-stream",
      "--use-fake-ui-for-media-stream",
    ],
  });
  const hostContext = await browser.newContext({
      viewport: { width: 1440, height: 1000 },
    }),
    viewerContext = await browser.newContext({
      viewport: { width: 1280, height: 900 },
      ignoreHTTPSErrors: viaProxy,
    });
  const host = await hostContext.newPage(),
    viewer = await viewerContext.newPage();
  const errors = [];
  observedPages.push(host, viewer);
  for (const page of observedPages)
    await page.addInitScript(() => {
      const Original = window.RTCPeerConnection;
      window.__testPeers = [];
      window.RTCPeerConnection = class extends Original {
        constructor(...args) {
          super(...args);
          window.__testPeers.push(this);
        }
      };
    });
  for (const page of [host, viewer]) {
    page.on("pageerror", (error) => errors.push(error.message));
    page.on("console", (message) => {
      if (
        message.type() === "error" &&
        !message.text().startsWith("Failed to load resource:")
      )
        console.error("Browser:", message.text());
    });
  }
  await viewer.goto(viewerOrigin);
  await viewer.screenshot({ path: join(output, "login.png"), fullPage: true });
  await viewer.locator("#name").fill("Друг");
  await viewer.locator("#password").fill("secret1");
  await viewer.locator("#join-button").click();
  await viewer.locator("#app").waitFor({ state: "visible" });
  if (viaProxy) {
    const cookie = (await viewerContext.cookies(viewerOrigin)).find(
      (cookie) => cookie.name === "cinema.session",
    );
    assert.ok(
      cookie?.secure && cookie?.httpOnly,
      "HTTPS tunnel must set a Secure HttpOnly session",
    );
    const bootstrap = await viewerContext.request.post(
      viewerOrigin + "/api/auth/host",
      {
        headers: { Authorization: "Bearer " + "e".repeat(64) },
      },
    );
    assert.equal(
      bootstrap.status(),
      401,
      "Tunnel must not expose organizer bootstrap",
    );
    const foreign = await viewerContext.request.post(
      viewerOrigin + "/api/auth/join",
      {
        headers: { Origin: "https://attacker.example" },
        data: { name: "Friend", password: "secret1" },
      },
    );
    assert.equal(
      foreign.status(),
      403,
      "Tunnel must preserve cross-origin protection",
    );
  }
  await host.goto(origin + "/#host=" + "e".repeat(64));
  await host.locator("#app").waitFor({ state: "visible" });
  await host.locator("#featured .poster button").first().click();
  await until(
    async () =>
      (await host.locator("#video").evaluate((v) => v.readyState >= 3)) &&
      (await viewer.locator("#video").evaluate((v) => v.readyState >= 3)),
    "Both players should buffer",
  );
  if (viaProxy) {
    const mediaId = await viewer.evaluate(() => currentId);
    const range = await viewerContext.request.get(
      viewerOrigin + "/api/stream/" + mediaId,
      {
        headers: { Range: "bytes=100-199" },
      },
    );
    assert.equal(range.status(), 206);
    assert.equal((await range.body()).length, 100);
    assert.ok(range.headers()["content-range"].startsWith("bytes 100-199/"));
    await until(
      () => proxy.webSocketConnections > 0,
      "SignalR must connect through the WebSocket proxy",
    );
  }
  await until(
    () =>
      host
        .locator("#sync")
        .textContent()
        .then((t) => t.includes("Синхронизировано")),
    "Room should become ready",
  );
  assert.equal(await viewer.locator("#play").isDisabled(), true);
  // Exercise the visible progress contract without depending on conversion speed
  // or requiring H.264 decoding support in the test Chromium distribution.
  const progress = {
    id: "progress-check",
    state: "converting",
    error: null,
    percent: 45.5,
    remainingSeconds: 90,
    copyVideo: true,
  };
  await host.route("**/api/prepare/*", (route) =>
    route.fulfill({ json: progress }),
  );
  await host.route("**/api/conversions/progress-check", (route) =>
    route.fulfill({ json: progress }),
  );
  await host.locator("#quality").selectOption("720");
  await host.locator("#preparation-progress").waitFor({ state: "visible" });
  assert.equal(
    await host.locator("#preparation-progress").getAttribute("value"),
    "45.5",
  );
  assert.ok(
    (await host.locator("#overlay-text").textContent()).includes("1:30"),
  );
  await host.locator("#quality").selectOption("original");
  await until(
    () => host.locator("#video").evaluate((video) => video.readyState >= 3),
    "Original video should resume after cancelling preparation",
  );
  await host.unroute("**/api/prepare/*");
  await host.unroute("**/api/conversions/progress-check");
  await host.locator("#play").click();
  await until(
    async () =>
      (await host
        .locator("#video")
        .evaluate((v) => !v.paused && v.currentTime > 1)) &&
      (await viewer
        .locator("#video")
        .evaluate((v) => !v.paused && v.currentTime > 1)),
    "Playback should start for both",
  );
  const positions = await Promise.all(
    [host, viewer].map((page) =>
      page.locator("#video").evaluate((v) => v.currentTime),
    ),
  );
  assert.ok(
    Math.abs(positions[0] - positions[1]) < 0.6,
    "Drift should stay below 600ms",
  );
  await host.locator("#play").click();
  await until(
    () => viewer.locator("#video").evaluate((v) => v.paused),
    "Pause should propagate",
  );
  await host.locator("#seek").fill("500");
  await host.locator("#seek").dispatchEvent("change");
  await until(
    () =>
      viewer
        .locator("#video")
        .evaluate((v) => Math.abs(v.currentTime - 20) < 1),
    "Seek should propagate",
  );
  await viewer.locator("#message").fill('<script>alert("xss")</script>');
  await viewer.locator("#chat-form button").click();
  await host
    .getByText('<script>alert("xss")</script>', { exact: true })
    .waitFor();
  if (process.env.CINEMA_SKIP_VOICE_MEDIA === "1")
    console.log(
      "SKIP: direct WebRTC media check explicitly disabled; signaling remains covered by .NET integration tests.",
    );
  else {
    await host.locator("#voice-join").click();
    await viewer.locator("#voice-join").click();
    await until(
      async () =>
        (await host.locator("#voice-status").textContent()).includes(
          "Голос подключён",
        ) &&
        (await viewer.locator("#voice-status").textContent()).includes(
          "Голос подключён",
        ),
      "Voice peers should connect",
    );
    await viewer.locator("#voice-mute").click();
    assert.equal(
      await viewer.locator("#voice-mute").textContent(),
      "Микрофон выключен",
    );
    await viewer.locator("#voice-join").click();
    await host.locator("#voice-join").click();
  }
  await host.locator("#people-tab").click();
  await host.getByRole("button", { name: "Разрешить управление" }).click();
  await until(
    async () => !(await viewer.locator("#play").isDisabled()),
    "Grant should reach viewer",
  );
  await viewer.locator("#play").click();
  await until(
    () => host.locator("#video").evaluate((v) => !v.paused),
    "Viewer with permission can play",
  );
  await host.screenshot({
    path: join(output, "room-desktop.png"),
    fullPage: true,
  });
  await viewer.setViewportSize({ width: 390, height: 844 });
  await viewer.screenshot({
    path: join(output, "room-mobile.png"),
    fullPage: true,
  });
  assert.equal(
    await viewer.evaluate(
      () => document.documentElement.scrollWidth > innerWidth,
    ),
    false,
    "Mobile should not overflow",
  );
  await viewer.reload();
  await viewer.locator("#app").waitFor({ state: "visible" });
  await until(
    () =>
      viewer
        .locator("#video")
        .evaluate((v) => v.readyState >= 3 && v.currentTime > 15),
    "Reconnect should restore position",
  );
  await host.getByRole("button", { name: "Отключить", exact: true }).click();
  await until(
    async () =>
      (
        await viewerContext.request.get(viewerOrigin + "/api/state")
      ).status() === 401,
    "Kick should revoke HTTP access",
  );
  assert.deepEqual(errors, []);
  console.log(
    "PASS: authentication, two-player playback/pause/seek, drift, chat escaping, permission grant, mobile, reconnect, kick." +
      (process.env.CINEMA_SKIP_VOICE_MEDIA === "1"
        ? ""
        : " WebRTC voice/mute passed."),
  );
  if (viaProxy)
    console.log(
      "PASS: HTTPS proxy login, Secure cookie, origin protection, local organizer isolation, HTTP Range, WebSockets, playback and reconnect.",
    );
} catch (error) {
  for (const page of observedPages) {
    console.error(
      "Voice state:",
      await page.locator("#voice-status").textContent(),
      await page.locator("#toast").textContent(),
      await page.evaluate(() => ({
        participants: snapshot?.participants,
        rtc: window.__testPeers?.map((p) => ({
          state: p.connectionState,
          ice: p.iceConnectionState,
          gather: p.iceGatheringState,
          signal: p.signalingState,
          local: !!p.localDescription,
          remote: !!p.remoteDescription,
        })),
      })),
    );
    await page.screenshot({
      path: join(output, `failure-${observedPages.indexOf(page)}.png`),
      fullPage: true,
    });
  }
  console.error(log.slice(-6000));
  throw error;
} finally {
  await browser?.close();
  await proxy?.close();
  child.kill("SIGTERM");
  await Promise.race([new Promise((r) => child.once("exit", r)), pause(5000)]);
  if (child.exitCode === null) child.kill("SIGKILL");
  await rm(scratch, { recursive: true, force: true });
}
