import { LegendEntry } from "./entry.js";

/** A color chip for a legend entry. No export reaches this file; the package's root re-exports it. */
export class Swatch {
  constructor(options?: { color?: string });
  color: string;
}

/** The swatch of an entry: grey when it's hidden. */
export function swatchFor(entry: LegendEntry): Swatch;
