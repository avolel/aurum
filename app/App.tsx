import { StyleSheet, Text, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { GOLD_FIXTURE_DEFAULTS, generateGoldTicks } from './src/fixture/generateTicks';

/**
 * Placeholder shell.
 *
 * The D-2 comparison spike that used to live here was removed when D-2 was decided without it —
 * web goes to `lightweight-charts`, and the one-codebase-vs-split question moves to Phase 4, where
 * a physical device is actually in play. What survived is `src/fixture/`, which is the seeded tick
 * generator; the harness, the three candidate screens and the per-library adapters are gone.
 *
 * This renders the fixture's summary and nothing else, so `expo start` still proves the shell is
 * intact without implying an app exists. The Phase 1 dashboard replaces this file wholesale in the
 * second tranche.
 */
export default function App() {
  const ticks = generateGoldTicks();
  const first = ticks[0];
  const last = ticks[ticks.length - 1];

  return (
    <View style={styles.root}>
      <StatusBar style="auto" />
      <Text style={styles.title}>Aurum — app shell</Text>
      <Text style={styles.body}>
        No UI yet. The Phase 1 dashboard is a separate tranche; this file is a placeholder.
      </Text>
      <Text style={styles.meta}>
        Fixture: {ticks.length.toLocaleString()} ticks · seed {GOLD_FIXTURE_DEFAULTS.seed}
        {'\n'}
        {new Date(first.ts).toISOString().slice(0, 10)} →{' '}
        {new Date(last.ts).toISOString().slice(0, 10)}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: '#fff', justifyContent: 'center', padding: 24, gap: 12 },
  title: { fontSize: 18, fontWeight: '600', color: '#111' },
  body: { fontSize: 13, color: '#444', lineHeight: 19 },
  meta: { fontSize: 11, opacity: 0.5, fontVariant: ['tabular-nums'], lineHeight: 16 },
});
