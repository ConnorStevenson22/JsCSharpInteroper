import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { iconNames, toRgba } from "../styles.js";

test("iconNames matches the IconName union written out in styles.d.ts", () => {
  // R10: more than 64 members.
  const dts = readFileSync(new URL("../styles.d.ts", import.meta.url), "utf8");
  const union = /export type IconName =([^;]*);/.exec(dts)[1];
  const declared = [...union.matchAll(/"([^"]+)"/g)].map((m) => m[1]);
  assert.equal(iconNames.length, 120);
  assert.deepEqual(declared, iconNames);
});

test("toRgba accepts every ColorInput case", () => {
  assert.deepEqual(toRgba("#ff8000"), { r: 255, g: 128, b: 0, a: 1 });
  assert.deepEqual(toRgba("Blue"), { r: 0, g: 0, b: 255, a: 1 });
  assert.deepEqual(toRgba([1, 2, 3]), { r: 1, g: 2, b: 3, a: 1 });
  assert.deepEqual(toRgba({ r: 1, g: 2, b: 3, a: 0.5 }), { r: 1, g: 2, b: 3, a: 0.5 });
  assert.throws(() => toRgba("nope"), RangeError);
});
