import { test } from "node:test";
import assert from "node:assert/strict";
import * as a from "../ops/a.js";
import * as b from "../ops/b.js";

test("sibling modules export the same names with other behaviour", () => {
  assert.equal(a.execute(3), 3);
  assert.equal(a.execute(3, { scale: 2 }), 6);
  assert.equal(b.execute(3), 3);
  assert.equal(b.execute(3, { offset: 2 }), 5);
});
