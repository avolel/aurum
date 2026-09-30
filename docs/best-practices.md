# Aurum — TypeScript and React Native: mistakes to avoid and habits to keep

> **None of this exists yet.** This guide was written for a larger, finished app. Aurum's `app/`
> folder is an empty starting point: no Redux, no Expo Router, no `src/hooks/`, no models folder.
> Treat this as the target for Phase 4, not as a description of code you can import today. Check a
> path exists before relying on it. Examples marked "found in the original app" came from that other
> app. Other examples use made-up names from this app's domain (prices, alerts).
>
> (This guide used to point to a CLAUDE.md for file naming, type-vs-interface rules and import
> shortcuts. That link didn't lead anywhere, and CLAUDE.md in this repository doesn't cover those
> topics yet.)

---

## Mistakes to avoid

### 1. A comment that doesn't match the file name

**Problem:**

```typescript
// File: PriceTick.model.ts
//priceTick.model.ts  // the comment has the wrong capitalisation
```

**Fix:** keep comments accurate, or remove them.

```typescript
// File: PriceTick.model.ts
// PriceTick.model.ts - the PriceTick interface and a function that makes an empty one
```

### 2. Using the `any` type

`any` switches type checking off for that value, which defeats the point of TypeScript.

**Found in the original app:**

```typescript
// BAD
searchResults: any[];
edi271: any | null;
account: AccountInfo | any | null;
```

**Fix:** give it a real type.

```typescript
// GOOD
searchResults: SearchResult[];
edi271: Edi271Response | null;
account: AccountInfo | null;
```

### 3. Exporting in different ways from similar files

**Found in the original app:**

```typescript
// File 1: named export only
export interface Field { ... }

// File 2: a named export plus a default export
export interface Question { ... }
export default emptyQuestion;

// File 3: exports the same thing twice
export const userService = { ... };
export default userService;  // not needed
```

**Fix:** pick one pattern. Types get named exports. A function that creates something, or a single
shared object, can also be the default export.

```typescript
// PREFERRED
export interface PriceAlert { ... }
export const createEmptyPriceAlert = (index: number): PriceAlert => ({ ... });
export default createEmptyPriceAlert;
```

### 4. Forcing a type to silence TypeScript

**Problem:**

```typescript
// BAD: "as any" hides a real type mismatch
w={width as any}
```

**Fix:** correct the type where it's declared.

```typescript
// GOOD
w={width}  // make sure width has the right type in the props interface
```

### 5. The same type declared in two places

**Problem:**

```typescript
// In store/store.ts
export type RootState = ReturnType<typeof store.getState>;

// In store/index.ts (a copy)
export type RootState = ReturnType<typeof store.getState>;
```

**Fix:** declare it once.

```typescript
// Only in store/index.ts or store/store.ts, not both
export type RootState = ReturnType<typeof store.getState>;
```

### 6. Similar files named in different styles

**Found in the original app:**

```typescript
// Four styles for the same kind of file:
entra-user.model.ts   // lowercase-with-dashes
user.model.ts         // lowercase
Field.model.ts        // Capitalised
legacy-form.models.ts // plural
```

**Fix:** all model files use capitalised words run together (called PascalCase), singular.

```typescript
EntraUser.model.ts
User.model.ts
Field.model.ts
LegacyForm.model.ts
```

---

## Habits to keep

### 1. Import types with `import type`

`import type` tells TypeScript the import is only used as a type, so it's removed from the built
code.

```typescript
// GOOD: type-only imports
import type { PriceTick } from '@models/pricing/PriceTick.model';
import type { RootState } from '@store';

// When you need both a value and a type from the same file
import createEmptyPriceAlert, { type PriceAlert } from '@models/alerts/PriceAlert.model';
```

(`@models` and `@store` are import shortcuts, called path aliases. They'd need setting up in
`tsconfig.json`, and aren't there yet.)

### 2. Say which server class a type copies

```typescript
// GOOD: names where it comes from
/**
 * Matches Aurum.App.Application.PriceSources.DTOs.PriceSourceDto
 * Field names arrive in camelCase (ASP.NET Core default).
 */
export interface PriceSourceDto {
  sourceCode: string;
  // ...
}
```

### 3. Use a list of allowed strings for a fixed set of values

A type made of a few allowed strings (called a union type) documents itself, and TypeScript rejects
anything else.

```typescript
// GOOD
type AsyncStatus = "idle" | "loading" | "success" | "error";
type ChartRange = "1m" | "5m" | "1h" | "1d";
```

### 4. Build props by extending an interface, not by combining types with `&`

Error messages are clearer when an interface extends another than when two types are joined with `&`
(called an intersection).

```typescript
// GOOD
interface PriceCardProps extends Omit<CardProps, 'width' | 'radius'> {
  width?: number | string;
  radius?: number;
}

// AVOID: harder to debug
type PriceCardProps = Omit<CardProps, 'width' | 'radius'> & {
  width?: number | string;
};
```

### 5. Give constant lists a type

`as const` tells TypeScript the list will never change, so it can work out the exact allowed values.

```typescript
// GOOD
export const CHART_RANGES = [
  '1m',
  '5m',
  '1h',
] as const;

type ChartRange = typeof CHART_RANGES[number];  // '1m' | '5m' | '1h'
```

### 6. One object per server area, holding its calls

```typescript
// GOOD: every server call for one area in one object
export const priceService = {
  async getHistory(params: GetPriceHistoryParams = {}): Promise<GetPriceHistoryApiResponse> {
    return apiService.get<GetPriceHistoryApiResponse>("/price/history", { params });
  },
  // ...
};

export default priceService;
```

### 7. How to lay out a Redux slice

A Redux "slice" is one piece of the app's shared data, plus the functions that change it.

```typescript
// GOOD: keep the slice's files together
// alerts.types.ts   - only if the types are complex; otherwise put them in the reducer file
// alerts.reducer.ts - the slice itself
// alerts.actions.ts - async actions (thunks), if needed
```

`docs/best-practices-redux.md` suggests different file names (`alertsSlice.ts` and so on). The two
guides need reconciling before Phase 4 starts.

### 8. Screen state that belongs in the web address

Some screen state should survive a page reload, or be shareable as a link: the selected tab, an open
panel and which item it shows, edit mode, filters. In the original app, all of that went through a
set of shared hooks in `src/hooks/urlState/`. Nobody wrote their own pair of
`useLocalSearchParams` and `router.setParams` (the Expo Router functions for reading and writing the
web address).

The reason: the same pattern had been written separately on about a dozen screens, each with slightly
different, sometimes buggy, handling of the edge cases, before being merged into shared hooks. Which
hook fits which case:

| Case | Hook |
|---|---|
| One value read from the address (a tab, a filter ID) | `useUrlParamState` |
| Applying a value from an incoming link to the screen once | `useConsumeUrlParamOnce` |
| Copying screen state out to one or more address values | `useUrlParamSync` |
| A set of filters where the address is the only source of truth | `useUrlParamsBag` |
| "Open this panel for this ID, found in a list already loaded or fetched if not, on this tab" | `useDeepLinkedDrawerParams` |

```typescript
// GOOD
import { useUrlParamState, enumCodec } from '@hooks/urlState';
const [activeTab, setActiveTab] = useUrlParamState('tab', enumCodec(TAB_KEYS, 'overview'));

// BAD: rebuilds reading and writing the address by hand
const params = useLocalSearchParams<{ tab?: string }>();
const [activeTab, setActiveTab] = useState(params.tab ?? 'overview');
useEffect(() => { router.setParams({ tab: activeTab }); }, [activeTab]);
```
