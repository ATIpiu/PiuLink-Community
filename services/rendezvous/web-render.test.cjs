const test = require("node:test");
const assert = require("node:assert/strict");
const { webcrypto } = require("node:crypto");
const { readFileSync } = require("node:fs");
const { join } = require("node:path");
const vm = require("node:vm");

test("slow image decode paints the current frame then the newest queued frame", async () => {
  const elements = new Map();
  const painted = [], closed = [], decodes = [];
  function element(id) {
    if (!elements.has(id)) elements.set(id, {
      hidden: true, addEventListener() {}, focus() {},
      getContext: () => ({drawImage: image => painted.push(image.number)}),
    });
    return elements.get(id);
  }
  const channel = {};
  let ready;
  const connected = new Promise(resolve => { ready = resolve; });
  class Peer {
    createDataChannel() { return channel; }
    createOffer() { ready(); return new Promise(() => {}); }
    close() {}
  }
  const window = {isSecureContext: true, RTCPeerConnection: Peer, crypto: webcrypto, addEventListener() {}};
  vm.runInNewContext(readFileSync(join(__dirname, "web.js"), "utf8"), {
    window, document: {getElementById: element}, history: {replaceState() {}},
    location: {hash: "#" + "0".repeat(64) + "." + "1".repeat(64), pathname: "/", hostname: "localhost"},
    crypto: webcrypto, RTCPeerConnection: Peer, Blob, TextEncoder, Date, setTimeout,
    createImageBitmap: async blob => {
      const number = new Uint8Array(await blob.arrayBuffer())[0];
      await new Promise(resolve => decodes.push(resolve));
      return {number, width: 1, height: 1, close: () => closed.push(number)};
    },
  });
  element("connect").onclick();
  await connected;
  const frame = number => {
    channel.onmessage({data: JSON.stringify({size: 1})});
    return channel.onmessage({data: new Uint8Array([number]).buffer});
  };
  const rendering = frame(1);
  frame(2); frame(3);
  await new Promise(setImmediate);
  assert.equal(decodes.length, 1);
  decodes.shift()();
  await new Promise(setImmediate);
  assert.deepEqual(painted, [1]);
  decodes.shift()();
  await rendering;
  assert.deepEqual(painted, [1, 3]);
  const pending = frame(4);
  await new Promise(setImmediate);
  element("disconnect").onclick();
  decodes.shift()();
  await pending;
  assert.deepEqual(painted, [1, 3]);
  assert.deepEqual(closed, [1, 3, 4]);
});
