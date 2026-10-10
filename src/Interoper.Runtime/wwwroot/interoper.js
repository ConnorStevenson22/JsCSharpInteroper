// The JS half of the Interoper runtime: a handle table, the ops, and the wire format (docs/design.md §3.2–§3.5).
// .NET sends one op as JSON and gets back {"v": result}, {} for undefined, or {"e": error}.

const handles = new Map(); // id -> object
const ids = new WeakMap(); // object -> id, so one object always has one id
const subscriptions = new Set(); // handle ids whose release disposes the object
const modules = new Map(); // module specifier -> imported module
const UNDEFINED = Object.freeze({ $u: 1 });
let nextId = 1;
let meta = { classes: {} };
let classIdsByName = new Map();
let callbackSink = null;
let fastPathError = null; // { error } until takeError reads it
let syncOnly = false; // set while invokeSync runs: nothing may start async work
const PROMISE_IN_SYNC = "The op returned a Promise; send it through invoke.";

class InteroperError extends Error {
  name = "InteroperError";
}

class BatchError extends Error {
  name = "BatchError";
  constructor(errors, count) {
    super(`${errors.length} of ${count} batched ops failed`);
    this.errors = errors;
  }
}

// --- Start ---

// metadataSpecifier: a module whose default export is the generated metadata table:
//   { classNameProperty: "className", classes: { [classId]: { module, export, name, value?, members } } }
// sink(callbackId, argsJson) calls a .NET callback and returns its result as JSON text ("null" for null), null or
// nothing for undefined, or a Promise of either.
// Value-like classes are imported here so `$c` can be built synchronously, in sync mode too.
export async function start(metadataSpecifier, sink) {
  meta = (await import(metadataSpecifier)).default;
  classIdsByName = new Map(Object.entries(meta.classes).map(([id, c]) => [c.name, Number(id)]));
  callbackSink = sink;
  const valueClasses = Object.keys(meta.classes).filter((id) => meta.classes[id].value);
  await Promise.all(valueClasses.map((id) => load(meta.classes[id].module)));
}

// The Server/Hybrid form of start: callbacks go through one DotNetObjectReference.
export function startWithDotNetObject(metadataSpecifier, dotNetObject) {
  return start(metadataSpecifier, (id, argsJson) => dotNetObject.invokeMethodAsync("InvokeCallback", id, argsJson));
}

// --- Entry points ---

// For [JSImport] in sync mode. Modules must already be loaded. An op that returns a Promise is a caller
// error, reported after the op has run.
export function invokeSync(opJson) {
  const response = respondWith(true, opJson);
  return isThenable(response) ? failure(new InteroperError(PROMISE_IN_SYNC)) : response;
}

// For IJSRuntime.InvokeAsync and Promise-returning ops: never rejects; errors come back as {"e": …}.
export async function invoke(opJson) {
  return respondWith(false, opJson);
}

// Typed fast paths for [JSImport]: one JS function serves number, string and bool signatures, for members
// declared as non-null primitives, so null or undefined is an error. Errors are thrown; .NET then reads them
// with takeError, since [JSImport] keeps only the message.
export function getValue(handle, name) {
  return fastPath(() => {
    const value = target(handle)[name];
    if (value == null) throw new InteroperError(`${name} is ${value}; a typed fast path needs a value`);
    return value;
  });
}

export function setValue(handle, name, value) {
  fastPath(() => { target(handle)[name] = value; });
}

// What the last fast path call threw, as an error envelope, once; null if it threw nothing.
export function takeError() {
  const taken = fastPathError;
  fastPathError = null;
  return taken && failure(taken.error);
}

// The number of live handles, for leak tests.
export function census() {
  return handles.size;
}

function fastPath(fn) {
  fastPathError = null;
  try {
    return fn();
  } catch (error) {
    fastPathError = { error };
    throw error;
  }
}

// The flag is restored afterwards because a callback into .NET can re-enter either entry point.
function respondWith(sync, opJson) {
  const outer = syncOnly;
  syncOnly = sync;
  try {
    return respond(opJson);
  } finally {
    syncOnly = outer;
  }
}

// A JSON envelope, or a Promise of one that never rejects.
function respond(opJson) {
  const success = (v) => JSON.stringify(v === undefined ? {} : { v });
  try {
    const result = run(parse(opJson));
    return isThenable(result) ? Promise.resolve(result).then(success).catch(failure) : success(result);
  } catch (e) {
    return failure(e);
  }
}

function failure(e) {
  return JSON.stringify({ e: toError(e) });
}

function toError(e) {
  try {
    if (!(e instanceof Error)) return { name: "Error", message: String(e) };
    const error = { name: e.name, message: e.message, stack: e.stack };
    if (e instanceof BatchError) error.errors = e.errors;
    return error;
  } catch {
    return { name: "Error", message: "Unknown error" }; // e.g. an object with no string form
  }
}

// --- Ops ---
// Each returns its result in wire form, or a Promise of it.

const ops = {
  create: (op) => {
    const entry = classEntry(op.t);
    return withModule(entry.module, (m) => toWire(new m[entry.export](...(op.args ?? []))));
  },
  get: (op) => withTarget(op, (o) => toWire(o[op.name], op.byValue)),
  set: (op) => withTarget(op, (o) => { o[op.name] = op.value; }),
  call: (op) => withTarget(op, (o) => then(o[op.name](...(op.args ?? [])), (r) => toWire(r, op.byValue))),
  snapshot: (op) => toData(target(op.h)),
  apply: (op) => { const o = target(op.h); for (const [k, v] of Object.entries(op.value)) o[k] = v; },
  subscribe: (op) => {
    const listener = op.options ? withOptions(op.callback, op.options) : op.callback;
    const subscription = target(op.h)[op.method](op.name, listener);
    if (!disposerOf(subscription)) throw new InteroperError(`${op.method} returned no dispose() or remove()`);
    // An object .NET already holds is not a subscription; releasing it must not dispose it.
    if (ids.has(subscription)) throw new InteroperError(`${op.method} returned an object that already has a handle`);
    const id = handleOf(subscription);
    subscriptions.add(id);
    return id;
  },
  release: (op) => releaseAll(op.ids),
  load: (op) => {
    if (syncOnly) throw new InteroperError("load imports modules; send it through invoke");
    return Promise.all(op.modules.map(load)).then(() => undefined);
  },
  batch: (op) => runBatch(op.ops),
};

function run(op) {
  const handler = Object.hasOwn(ops, op.op) && ops[op.op];
  if (!handler) throw new InteroperError(`Unknown op: ${op.op}`);
  return handler(op);
}

// R24: what JS does before an event reaches .NET, which can't do it in time on Server. Library events with these
// methods work like DOM events.
function withOptions(callback, o) {
  return (event, ...rest) => {
    if (o.preventDefault) event?.preventDefault?.();
    if (o.stopPropagation) event?.stopPropagation?.();
    return callback(o.fields ? pick(event, o.fields) : event, ...rest);
  };
}

// The named top-level members of an object, so nothing else crosses the wire; any other value, an array included, as
// it is (as InMemoryTransport does).
function pick(value, names) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return value;
  const out = {};
  for (const name of names) out[name] = value[name];
  return out;
}

// Runs ops that return nothing (writes, void calls) in order. A Promise is awaited before the next op, or
// under invokeSync counts as that op's failure. A failed op doesn't stop the batch; the batch then fails
// with one entry per failed op.
function runBatch(list) {
  const errors = [];
  const fail = (index, e) => errors.push({ index, ...toError(e) });
  const next = (i) => {
    for (; i < list.length; i++) {
      const index = i;
      let result;
      try {
        result = run(list[i]);
      } catch (e) {
        fail(i, e);
        continue;
      }
      if (!isThenable(result)) continue;
      if (syncOnly) {
        result.then(undefined, () => {});
        fail(i, new InteroperError(PROMISE_IN_SYNC));
        continue;
      }
      return result.then(undefined, (e) => fail(index, e)).then(() => next(index + 1));
    }
    if (errors.length > 0) throw new BatchError(errors, list.length);
  };
  return next(0);
}

// Releases every id, even when a subscription's dispose() (or remove()) throws; then reports what failed.
function releaseAll(list) {
  const errors = [];
  for (const id of list) {
    const object = handles.get(id);
    if (object === undefined) continue;
    handles.delete(id);
    ids.delete(object);
    if (!subscriptions.delete(id)) continue;
    try {
      disposerOf(object).call(object);
    } catch (e) {
      errors.push(e);
    }
  }
  if (errors.length === 1) throw errors[0];
  if (errors.length > 1) throw new AggregateError(errors, `${errors.length} dispose() calls failed`);
}

// What ends a subscription: its dispose(), or else its remove(), as some libraries name it; undefined if neither.
function disposerOf(subscription) {
  if (typeof subscription?.dispose === "function") return subscription.dispose;
  if (typeof subscription?.remove === "function") return subscription.remove;
  return undefined;
}

// --- Handles and classes ---

function target(handle) {
  if (!handles.has(handle)) throw new InteroperError(`Unknown handle: ${handle}`);
  return handles.get(handle);
}

// An op targets a handle (`h`) or a module's exports (`module`).
function withTarget(op, fn) {
  return op.module === undefined ? fn(target(op.h)) : withModule(op.module, fn);
}

function handleOf(object) {
  let id = ids.get(object);
  if (id === undefined) {
    id = nextId++;
    handles.set(id, object);
    ids.set(object, id);
  }
  return id;
}

function classEntry(classId) {
  const entry = meta.classes[classId];
  if (!entry) throw new InteroperError(`Unknown class id: ${classId}`);
  return entry;
}

// The most-derived class id, from the runtime class-name property; 0 if unknown.
function classIdOf(object) {
  return classIdsByName.get(object[meta.classNameProperty]) ?? 0;
}

function load(specifier) {
  return import(specifier).then((m) => {
    modules.set(specifier, m);
    return m;
  });
}

function withModule(specifier, fn) {
  const m = modules.get(specifier);
  if (m) return fn(m);
  if (syncOnly) throw new InteroperError(`Module not loaded: ${specifier}; send load first`);
  return load(specifier).then(fn);
}

// --- Wire format ---

// JSON.parse revives bottom-up, so nested `$c` bags are instances before their parent is built.
function parse(json) {
  return json == null ? undefined : JSON.parse(json, revive);
}

function revive(_key, value) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return value;
  if (Object.hasOwn(value, "$u")) return undefined;
  if (Object.hasOwn(value, "$h")) return target(value.$h);
  if (Object.hasOwn(value, "$f")) return callback(value.$f);
  if (Object.hasOwn(value, "$c")) {
    const { $c, ...bag } = value;
    const entry = classEntry($c);
    const m = modules.get(entry.module);
    if (!m) throw new InteroperError(`Class ${entry.name} is not loaded; only value-like classes can be sent as $c`);
    return new m[entry.export](bag);
  }
  return value;
}

// A .NET callback. Its arguments go by value; its result is whatever the sink returns (a Promise on Server).
function callback(id) {
  return (...args) => then(callbackSink(id, JSON.stringify(toWire(args, true))), parse);
}

// A JS value in wire form. Plain objects and arrays are copied; other objects become handles, or with
// byValue, value-like class instances become `$c` data.
function toWire(value, byValue = false) {
  if (value === null || (typeof value !== "object" && typeof value !== "function")) return value;
  if (Array.isArray(value)) return Array.from(value, (x) => wireItem(toWire(x, byValue)));
  const proto = Object.getPrototypeOf(value);
  if (proto === Object.prototype || proto === null) {
    const out = {};
    for (const [k, x] of Object.entries(value)) if (x !== undefined) out[k] = toWire(x, byValue);
    return out;
  }
  const classId = classIdOf(value);
  if (byValue && meta.classes[classId]?.value) return toData(value);
  return { $h: handleOf(value), $t: classId };
}

// Array items can't be omitted, so undefined is `$u`.
function wireItem(value) {
  return value === undefined ? UNDEFINED : value;
}

// The snapshot members of an object, with value-like members copied deeply and entities as handles (R17).
function toData(object) {
  const classId = classIdOf(object);
  const entry = classEntry(classId);
  const out = entry.value ? { $c: classId } : {};
  for (const name of entry.members) {
    const v = object[name];
    if (v !== undefined) out[name] = toWire(v, true);
  }
  return out;
}

function isThenable(v) {
  return typeof v?.then === "function";
}

function then(value, onValue) {
  return isThenable(value) ? value.then(onValue) : onValue(value);
}
