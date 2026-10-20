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
let classIdsByTag = new Map(); // a custom element's localName -> its class id
let classIdsByConstructor = new Map(); // a loaded module's exported class -> its class id
let classesByModule = new Map(); // module specifier -> [export, class id][]
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
//   { classNameProperty: "className", classes: { [classId]: { module?, export?, name, tag?, value?, members } | { name, of } },
//     customEvents?: [{ name, fields? }] }
// sink(callbackId, argsJson) calls a .NET callback and returns its result as JSON text ("null" for null), null or
// nothing for undefined, or a Promise of either.
// An entry with `of` is a variant (mapping §5): its own class id for its runtime class name, and of's entry otherwise.
// Value-like classes are imported here so `$c` can be built synchronously, in sync mode too; an entity's bag is `$c` too,
// whose module invoke imports first, and sync mode preloads. A custom element's module
// is loaded by its component (R28), or by the sync-mode registry's preload list.
export async function start(metadataSpecifier, sink) {
  meta = (await import(metadataSpecifier)).default;
  const classes = Object.entries(meta.classes);
  // What an entry leaves out: an import's export is `default`, and there are no members.
  for (const [, c] of classes) {
    if (c.module) c.export ??= "default";
    c.members ??= [];
  }
  classIdsByName = new Map(classes.filter(([, c]) => c.name).map(([id, c]) => [c.name, Number(id)]));
  classIdsByTag = new Map(classes.filter(([, c]) => c.tag).map(([id, c]) => [c.tag, Number(id)]));
  classesByModule = Map.groupBy(classes.filter(([, c]) => c.module && c.export), ([, c]) => c.module);
  classIdsByConstructor = new Map();
  modules.forEach(registerClasses);
  callbackSink = sink;
  registerCustomEvents(meta.customEvents ?? []);
  // A class that can't be imported has no module (design §3.5).
  const preloaded = new Set(classes.filter(([, c]) => c.value && c.module).map(([, c]) => c.module));
  await Promise.all([...preloaded].map(load));
}

// R28: the components' custom events, registered with Blazor so its event pipeline delivers them, with the detail as
// wire JSON (projected to `fields`) in JsEventArgs.Json. Once per page, whichever transport starts first, since Blazor
// refuses a second registration; skipped without Blazor (as under node --test). Blazor names an event in lower case,
// and refuses a browser event name equal to its own, so that's given only when it differs.
const registeredEvents = Symbol.for("interoper.customEvents");

function registerCustomEvents(events) {
  const blazor = globalThis.Blazor;
  if (typeof blazor?.registerCustomEventType !== "function") return;
  const registered = (globalThis[registeredEvents] ??= new Set());
  for (const { name, fields } of events) {
    const eventName = name.toLowerCase();
    if (registered.has(eventName)) continue;
    registered.add(eventName);
    blazor.registerCustomEventType(eventName, {
      ...(eventName === name ? {} : { browserEventName: name }),
      createEventArgs: (event) => ({ json: JSON.stringify(wireItem(toWire(fields ? pick(event.detail, fields) : event.detail, true))) }),
    });
  }
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

// For IJSRuntime.InvokeAsync and Promise-returning ops: never rejects; errors come back as {"e": …}. An entity's bag
// (`$c`, R16, R18) may be the first use of its class's module, so that is imported before the op is parsed.
export async function invoke(opJson) {
  const unloaded = unloadedClassModules(opJson);
  if (unloaded.length > 0) {
    try {
      await Promise.all(unloaded.map(load));
    } catch (e) {
      return failure(e);
    }
  }
  return respondWith(false, opJson);
}

// The modules of the classes the op's `$c` values name that aren't imported yet. In JSON text `"$c":` is always a key:
// inside a string its quotes would be escaped.
const classValue = /"\$c":(\d+)/g;
function unloadedClassModules(opJson) {
  const unloaded = new Set();
  for (const [, id] of String(opJson).matchAll(classValue)) {
    const module = meta.classes[id] && classEntry(id).module;
    if (module && !modules.has(module)) unloaded.add(module);
  }
  return [...unloaded];
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
  get: (op) => withTarget(op, (o) => toWire(o[op.name], op.byValue, op.live)),
  set: (op) => withTarget(op, (o) => { o[op.name] = op.value; }),
  call: (op) => withTarget(op, (o) => then(o[op.name](...(op.args ?? [])), (r) => toWire(r, op.byValue, op.live))),
  snapshot: (op) => toData(target(op.h), op.members),
  apply: (op) => { const o = target(op.h); for (const [k, v] of Object.entries(op.value)) o[k] = v; },
  subscribe: (op) => {
    const o = target(op.h);
    const subscription = op.names
      ? watchMembers(o, op.method, op.names, op.callback)
      : subscribeTo(o, op.method, op.name, op.options ? withOptions(op.callback, op.options) : op.callback);
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
  wrap: (op) => toWire(op.value),
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

function subscribeTo(object, method, name, listener) {
  const subscription = object[method](name, listener);
  if (!disposerOf(subscription)) throw new InteroperError(`${method} returned no dispose() or remove()`);
  return subscription;
}

// R18: one listener per member, and one call to .NET per turn in which any changed, with the members' snapshot as it
// is then. A microtask ends the turn: it runs once the code that made the changes returns (a batch op, the library's
// own method, or on WebAssembly every sync call until .NET yields), so a burst is one message, and it waits for no
// timer, which would add latency and is throttled in background tabs.
function watchMembers(object, method, names, callback) {
  const subscriptions = [];
  let queued = false;
  let live = true;
  const flush = () => {
    queued = false;
    if (live) callback(toData(object, names));
  };
  const changed = () => {
    if (queued) return;
    queued = true;
    queueMicrotask(flush);
  };
  const dispose = () => {
    live = false;
    disposeAll(subscriptions.splice(0));
  };
  try {
    for (const name of names) subscriptions.push(subscribeTo(object, method, name, changed));
  } catch (e) {
    try { dispose(); } catch { /* the failed subscribe is the error to report */ }
    throw e;
  }
  return { dispose };
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
  const ended = [];
  for (const id of list) {
    const object = handles.get(id);
    if (object === undefined) continue;
    handles.delete(id);
    ids.delete(object);
    if (subscriptions.delete(id)) ended.push(object);
  }
  disposeAll(ended);
}

// Ends every subscription, even when one's dispose() (or remove()) throws; then reports what failed.
function disposeAll(list) {
  const errors = [];
  for (const subscription of list) {
    try {
      disposerOf(subscription).call(subscription);
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
  return entry.of ? classEntry(entry.of) : entry;
}

// The most-derived class id: an element's by its tag, since a runtime class-name property such as `className` means
// something else on an element (its CSS classes, R28); otherwise from that property, or from the prototype's own
// constructor once its module is loaded (a plain class with no such property), read on the object and then up its
// prototype chain, so a class the typings don't have is its nearest registered ancestor (R19 step 1); 0 if unknown. A
// read that throws (a getter on a prototype) counts as no name. The walk stops after maxPrototypes, in case a proxy's
// chain never ends.
const maxPrototypes = 32;
function classIdOf(object) {
  if (globalThis.Element && object instanceof globalThis.Element) return classIdsByTag.get(object.localName) ?? 0;
  for (let p = object, depth = 0; p !== null && depth < maxPrototypes; p = Object.getPrototypeOf(p), depth++) {
    let id;
    try {
      id = classIdsByName.get(p[meta.classNameProperty]);
      if (id === undefined && Object.hasOwn(p, "constructor")) id = classIdsByConstructor.get(p.constructor);
    } catch {
      continue;
    }
    if (id !== undefined) return id;
  }
  return 0;
}

// R13: the element Blazor rendered for an ElementReference, by the capture attribute it gives every @ref element.
function element(id) {
  const found = globalThis.document?.querySelector(`[_bl_${id}]`);
  if (!found) throw new InteroperError(`No element has the capture attribute _bl_${id}; send an ElementReference after its first render`);
  return found;
}

function load(specifier) {
  return import(specifier).then((m) => {
    modules.set(specifier, m);
    registerClasses(m, specifier);
    return m;
  });
}

function registerClasses(m, specifier) {
  for (const [id, c] of classesByModule.get(specifier) ?? []) {
    if (typeof m[c.export] === "function") classIdsByConstructor.set(m[c.export], Number(id));
  }
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
  if (Object.hasOwn(value, "$e")) return element(value.$e);
  if (Object.hasOwn(value, "$c")) {
    const { $c, ...bag } = value;
    const entry = classEntry($c);
    const m = modules.get(entry.module);
    if (!m) throw new InteroperError(`Class ${entry.name} is not loaded, so $c can't build it; send load first`);
    return new m[entry.export](bag);
  }
  return value;
}

// A .NET callback. Its arguments go by value; its result is whatever the sink returns (a Promise on Server).
function callback(id) {
  return (...args) => then(callbackSink(id, JSON.stringify(toWire(args, true))), parse);
}

// A JS value in wire form. Plain objects and arrays are copied; other objects become handles, or with
// byValue, value-like class instances become `$c` data. With live, a plain object with a method is a handle too: a live
// interface's object (R15), which no class the metadata knows has made. As data (a snapshot's members, R17) nothing
// becomes a handle: such an object is left out, undefined in an array.
function toWire(value, byValue = false, live = false, data = false) {
  if (value === null || (typeof value !== "object" && typeof value !== "function")) return value;
  if (Array.isArray(value)) return Array.from(value, (x) => wireItem(toWire(x, byValue, live, data)));
  const proto = Object.getPrototypeOf(value);
  if (proto === Object.prototype || proto === null) {
    const entries = Object.entries(value);
    if (live && entries.some(([, x]) => typeof x === "function")) return { $h: handleOf(value), $t: 0 };
    const out = {};
    for (const [k, x] of entries) {
      const w = toWire(x, byValue, live, data);
      if (w !== undefined) out[k] = w;
    }
    return out;
  }
  const classId = classIdOf(value);
  if (byValue && meta.classes[classId]?.value) return toData(value);
  return data ? undefined : { $h: handleOf(value), $t: classId };
}

// Array items can't be omitted, so undefined is `$u`.
function wireItem(value) {
  return value === undefined ? UNDEFINED : value;
}

// The snapshot members of an object, or the named ones, with value-like members copied deeply and no handles (R17,
// R18).
function toData(object, names) {
  const classId = classIdOf(object);
  const entry = classEntry(classId);
  const out = entry.value ? { $c: classId } : {};
  for (const name of names ?? entry.members) {
    const w = toWire(object[name], true, false, true);
    if (w !== undefined) out[name] = w;
  }
  return out;
}

function isThenable(v) {
  return typeof v?.then === "function";
}

function then(value, onValue) {
  return isThenable(value) ? value.then(onValue) : onValue(value);
}
