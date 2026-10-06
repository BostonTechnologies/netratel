#!/usr/bin/env node
"use strict";

const crypto = require("node:crypto");
const http = require("node:http");
const http2 = require("node:http2");
const https = require("node:https");
const fs = require("node:fs");
const assert = require("node:assert/strict");
const { performance } = require("node:perf_hooks");

const rpcPath = "/netratel.gateway.v1.AgentGateway/Connect";
const authority = "netratel.example.test";
const caPath = "/run/traefik/ca.crt";
// Only the disposable fixture accepts this value. It is never an agent credential.
const fixtureAuthorization = "Bearer netratel-disposable-fixture-only";
const sustainedMilliseconds = 195_000;
const heartbeatMilliseconds = 15_000;

// Minimal protobuf field 1 (sequence), inside the standard uncompressed gRPC envelope.
// This deliberately exercises HTTP/2 streaming, not the product AgentFrame schema.
function heartbeatFrame(sequence) {
  assert.ok(sequence > 0 && sequence < 128);
  return Buffer.from([0, 0, 0, 0, 2, 8, sequence]);
}

function readHeartbeatFrames(onFrame) {
  let pending = Buffer.alloc(0);
  return chunk => {
    pending = Buffer.concat([pending, chunk]);
    while (pending.length >= 7) {
      assert.ok(pending.subarray(0, 6).equals(Buffer.from([0, 0, 0, 0, 2, 8])), "Invalid fixture frame.");
      onFrame(pending[6]);
      pending = pending.subarray(7);
    }
    assert.ok(pending.length < 7);
  };
}

function serveDuplex(stream, headers) {
  const started = performance.now();
  const sessionId = `fixture-session-${crypto.randomUUID()}`;
  let received = 0;
  let errorCode = null;
  stream.on("error", error => { errorCode = error.code ?? "unknown"; });
  stream.once("close", () => console.log(JSON.stringify({
    event: "synthetic-gateway-disconnected", sessionId,
    elapsedMilliseconds: Math.round(performance.now() - started),
    acknowledgedFrames: received, http2ResetCode: stream.rstCode, errorCode
  })));
  stream.respond({
    ":status": 200, "content-type": "application/grpc",
    "x-netratel-fixture-responder": "fixture-h2c-9223",
    "x-netratel-fixture-session": sessionId
  }, { waitForTrailers: true });
  const authorized = headers.authorization === fixtureAuthorization && headers[":path"] === rpcPath;
  stream.on("wantTrailers", () => stream.sendTrailers({ "grpc-status": authorized ? "0" : "16" }));
  if (!authorized) {
    stream.end();
    return;
  }
  const read = readHeartbeatFrames(sequence => {
    assert.equal(sequence, received + 1, "Fixture request sequence changed.");
    received = sequence;
    stream.write(heartbeatFrame(sequence));
  });
  stream.on("data", chunk => {
    try { read(chunk); }
    catch { stream.close(http2.constants.NGHTTP2_PROTOCOL_ERROR); }
  });
  stream.on("end", () => { if (!stream.destroyed) stream.end(); });
}

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
  grpc.on("sessionError", () => {});
  grpc.on("stream", (stream, headers) => {
    if (headers["x-netratel-fixture-sustained"] === "1") {
      serveDuplex(stream, headers);
      return;
    }
    stream.on("error", () => {});
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

function requestHttp1(path, target = authority) {
  return new Promise((resolve, reject) => {
    let timer;
    const request = https.request({
      hostname: target,
      servername: authority,
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

async function runProbe(target = authority) {
  const client = http2.connect(`https://${target}`, { ca: fs.readFileSync(caPath), servername: authority });
  const sessionFailure = new Promise((_resolve, reject) => client.once("error", reject));
  const timeout = setTimeout(() => client.destroy(new Error("Traefik HTTP/2 gRPC request timed out.")), 15_000);
  try {
    const grpcStream = client.request({
      ":method": "POST",
      ":path": rpcPath,
      ":authority": authority,
      "content-type": "application/grpc",
      te: "trailers"
    });
    const grpc = await Promise.race([collectHttp2Response(grpcStream), sessionFailure]);
    const grpcHttpStatus = Number(grpc.headers[":status"]);
    assert.equal(grpcHttpStatus, 200, "Synthetic gRPC fixture did not return HTTP 200.");
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
      ":authority": authority,
      "content-type": "application/grpc",
      te: "trailers"
    });
    const nearMissGrpc = await Promise.race([collectHttp2Response(nearMissStream), sessionFailure]);
    assertNativeNearMiss(nearMissGrpc, nearMissPath);
    const unauthenticated = await Promise.race([collectHttp2Response(client.request({
      ":method": "POST", ":path": rpcPath, ":authority": authority,
      "content-type": "application/grpc", te: "trailers", "x-netratel-fixture-sustained": "1"
    })), sessionFailure]);
    requireHeader(unauthenticated.trailers, "grpc-status", "16");
    const restPath = "/api/v2/client-presence/";
    const rest = await requestHttp1(restPath, target);
    assertRestResponse(rest, restPath);

    console.log(JSON.stringify({
      proxy: target,
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

function probeDuplex(profile, target, expectedIngress) {
  return new Promise((resolve, reject) => {
    const started = performance.now();
    const secure = target.startsWith("https:");
    const client = http2.connect(target, secure ? { ca: fs.readFileSync(caPath), servername: authority } : {});
    let sessionError = null;
    client.on("error", error => { sessionError = error.code ?? "unknown"; });
    const request = client.request({
      ":method": "POST", ":path": rpcPath, ":authority": authority,
      "content-type": "application/grpc", te: "trailers",
      authorization: fixtureAuthorization, "x-netratel-fixture-sustained": "1"
    });
    let received = 0;
    let sent = 0;
    let lastAcknowledgedAt = null;
    let sessionId = null;
    let httpStatus = null;
    let grpcStatus = null;
    let errorCode = null;
    let validationError = null;
    let ended = false;
    let completedRequest = false;
    let interval;
    const finish = () => {
      clearInterval(interval);
      clearTimeout(completion);
      clearTimeout(deadline);
      const elapsedMilliseconds = Math.round(performance.now() - started);
      const evidence = {
        profile, elapsedMilliseconds, intendedLifetimeMilliseconds: sustainedMilliseconds,
        heartbeatCadenceMilliseconds: heartbeatMilliseconds,
        sentFrames: sent, acknowledgedFrames: received, latestAcknowledgedSequence: received,
        lastAcknowledgedAgeMilliseconds: lastAcknowledgedAt === null ? null : Math.round(performance.now() - lastAcknowledgedAt),
        sessionId, httpStatus, grpcStatus, http2ResetCode: request.rstCode,
        errorCode: errorCode ?? sessionError, ended, completedRequest, reconnects: 0
      };
      client.destroy();
      if (validationError) reject(validationError);
      else resolve(evidence);
    };
    request.on("response", headers => {
      try {
        httpStatus = Number(headers[":status"]);
        assert.equal(httpStatus, 200);
        requireHeader(headers, "x-netratel-fixture-responder", "fixture-h2c-9223");
        if (expectedIngress) requireHeader(headers, "x-netratel-ingress-route", expectedIngress);
        sessionId = headers["x-netratel-fixture-session"];
        assert.match(sessionId ?? "", /^fixture-session-[0-9a-f-]{36}$/);
      } catch (error) { validationError = error; request.close(); }
    });
    request.on("trailers", headers => { grpcStatus = headers["grpc-status"] ?? null; });
    const read = readHeartbeatFrames(sequence => {
      assert.equal(sequence, received + 1, "Fixture ACK sequence changed.");
      assert.ok(sequence <= sent, "Fixture ACK precedes request.");
      received = sequence;
      lastAcknowledgedAt = performance.now();
    });
    request.on("data", chunk => {
      try { read(chunk); }
      catch (error) { validationError = error; request.close(); }
    });
    request.on("error", error => { errorCode = error.code ?? "unknown"; });
    request.on("end", () => { ended = true; });
    request.once("close", finish);
    const send = () => { if (!request.destroyed) request.write(heartbeatFrame(++sent)); };
    send();
    interval = setInterval(send, heartbeatMilliseconds);
    const completion = setTimeout(() => {
      completedRequest = true;
      clearInterval(interval);
      send();
      request.end();
    }, sustainedMilliseconds);
    const deadline = setTimeout(() => {
      validationError = new Error(`Synthetic ${profile} duplex exceeded its bounded deadline.`);
      request.close();
    }, sustainedMilliseconds + 10_000);
  });
}

async function runSustainedProbe() {
  const profiles = await Promise.all([
    probeDuplex("direct-h2c-control", "http://gateway-fixture:9223", null),
    probeDuplex("traefik-default-read-timeout", `https://${authority}`, "grpc-h2c-9223"),
    probeDuplex("traefik-streaming-read-timeout-zero", "https://gateway-streaming", "grpc-h2c-9223")
  ]);
  for (const evidence of profiles) {
    console.log(JSON.stringify({ event: "synthetic-sustained-gateway", ...evidence }));
    if (evidence.profile === "traefik-default-read-timeout") {
      assert.ok(evidence.elapsedMilliseconds >= 50_000 && evidence.elapsedMilliseconds <= 75_000,
        "The default fixture must reproduce a roughly 60-second body deadline.");
      assert.ok(evidence.acknowledgedFrames >= 3, "Default fixture failed before active duplex ACKs.");
      assert.equal(evidence.http2ResetCode, http2.constants.NGHTTP2_INTERNAL_ERROR,
        "The default fixture must reproduce the observed HTTP/2 INTERNAL_ERROR reset.");
      assert.ok(evidence.lastAcknowledgedAgeMilliseconds <= 25_000,
        "The default fixture must remain actively acknowledged before its body deadline.");
      assert.equal(evidence.completedRequest, false, "Default fixture unexpectedly survived its body deadline.");
      assert.notEqual(evidence.grpcStatus, "0", "Default fixture unexpectedly completed successfully.");
    } else {
      assert.ok(evidence.elapsedMilliseconds >= sustainedMilliseconds);
      assert.equal(evidence.completedRequest, true);
      assert.equal(evidence.ended, true);
      assert.equal(evidence.grpcStatus, "0");
      assert.equal(evidence.http2ResetCode, 0);
      assert.equal(evidence.errorCode, null);
      assert.equal(evidence.acknowledgedFrames, evidence.sentFrames);
      assert.ok(evidence.acknowledgedFrames >= 14);
      assert.ok(evidence.lastAcknowledgedAgeMilliseconds < 5_000);
    }
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
  runProbe(process.argv[3]).catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
} else if (process.argv[2] === "sustained") {
  runSustainedProbe().catch(error => {
    console.error(error.message);
    process.exitCode = 1;
  });
} else {
  console.error("Usage: traefik-upstream.js server|probe [proxy-host]|sustained");
  process.exitCode = 2;
}
