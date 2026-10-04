#!/usr/bin/env node
"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const sourcePath = process.argv[2] || path.resolve(__dirname, "../../../src/NetRatel/NetRatel.Web/wwwroot/download.js");
const source = fs.readFileSync(sourcePath, "utf8");

for (const scenario of ["network-error", "unexpected-abort", "marked-session-loss", "explicit-cancel", "success"]) {
  test(`native upload ${scenario} sends once and settles once`, async () => {
    const requests = [];
    const retryTimers = [];
    let cleanups = 0;
    class TrackedMap extends Map {
      delete(key) {
        if (key === "upload-operation") cleanups++;
        return super.delete(key);
      }
    }
    const window = {
      setTimeout: callback => { retryTimers.push(callback); return retryTimers.length; },
      clearTimeout: () => {}
    };
    class Request {
      constructor() { this.upload = {}; requests.push(this); }
      open(method, url) { assert.equal(method, "PUT"); assert.equal(url, "/upload"); }
      send(file) { assert.equal(file.size, 3); }
      abort() { this.onabort(); }
      getResponseHeader() { return "true"; }
    }
    vm.runInNewContext(source, { window, XMLHttpRequest: Request, Map: TrackedMap });
    let settlements = 0;
    let outcome;
    const completion = window.netratelFileTransfers.upload(
      { files: [{ size: 3 }] }, "/upload", "upload-operation", null).then(
        () => { settlements++; outcome = "completed"; },
        error => { settlements++; outcome = error.message; });
    const request = requests[0];
    if (scenario === "network-error") request.onerror();
    else if (scenario === "unexpected-abort") request.onabort();
    else if (scenario === "marked-session-loss") { request.status = 409; request.onload(); }
    else if (scenario === "explicit-cancel") window.netratelFileTransfers.cancel("upload-operation");
    else { request.status = 204; request.onload(); }

    // Drive a queued retry, if present, to reproduce the old full-PUT replay
    // without wall-clock waits. Settle that negative fixture before asserting.
    if (retryTimers.length) retryTimers.shift()();
    if (requests.length > 1) { requests[1].status = 200; requests[1].onload(); }
    await completion;
    assert.equal(requests.length, 1, "an uncertain mutation must not be issued twice");
    assert.equal(retryTimers.length, 0, "the finished upload must leave no retry timer");
    assert.equal(window.netratelFileTransfers.uploads.size, 0, "the finished upload must release its owner");
    if (scenario === "success") assert.equal(outcome, "completed");
    else if (scenario === "explicit-cancel") assert.match(outcome, /cancelled/i);
    else assert.match(outcome, /outcome is unknown/i);

    // Delayed callbacks from the finished physical request cannot settle or
    // clean up the operation again, or create a replacement mutation.
    request.status = 200;
    request.onload();
    request.onerror();
    request.onabort();
    await Promise.resolve();
    assert.equal(settlements, 1);
    assert.equal(cleanups, 1);
    assert.equal(requests.length, 1);
    assert.equal(retryTimers.length, 0);
  });
}
