#!/usr/bin/env node
"use strict";

const crypto = require("node:crypto");
const http = require("node:http");
const http2 = require("node:http2");
const https = require("node:https");
const fs = require("node:fs");

const rpcPath = "/netratel.gateway.v1.AgentGateway/Connect";
const authority = "netratel.example.test";
const caPath = "/run/traefik/ca.crt";

function startFixture() {
  const rest = http.createServer((request, response) => {
    const correlationId = `fixture-rest-${crypto.randomUUID()}`;
    response.writeHead(403, {
      "content-type": "application/problem+json",
      "x-netratel-fixture-responder": "fixture-rest-9222",
      "x-netratel-fixture-path": request.url,
      "x-correlation-id": correlationId
    });
    response.end(JSON.stringify({ status: 403, title: "Synthetic fixture denial" }));
  });

  const grpc = http2.createServer();
  grpc.on("stream", (stream, headers) => {
    const correlationId = `fixture-grpc-${crypto.randomUUID()}`;
    stream.respond({
      ":status": 200,
      "content-type": "application/grpc",
      "x-netratel-fixture-responder": "fixture-h2c-9223",
      "x-netratel-fixture-path": headers[":path"]
    }, { waitForTrailers: true });
    stream.on("wantTrailers", () => {
      stream.sendTrailers({
        "grpc-status": "7",
        "grpc-message": "synthetic-fixture-permission-denied",
        "x-correlation-id": correlationId
      });
    });
    stream.end();
  });

  rest.listen(9222, "0.0.0.0");
  grpc.listen(9223, "0.0.0.0");
}

function collectHttp2Response(stream) {
  return new Promise((resolve, reject) => {
    let headers;
    let trailers = {};
    const body = [];
    stream.on("response", value => { headers = value; });
    stream.on("trailers", value => { trailers = value; });
    stream.on("data", value => body.push(value));
    stream.on("error", reject);
    stream.on("end", () => resolve({ headers: headers ?? {}, trailers, body: Buffer.concat(body) }));
    stream.end(Buffer.from([0, 0, 0, 0, 0]));
  });
}

function requestHttp1(path) {
  return new Promise((resolve, reject) => {
    let timer;
    const request = https.request({
      hostname: authority,
      port: 443,
      path,
      method: "GET",
      ca: fs.readFileSync(caPath),
      headers: { host: authority }
    }, response => {
      const body = [];
      response.on("data", value => body.push(value));
      response.on("error", error => {
        clearTimeout(timer);
        reject(error);
      });
      response.on("end", () => {
        clearTimeout(timer);
        resolve({ status: response.statusCode, headers: response.headers, body: Buffer.concat(body) });
      });
    });
    timer = setTimeout(() => request.destroy(new Error(`REST request timed out for ${path}.`)), 10_000);
    request.on("error", error => {
      clearTimeout(timer);
      reject(error);
    });
    request.end();
  });
}

function requireHeader(headers, name, expected) {
  if (headers[name] !== expected) {
    throw new Error(`Expected ${name}=${expected}; received ${headers[name] ?? "<missing>"}.`);
  }
}

async function runProbe() {
  const client = http2.connect(`https://${authority}`, { ca: fs.readFileSync(caPath) });
  const sessionFailure = new Promise((_resolve, reject) => client.once("error", reject));
  const timeout = setTimeout(() => client.destroy(new Error("Traefik HTTP/2 gRPC request timed out.")), 15_000);
  try {
    const grpcStream = client.request({
      ":method": "POST",
      ":path": rpcPath,
      "content-type": "application/grpc",
      te: "trailers"
    });
    const grpc = await Promise.race([collectHttp2Response(grpcStream), sessionFailure]);
    const grpcHttpStatus = Number(grpc.headers[":status"]);
    requireHeader(grpc.headers, "x-netratel-ingress-route", "grpc-h2c-9223");
    requireHeader(grpc.headers, "x-netratel-fixture-responder", "fixture-h2c-9223");
    requireHeader(grpc.headers, "x-netratel-fixture-path", rpcPath);
    requireHeader(grpc.trailers, "grpc-status", "7");
    if (grpcHttpStatus !== 200 || grpc.headers[":status"] !== 200 || grpc.body.length !== 0) {
      throw new Error(`Expected the gRPC fixture denial to use HTTP/2 200 and grpc-status 7; got HTTP ${grpcHttpStatus}.`);
    }
    if (!/^fixture-grpc-[0-9a-f-]{36}$/.test(grpc.trailers["x-correlation-id"] ?? "")) {
      throw new Error("The gRPC fixture response lacks a sanitized correlation identifier.");
    }

    const nearMissPath = "/netratel.gateway.v1x.AgentGateway/Connect";
    const nearMissStream = client.request({
      ":method": "POST",
      ":path": nearMissPath,
      "content-type": "application/grpc",
      te: "trailers"
    });
    const nearMissGrpc = await Promise.race([collectHttp2Response(nearMissStream), sessionFailure]);
    assertNativeNearMiss(nearMissGrpc, nearMissPath);
    const restPath = "/api/v2/client-presence/";
    const rest = await requestHttp1(restPath);
    assertRestResponse(rest, restPath);

    console.log(JSON.stringify({
      grpc: {
        requestPath: rpcPath,
        httpVersion: "2.0",
        httpStatus: grpcHttpStatus,
        grpcStatus: grpc.trailers["grpc-status"],
        ingressRoute: grpc.headers["x-netratel-ingress-route"],
        responder: grpc.headers["x-netratel-fixture-responder"],
        correlationId: grpc.trailers["x-correlation-id"]
      },
      nativeNearMiss: nativeNearMissEvidence(nearMissGrpc),
      rest: restEvidence(rest)
    }));
  } finally {
    clearTimeout(timeout);
    client.destroy();
  }
}

function assertRestResponse(response, path) {
  if (response.status !== 403) {
    throw new Error(`Expected fixture REST HTTP 403 for ${path}; got HTTP ${response.status}.`);
  }
  requireHeader(response.headers, "x-netratel-ingress-route", "rest-http-9222");
  requireHeader(response.headers, "x-netratel-fixture-responder", "fixture-rest-9222");
  requireHeader(response.headers, "x-netratel-fixture-path", path);
  if (!/^fixture-rest-[0-9a-f-]{36}$/.test(response.headers["x-correlation-id"] ?? "")) {
    throw new Error(`The REST fixture response for ${path} lacks a sanitized correlation identifier.`);
  }
  if (response.headers.location) {
    throw new Error(`The REST fixture route unexpectedly redirected to ${response.headers.location}.`);
  }
}

function assertNativeNearMiss(response, path) {
  if (Number(response.headers[":status"]) !== 403) {
    throw new Error(`Expected the native-shaped near-miss to reach fixture REST HTTP 403 for ${path}; got HTTP ${response.headers[":status"] ?? "<missing>"}.`);
  }
  requireHeader(response.headers, "x-netratel-ingress-route", "rest-http-9222");
  requireHeader(response.headers, "x-netratel-fixture-responder", "fixture-rest-9222");
  requireHeader(response.headers, "x-netratel-fixture-path", path);
  if (response.trailers["grpc-status"] !== undefined) {
    throw new Error(`The REST fallback unexpectedly produced grpc-status for ${path}.`);
  }
  if (!/^fixture-rest-[0-9a-f-]{36}$/.test(response.headers["x-correlation-id"] ?? "")) {
    throw new Error(`The native-shaped near-miss lacks a sanitized REST responder correlation identifier.`);
  }
}

function nativeNearMissEvidence(response) {
  return {
    httpVersion: "2.0",
    httpStatus: Number(response.headers[":status"]),
    grpcStatus: response.trailers["grpc-status"] ?? null,
    ingressRoute: response.headers["x-netratel-ingress-route"],
    responder: response.headers["x-netratel-fixture-responder"],
    requestPath: response.headers["x-netratel-fixture-path"],
    correlationId: response.headers["x-correlation-id"]
  };
}

function restEvidence(response) {
  return {
    httpVersion: "1.1",
    httpStatus: response.status,
    ingressRoute: response.headers["x-netratel-ingress-route"],
    responder: response.headers["x-netratel-fixture-responder"],
    requestPath: response.headers["x-netratel-fixture-path"],
    correlationId: response.headers["x-correlation-id"]
  };
}

if (process.argv[2] === "server") {
  startFixture();
} else if (process.argv[2] === "probe") {
  runProbe().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
} else {
  console.error("Usage: traefik-upstream.js server|probe");
  process.exitCode = 2;
}
