import { copyFile, mkdir } from "node:fs/promises";
await mkdir("src/CinemaTogether.Server/wwwroot/vendor", { recursive: true });
await copyFile(
  "node_modules/@microsoft/signalr/dist/browser/signalr.min.js",
  "src/CinemaTogether.Server/wwwroot/vendor/signalr.min.js",
);
await copyFile(
  "node_modules/@microsoft/signalr/dist/browser/signalr.min.js.map",
  "src/CinemaTogether.Server/wwwroot/vendor/signalr.min.js.map",
);
