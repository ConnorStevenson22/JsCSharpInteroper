import { Marker } from "./Marker.js";

// Compiled typings write a mixin's base as a constant; `export {}` keeps it and the mixin class private.
export {};

export interface PinDragEvent {
  dx: number;
  dy: number;
}

export interface PinEvents {
  drag: PinDragEvent;
}

/** The class a mixin function returns, applied to any base. */
declare class Draggable {
  constructor(...args: any[]);
  "$events": PinEvents;
  /** Its subscriptions have `remove()`, not `dispose()`. */
  on<K extends keyof this["$events"] & string>(type: K, listener: (event: this["$events"][K]) => void): { remove(): void };
  /** Moves the position and emits `drag`. */
  dragBy(dx: number, dy: number): void;
}

declare const PinSuperclass: typeof Marker & typeof Draggable;

/** A marker with the `Draggable` mixin: its base is still `Marker`. */
export class Pin extends PinSuperclass {
  /** Anything the app keeps with the pin. */
  meta: any;
}
