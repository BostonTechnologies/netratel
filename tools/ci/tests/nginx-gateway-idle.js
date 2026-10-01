#!/usr/bin/env node
"use strict";

const http2 = require("node:http2");
const fs = require("node:fs");
const assert = require("node:assert/strict");
const frame = Buffer.from([0, 0, 0, 0, 0]);
const idleMilliseconds = 65_000;

if (process.argv[2] === "server") {
  const server = http2.createServer();
  server.on("stream", stream => {
    let received = 0;
    stream.on("error", () => {});
    stream.respond({ ":status": 200, "content-type": "application/grpc" }, { waitForTrailers: true });
    stream.on("wantTrailers", () => stream.sendTrailers({ "grpc-status": "0" }));
    stream.on("data", chunk => { received += chunk.length; });
    stream.write(frame);
    stream.on("end", () => {
      if (received !== frame.length * 2) {
        stream.close(http2.constants.NGHTTP2_PROTOCOL_ERROR);
        return;
      }
      stream.end(frame);
    });
  });
  server.listen(9223, "0.0.0.0");
} else if (process.argv[2] === "probe") {
  const session = http2.connect("https://netratel.example.test", {
    ca: fs.readFileSync("/fixture/tls.crt")
  });
  const started = Date.now();
  let idleTimer;
  let deadline;
  new Promise((resolve, reject) => {
    session.on("error", reject);
    const request = session.request({
      ":method": "POST",
      ":path": "/netratel.gateway.v1.AgentCommandGateway/Connect",
      "content-type": "application/grpc",
      te: "trailers"
    });
    let received = 0;
    let status;
    let grpcStatus;
    request.on("response", headers => { status = headers[":status"]; });
    request.on("trailers", headers => { grpcStatus = headers["grpc-status"]; });
    request.on("error", reject);
    request.on("data", chunk => {
      received += chunk.length;
      if (!idleTimer) {
        // Keep the request body open without sending heartbeats or retries.
        // A command may arrive after a long idle period on the same stream.
        idleTimer = setTimeout(() => request.end(frame), idleMilliseconds);
      }
    });
    request.on("end", () => {
      try {
        assert.equal(status, 200);
        assert.equal(grpcStatus, "0");
        assert.equal(received, frame.length * 2);
        assert.ok(Date.now() - started >= idleMilliseconds);
        resolve();
      } catch (error) { reject(error); }
    });
    deadline = setTimeout(() => reject(new Error("Idle gateway probe exceeded its deadline.")), 80_000);
    request.write(frame);
  }).then(() => {
    console.log("Nginx gateway preserved the same bidirectional stream across 65 seconds of request-body silence.");
  }).catch(error => {
    console.error("Nginx gateway idle regression failed:", error.code ?? error.name, "after", Date.now() - started, "ms");
    process.exitCode = 1;
  }).finally(() => {
    clearTimeout(idleTimer);
    clearTimeout(deadline);
    session.destroy();
  });
} else {
  throw new Error("Expected server or probe mode.");
}
