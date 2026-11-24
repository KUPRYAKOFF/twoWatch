// Local test fixture: HTTPS terminates here and both HTTP and WebSockets reach
// Kestrel over plain HTTP, just as they do through a local tunnel client.
import https from "node:https";
import http from "node:http";
import net from "node:net";
import { readFile } from "node:fs/promises";
import { execFileSync } from "node:child_process";
import { join } from "node:path";

export async function startHttpsProxy(backendPort, directory) {
  const key = join(directory, "proxy.key");
  const cert = join(directory, "proxy.crt");
  execFileSync(
    "openssl",
    [
      "req",
      "-x509",
      "-newkey",
      "rsa:2048",
      "-nodes",
      "-days",
      "1",
      "-subj",
      "/CN=localhost",
      "-addext",
      "subjectAltName=IP:127.0.0.1,DNS:localhost",
      "-keyout",
      key,
      "-out",
      cert,
    ],
    { stdio: "ignore" },
  );

  const server = https.createServer({
    key: await readFile(key),
    cert: await readFile(cert),
  });
  const sockets = new Set();
  let webSocketConnections = 0;
  server.on("connection", (socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
  });
  function headers(request) {
    return {
      ...request.headers,
      host: `127.0.0.1:${backendPort}`,
      "x-forwarded-host": request.headers.host,
      "x-forwarded-proto": "https",
      "x-forwarded-for": "203.0.113.42",
    };
  }
  server.on("request", (request, response) => {
    const upstream = http.request(
      {
        hostname: "127.0.0.1",
        port: backendPort,
        method: request.method,
        path: request.url,
        headers: headers(request),
      },
      (result) => {
        response.writeHead(result.statusCode, result.headers);
        result.on("error", () => response.destroy());
        result.pipe(response);
      },
    );
    upstream.on("error", () => {
      if (!response.headersSent && !response.destroyed)
        response.writeHead(502).end();
      else response.destroy();
    });
    request.on("aborted", () => upstream.destroy());
    request.pipe(upstream);
  });
  server.on("upgrade", (request, socket, head) => {
    const upstream = net.connect(backendPort, "127.0.0.1", () => {
      let handshake = `${request.method} ${request.url} HTTP/${request.httpVersion}\r\n`;
      for (const [name, raw] of Object.entries(headers(request))) {
        for (const value of Array.isArray(raw) ? raw : [raw]) {
          if (value !== undefined) handshake += `${name}: ${value}\r\n`;
        }
      }
      upstream.write(handshake + "\r\n");
      if (head.length) upstream.write(head);
      socket.pipe(upstream).pipe(socket);
    });
    upstream.on("error", () => socket.destroy());
    upstream.once("data", (data) => {
      if (/^HTTP\/1\.[01] 101 /.test(data.toString("ascii")))
        webSocketConnections++;
    });
    socket.on("error", () => upstream.destroy());
    socket.on("close", () => upstream.destroy());
    upstream.on("close", () => socket.destroy());
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  return {
    origin: `https://127.0.0.1:${server.address().port}`,
    get webSocketConnections() {
      return webSocketConnections;
    },
    async close() {
      for (const socket of sockets) socket.destroy();
      await new Promise((resolve) => server.close(resolve));
    },
  };
}
