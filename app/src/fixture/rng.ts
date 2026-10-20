/**
 * Deterministic RNG for the D-2 fixture.
 *
 * Deterministic so the same seed gives the same prices on every platform. `TickReplay.cs` ports it
 * to C#.
 *
 * mulberry32: a 32-bit PRNG that is a few lines long and has good enough distribution for
 * synthetic price data. Not cryptographic, and it does not need to be.
 */
export function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return function next(): number {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * Standard normal samples via Box-Muller. Returns one value per call; the second value of each
 * pair is cached so we do not throw away half the entropy.
 */
export function normalSampler(uniform: () => number): () => number {
  let spare: number | null = null;
  return function nextNormal(): number {
    if (spare !== null) {
      const value = spare;
      spare = null;
      return value;
    }
    // Guard against u === 0, which makes Math.log(u) infinite.
    const u = Math.max(uniform(), Number.EPSILON);
    const v = uniform();
    const radius = Math.sqrt(-2 * Math.log(u));
    const theta = 2 * Math.PI * v;
    spare = radius * Math.sin(theta);
    return radius * Math.cos(theta);
  };
}
