/** Sibling modules `ops/a` and `ops/b` each export an `Options` and an `execute` (R27). */
export interface Options {
  scale?: number;
}

/** `x * options.scale` (1 by default). */
export function execute(x: number, options?: Options): number;
