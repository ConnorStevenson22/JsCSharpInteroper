import { MapView } from "./MapView.js";

/** A tool for a view. Its factory makes a plain object, so no class makes it (R15). */
export interface Tool {
  readonly name: string;
  readonly active: boolean;
  activate(): void;
  deactivate(): void;
}

/** A tool that reads the view's zoom: an interface that extends another (R15). */
export interface MeasureTool extends Tool {
  /** How many readings since the last `clear()`. */
  readonly readings: number;
  /** The view's zoom now. */
  measure(): number;
  clear(): void;
}

export function createPanTool(view: MapView): Tool;

export function createMeasureTool(view: MapView): MeasureTool;

/** The tools made for the view, in order, as `Tool`s. */
export function toolsOf(view: MapView): Tool[];
