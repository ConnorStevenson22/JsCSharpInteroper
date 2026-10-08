// Minimal handle table + sync accessors, standing in for the generated JS runtime.
const handles = new Map();
let next = 1;
export function create(className, propsJson) {
  const obj = { declaredClass: className, ...JSON.parse(propsJson) };
  handles.set(next, obj);
  return next++;
}
export function getNumber(h, name) { return handles.get(h)[name]; }
export function setNumber(h, name, value) { handles.get(h)[name] = value; }
export function getJson(h, names) {
  const o = handles.get(h), out = {};
  for (const n of names.split(",")) if (o[n] !== undefined) out[n] = o[n];
  return JSON.stringify(out);
}
// Fire an "event" back into .NET synchronously via [JSExport].
export async function fireEvent(h, payload) {
  const { getAssemblyExports } = await globalThis.getDotnetRuntime(0);
  const exports = await getAssemblyExports("WasmTransport.dll");
  return exports.WasmTransport.JsCallbacks.OnEvent(h, payload);
}
globalThis.jsbindGlobal = { getNumber };
