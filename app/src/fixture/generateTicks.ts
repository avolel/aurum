import { mulberry32, normalSampler } from './rng';

/**
 * Canonical tick shape. Every candidate renders from this array; per-library shapes are derived
 * in `adapters.ts` outside the measured window.
 *
 * `ts` is epoch milliseconds UTC. `price` is XAUUSD, one troy ounce in USD.
 */
export interface Tick {
  ts: number;
  price: number;
}

export interface FixtureOptions {
  /** Number of ticks to emit. D-2 measures at 30_000. */
  count: number;
  /** Any 32-bit integer. Same seed + same count => same series, on every platform. */
  seed: number;
  /** Spacing between consecutive in-session ticks, milliseconds. */
  intervalMs: number;
  /** Opening price for the series. */
  startPrice: number;
  /** Annualised drift, as a fraction (0.06 = +6%/yr). */
  annualDrift: number;
  /** Annualised volatility, as a fraction (0.15 = 15%/yr, roughly gold's realised vol). */
  annualVolatility: number;
  /**
   * Whether to skip ticks while the market is closed. Gold trades ~Sun 22:00 UTC through
   * Fri 22:00 UTC, so a realistic month has four two-day holes in it.
   */
  sessionGaps: boolean;
}

/**
 * Defaults chosen so 30k ticks is about a month of one-minute gold — the span a user would
 * actually pan across, at the resolution that makes the render expensive.
 */
export const GOLD_FIXTURE_DEFAULTS: FixtureOptions = {
  count: 30_000,
  seed: 0x60_1d,
  intervalMs: 60_000,
  startPrice: 3_320,
  annualDrift: 0.06,
  annualVolatility: 0.15,
  sessionGaps: true,
};

const MS_PER_YEAR = 365 * 24 * 60 * 60 * 1000;

/**
 * Geometric Brownian motion, which is the standard first approximation for a spot metal price and
 * is what makes this fixture behave differently from a random walk: prices stay positive, moves
 * scale with the level, and the series wanders rather than mean-reverting to the start.
 *
 * Deliberately *not* modelled — these would change what the chart has to draw, and the spike is
 * measuring render cost, not price realism:
 *   - jumps on macro releases (a real gold series has visible discontinuities)
 *   - intraday volatility seasonality (London/NY overlap is not like the Asian session)
 *   - bid/ask, so there is no candle body to draw, only a line
 */
export function generateGoldTicks(options: Partial<FixtureOptions> = {}): Tick[] {
  const { count, seed, intervalMs, startPrice, annualDrift, annualVolatility, sessionGaps } = {
    ...GOLD_FIXTURE_DEFAULTS,
    ...options,
  };

  const nextNormal = normalSampler(mulberry32(seed));

  // Per-step drift and diffusion for the GBM increment. dt is the step as a fraction of a year.
  const dt = intervalMs / MS_PER_YEAR;
  const drift = (annualDrift - (annualVolatility * annualVolatility) / 2) * dt;
  const diffusion = annualVolatility * Math.sqrt(dt);

  const ticks: Tick[] = new Array(count);
  let price = startPrice;

  // Start on a Monday 00:00 UTC so the first session gap lands where a reader expects it.
  let ts = Date.UTC(2026, 0, 5, 0, 0, 0);

  for (let i = 0; i < count; i += 1) {
    if (sessionGaps) {
      ts = advancePastClosedMarket(ts);
    }
    ticks[i] = { ts, price };
    price *= Math.exp(drift + diffusion * nextNormal());
    ts += intervalMs;
  }

  return ticks;
}

/**
 * Rolls `ts` forward to the next market open if it lands in the weekend close
 * (Fri 22:00 UTC → Sun 22:00 UTC). Returns `ts` unchanged during a session.
 */
function advancePastClosedMarket(ts: number): number {
  const d = new Date(ts);
  const day = d.getUTCDay(); // 0 = Sunday
  const hour = d.getUTCHours();

  const closed =
    day === 6 || (day === 5 && hour >= 22) || (day === 0 && hour < 22);
  if (!closed) return ts;

  // Next Sunday 22:00 UTC at or after ts.
  const open = new Date(ts);
  open.setUTCHours(22, 0, 0, 0);
  const daysToSunday = (7 - open.getUTCDay()) % 7;
  open.setUTCDate(open.getUTCDate() + daysToSunday);
  if (open.getTime() < ts) open.setUTCDate(open.getUTCDate() + 7);
  return open.getTime();
}
