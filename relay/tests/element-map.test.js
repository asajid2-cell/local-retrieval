'use strict';
// The strip half of OSC 21338, driven by the canonical fixture.
//
// The fixture is the acceptance target, not a sample: every strip entry is an ordered chunk list
// with the expected text, frames, carry and cumulative dropped count after every chunk. The codec
// entries are checked against the same validator, so the relay's idea of "a conforming frame" is
// proven to be the emitter's idea, not a second opinion. No vendored copy: the file is read in
// place from docs/.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {
  createStripState, stripElementMap, clipFrame, isFrame, MAX_PAYLOAD_BYTES,
} = require('../element-map');

const repo = path.join(__dirname, '..', '..');
const vectors = JSON.parse(fs.readFileSync(path.join(repo, 'docs', 'element-map-vectors.json'), 'utf8'));

const hex = s => (s ? Buffer.from(s, 'hex') : Buffer.alloc(0));
const hexOf = b => Buffer.from(b).toString('hex');

function runChunks(chunks) {
  const state = createStripState();
  return chunks.map(chunk => {
    const { text, frames } = stripElementMap(hex(chunk), state);
    return { text: hexOf(text), frames, carry: hexOf(state.carry), dropped: state.dropped };
  });
}

test('fixture is the pinned shape', () => {
  assert.equal(vectors.meta.osc, 21338);
  assert.equal(vectors.meta.max_payload_bytes, MAX_PAYLOAD_BYTES);
  assert.ok(vectors.strip.entries.length > 0);
});

for (const entry of vectors.strip.entries) {
  test(`strip: ${entry.name}`, () => {
    const got = runChunks(entry.chunks);
    assert.equal(got.length, entry.expect.length, 'chunk count');
    entry.expect.forEach((want, n) => {
      const actual = got[n];
      const label = `${entry.name} after chunk ${n + 1}`;
      assert.equal(actual.text, want.text_hex, `${label}: text`);
      assert.deepEqual(actual.frames, want.frames, `${label}: frames`);
      assert.equal(actual.carry, want.carry_hex, `${label}: carry`);
      assert.equal(actual.dropped, want.dropped, `${label}: dropped`);
    });
  });
}

test('codec: every emitted sequence decodes to the frame it was built from', () => {
  const emitted = vectors.codec.entries.filter(e => e.ok);
  assert.ok(emitted.length > 0);
  for (const entry of emitted) {
    const state = createStripState();
    const { text, frames } = stripElementMap(hex(entry.sequence_hex), state);
    assert.equal(text.length, 0, `${entry.name}: the sequence must not reach the text stream`);
    assert.equal(frames.length, 1, `${entry.name}: exactly one frame`);
    assert.deepEqual(frames[0], entry.frame, `${entry.name}: decoded frame`);
    assert.equal(state.dropped, 0, `${entry.name}: not a drop`);
    assert.equal(state.carry.length, 0, `${entry.name}: nothing carried`);
  }
});

test('codec: the oversize frame is never on the wire, so the stripper never sees it', () => {
  for (const entry of vectors.codec.entries.filter(e => !e.ok)) {
    assert.equal(entry.payload_bytes, MAX_PAYLOAD_BYTES + 1, `${entry.name}: boundary payload`);
    assert.equal(entry.reason, 'too-large');
    assert.equal(entry.bytes, MAX_PAYLOAD_BYTES + 1);
    assert.equal(entry.sequence_hex, undefined, `${entry.name}: a refused frame writes nothing`);
  }
});

test('a stream with no introducer is byte-identical', () => {
  const samples = [
    Buffer.from('', 'latin1'),
    Buffer.from('plain text\r\n', 'latin1'),
    Buffer.from('\u001b[31mred\u001b[0m\u001b[2J\u001b[H', 'latin1'),
    Buffer.concat([Buffer.from('\u001b]0;title\u0007', 'latin1'), Buffer.from('x'.repeat(70000))]),
  ];
  for (const sample of samples) {
    const state = createStripState();
    const { text, frames } = stripElementMap(sample, state);
    assert.deepEqual(text, sample);
    assert.deepEqual(frames, []);
    assert.equal(state.carry.length, 0);
    assert.equal(state.dropped, 0);
  }
});

test('a chunk boundary inside a frame is resolved on the next chunk', () => {
  const seq = hex(vectors.codec.entries[0].sequence_hex);
  const state = createStripState();
  const head = stripElementMap(seq.subarray(0, 5), state);
  assert.equal(head.text.length, 0);
  assert.equal(head.frames.length, 0);
  assert.equal(state.carry.length, 5);
  const tail = stripElementMap(seq.subarray(5), state);
  assert.equal(tail.frames.length, 1);
  assert.equal(state.carry.length, 0);
});

test('clipFrame keeps rects inside the grid and drops the rest', () => {
  const frame = {
    v: 1, gen: 4, cols: 10, rows: 4,
    els: [
      { id: 'a', role: 'box', r: [-2, -1, 5, 3] },      // clipped to [0,0,3,2]
      { id: 'b', role: 'text', r: [8, 3, 5, 5] },        // clipped to [8,3,2,1]
      { id: 'c', role: 'text', r: [20, 20, 4, 4] },      // fully outside, dropped
      { id: 'd', role: 'text', r: [1, 1, 0, 3] },        // zero width, dropped
      { id: 'a', role: 'text', r: [1, 1, 2, 1], label: 'last wins' },
    ],
  };
  assert.deepEqual(clipFrame(frame), {
    v: 1, gen: 4, cols: 10, rows: 4,
    els: [
      { id: 'b', role: 'text', r: [8, 3, 2, 1] },
      { id: 'a', role: 'text', r: [1, 1, 2, 1], label: 'last wins' },
    ],
  });
  assert.equal(clipFrame({ ...frame, v: 2 }), null);
});

test('clipFrame tolerates a map wider than the grid it was computed against', () => {
  const frame = { v: 1, gen: 1, cols: 80, rows: 24, els: [{ id: 'e', role: 'text', r: [0, 0, 200, 1] }] };
  assert.deepEqual(clipFrame(frame).els, [{ id: 'e', role: 'text', r: [0, 0, 80, 1] }]);
});

test('isFrame rejects the shapes the fixture calls corrupt', () => {
  assert.equal(isFrame(null), false);
  assert.equal(isFrame({ v: 2, gen: 1, cols: 1, rows: 1, els: [] }), false);
  assert.equal(isFrame({ v: 1, gen: 1, cols: 1, rows: 1, els: [{ id: 'x', role: 'text', r: [0, 0, 1, 1], act: { t: 'ptr', x: 1 } }] }), false);
  assert.equal(isFrame({ v: 1, gen: 1, cols: 1, rows: 1, els: [{ id: 'x', role: 'text', r: [0, 0, 1, 1], act: { t: 'ptr', x: 0, y: 0, b: 3 } }] }), false);
  assert.equal(isFrame({ v: 1, gen: 1, cols: 1, rows: 1, els: [{ id: 'x', role: 'text', r: [0, 0, 1, 1], act: {} }] }), false);
  assert.equal(isFrame({ v: 1, gen: 1, cols: 1, rows: 1, els: [{ id: 'x', role: 'text', r: [0, 0, 1, 1], act: { t: 'key' } }] }), false);
  assert.equal(isFrame({ v: 1, gen: 1, cols: 1, rows: 1, els: [{ id: 'x', role: 'text', r: [0, 0, 1, 1], act: { t: 'focus' } }] }), true);
  assert.equal(isFrame({ v: 1, gen: 1, cols: 1, rows: 1, els: [{ id: 'x', role: 'text', r: [0, 0, 1, 1], act: { t: 'none' } }] }), true);
  assert.equal(isFrame({ v: 1, gen: 1, cols: 1, rows: 1, els: [{ id: 'x', role: 'text', r: [0, 0, 1, 1] }] }), true);
});
