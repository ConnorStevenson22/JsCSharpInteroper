// Starts interoper.js against the demo-maps metadata and plays the .NET side: ops go in as JSON, results come
// back as parsed wire JSON, and callbacks are plain JS functions behind the callback sink.
import * as interoper from "../../src/Interoper.Runtime/wwwroot/interoper.js";
import demoMaps from "./demo-maps.meta.mjs";

export { ids, modules } from "./demo-maps.meta.mjs";
export const metadata = new URL("./demo-maps.meta.mjs", import.meta.url).href;
export const { census } = interoper;

const callbacks = new Map();
let nextCallback = 1;

// Registers a .NET-side callback; it receives wire values and returns a wire value (or a Promise of one).
export function callback(fn) {
  const id = nextCallback++;
  callbacks.set(id, fn);
  return { $f: id };
}

export function sink(id, argsJson) {
  const toJson = (r) => (r === undefined ? null : JSON.stringify(r));
  const result = callbacks.get(id)(...JSON.parse(argsJson));
  return result instanceof Promise ? result.then(toJson) : toJson(result);
}

await interoper.start(metadata, sink);
// Entity classes load on first use; sync mode loads them up front.
const classModules = new Set(Object.values(demoMaps.classes).map((c) => c.module));
value(await sendAsync({ op: "load", modules: [...classModules] }));

// One op through the sync seam ([JSImport]).
export function send(op) {
  return JSON.parse(interoper.invokeSync(JSON.stringify(op)));
}

// One op through the async seam (IJSRuntime).
export async function sendAsync(op) {
  return JSON.parse(await interoper.invoke(JSON.stringify(op)));
}

// The result of a successful op; a failed one throws.
export function value(response) {
  if (response.e) throw new Error(`${response.e.name}: ${response.e.message}`);
  return response.v;
}

export const create = (t, bag) => value(send({ op: "create", t, args: bag === undefined ? [] : [bag] }));
export const get = (h, name, byValue) => value(send({ op: "get", h: h.$h, name, byValue }));
export const set = (h, name, v) => value(send({ op: "set", h: h.$h, name, value: v }));
export const call = (h, name, ...args) => value(send({ op: "call", h: h.$h, name, args }));
export const release = (...handles) => value(send({ op: "release", ids: handles.map((h) => h.$h ?? h) }));
