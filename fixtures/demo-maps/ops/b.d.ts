/** The same names as `ops/a`, with other members (R27). */
export interface Options {
  offset?: number;
}

/** `x + options.offset` (0 by default). */
export function execute(x: number, options?: Options): number;
