// Stand-in for the runtime's JS half: a handle table, property access, snapshots, events,
// and one callback path into .NET per host ([JSExport] on WebAssembly, a DotNetObjectReference elsewhere).
const handles = new Map();
const subscriptions = new Map(); // `${handle}:${event}` -> callback ids
let nextHandle = 1;
let wasmDispatch = null;
let dotNetSink = null;

class DemoObject {
  constructor(jsClass, props) {
    this.declaredClass = jsClass;
    Object.assign(this, props);
  }
}

export async function useWasmExports(assemblyName) {
  const { getAssemblyExports } = await globalThis.getDotnetRuntime(0);
  const exports = await getAssemblyExports(assemblyName);
  wasmDispatch = exports.HostsSpike.Client.Runtime.JsCallbacks.DispatchFromJs;
}

export function useDotNetSink(sink) { dotNetSink = sink; }

export function create(jsClass, propertiesJson) {
  handles.set(nextHandle, new DemoObject(jsClass, JSON.parse(propertiesJson)));
  return nextHandle++;
}

export function getNumber(handle, property) { return handles.get(handle)[property]; }
export function setNumber(handle, property, value) { handles.get(handle)[property] = value; }

export function snapshot(handle, properties) {
  const obj = handles.get(handle), out = {};
  for (const p of properties.split(",")) if (obj[p] !== undefined) out[p] = obj[p];
  return JSON.stringify(out);
}

export function subscribe(handle, eventName, callbackId) {
  const key = `${handle}:${eventName}`;
  if (!subscriptions.has(key)) subscriptions.set(key, []);
  subscriptions.get(key).push(callbackId);
}

export function release(handle) { handles.delete(handle); }

export function raise(handle, eventName, payloadJson) {
  for (const id of subscriptions.get(`${handle}:${eventName}`) ?? []) {
    if (wasmDispatch) wasmDispatch(id, payloadJson);          // synchronous, same thread
    else dotNetSink.invokeMethodAsync("Dispatch", id, payloadJson); // async over SignalR / WebView
  }
}
