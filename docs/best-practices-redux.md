# Redux in React Native: how to organise it

> **None of this exists yet.** There's no Redux in this repository. `app/` is an empty Expo starting
> point. This guide describes the target for Phase 4, not code you can use today. Examples use
> made-up names from this app's domain (prices, alerts, sign-in).

**What Redux is:** a single shared store for data many screens need. Screens read from it, and send it
"actions" (messages describing what happened) to change it. **Redux Toolkit** (RTK) is the official
set of helpers that removes most of the repetitive code Redux used to need.

---

## 1. Folders

Group Redux code by feature, not by kind of file.

### 1.1 Suggested layout

```
app/
  store/
    index.ts              # creates the store
    rootReducer.ts        # combines every feature's slice
    middleware.ts         # custom steps actions pass through
  features/
    auth/
      authSlice.ts        # the slice: this feature's data and the functions that change it
      authSelectors.ts    # functions that read from the store, cached
      authThunks.ts       # async work (server calls and so on)
      authTypes.ts        # TypeScript types
      __tests__/          # tests, kept next to the code
    prices/
      pricesSlice.ts
      pricesSelectors.ts
      pricesApi.ts        # server calls, written with RTK Query
  hooks/
    useAppDispatch.ts     # typed version of useDispatch
    useAppSelector.ts     # typed version of useSelector
  services/
    api.ts                # RTK Query's shared base setup
```

> ✅ **Group by feature.** Keep all of a feature's Redux code (slice, selectors, thunks, types,
> tests) in one folder. Then a feature is easy to find, change, or delete as a whole.

### 1.2 File names

| Kind of file | Name | Example |
|--------------|------|---------|
| Slice | `featureSlice.ts` | `authSlice.ts` |
| Selectors | `featureSelectors.ts` | `authSelectors.ts` |
| Thunks | `featureThunks.ts` | `authThunks.ts` |
| RTK Query server calls | `featureApi.ts` | `pricesApi.ts` |
| Types | `featureTypes.ts` | `authTypes.ts` |

RTK Query files sit in the feature's folder, **not** in a shared `store/api/` folder, and are named
`featureApi.ts`, **not** `feature.api.ts`.

The original guide said this was the one kind of file that doesn't take the `.slice.ts` or
`.reducer.ts` ending "that CLAUDE.md lists". This repository's CLAUDE.md lists no such endings, and
`docs/best-practices.md` suggests `alerts.reducer.ts`-style names. Settle on one style before Phase 4.

---

## 2. Setting up Redux Toolkit

Always use Redux Toolkit. Never write Redux code the old manual way: Toolkit removes most of the
repetitive code and makes the safe patterns the default.

### 2.1 Creating the store

```typescript
// store/index.ts
import { configureStore } from '@reduxjs/toolkit';
import { setupListeners } from '@reduxjs/toolkit/query';
import rootReducer from './rootReducer';
import { apiSlice } from '../services/api';

export const store = configureStore({
  reducer: rootReducer,
  middleware: (getDefaultMiddleware) =>
    getDefaultMiddleware({
      // The saving-to-disk library sends this action with values that can't be turned into JSON
      serializableCheck: {
        ignoredActions: ['persist/PERSIST'],
      },
      // Only check for accidental changes to the store while developing; it's slow
      immutableCheck: __DEV__,
    }).concat(apiSlice.middleware),
});

// Lets RTK Query refetch when the app comes back to the foreground or reconnects
setupListeners(store.dispatch);

export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
```

### 2.2 Typed hooks

Make typed versions of the two Redux hooks once, so no screen has to repeat the types. Import these
instead of the plain ones.

```typescript
// hooks/useAppDispatch.ts
import { useDispatch } from 'react-redux';
import type { AppDispatch } from '../store';
export const useAppDispatch = useDispatch.withTypes<AppDispatch>();

// hooks/useAppSelector.ts
import { useSelector } from 'react-redux';
import type { RootState } from '../store';
export const useAppSelector = useSelector.withTypes<RootState>();
```

> ❌ **Don't** import `useDispatch` or `useSelector` straight from `react-redux` in a component. Always
> use the typed versions. Then you never need to cast types by hand, and mismatches are caught when
> the code is built.

---

## 3. Slices

A slice is one feature's share of the store, with the functions (called reducers) that change it.

### 3.1 A slice template

```typescript
// features/auth/authSlice.ts
import { createSlice, PayloadAction } from '@reduxjs/toolkit';
import { loginUser, logoutUser } from './authThunks';
import { AuthState } from './authTypes';

const initialState: AuthState = {
  user: null,
  token: null,
  status: 'idle',       // 'idle' | 'loading' | 'succeeded' | 'failed'
  error: null,
};

const authSlice = createSlice({
  name: 'auth',
  initialState,
  reducers: {
    clearError(state) {
      // Looks like changing the state directly, but Toolkit (through a library called Immer)
      // turns this into a safe copy
      state.error = null;
    },
    updateProfile(state, action: PayloadAction<Partial<User>>) {
      if (state.user) {
        Object.assign(state.user, action.payload);
      }
    },
  },
  // Responses to the async sign-in steps
  extraReducers: (builder) => {
    builder
      .addCase(loginUser.pending, (state) => {
        state.status = 'loading';
        state.error = null;
      })
      .addCase(loginUser.fulfilled, (state, action) => {
        state.status = 'succeeded';
        state.user = action.payload.user;
        state.token = action.payload.token;
      })
      .addCase(loginUser.rejected, (state, action) => {
        state.status = 'failed';
        state.error = action.payload ?? 'Login failed';
      });
  },
});
```

### 3.2 How to shape the data

- Keep it flat. Avoid objects nested deep inside objects.
- Use the same four statuses everywhere: `'idle' | 'loading' | 'succeeded' | 'failed'`.
- Store IDs the server gave you, not ones made up in the app, where possible.
- Only put data in Redux if many screens need it.
- Keep form input, animations and anything that only affects one screen's look in that component's own
  state.

### 3.3 Redux or the component's own state?

| Put in Redux | Keep in the component |
|--------------|-----------------------|
| Sign-in and session data | What's typed in a form |
| Server data several screens use | Whether a panel is open or a section expanded |
| App-wide settings | Animation and gesture state |
| Data needed after moving to another screen | Short-lived things (tooltips, hover) |
| Data that must work offline | Scroll position |

---

## 4. Selectors

A selector is a function that reads a piece of the store. Good selectors matter for speed in React
Native: a component only redraws when the specific data it selected changes.

### 4.1 Patterns

```typescript
// features/prices/pricesSelectors.ts
import { createSelector } from '@reduxjs/toolkit';
import { RootState } from '../../store';

// Plain selectors: they only read, so no caching needed
export const selectAlerts = (state: RootState) => state.alerts.items;
export const selectAlertsStatus = (state: RootState) => state.alerts.status;

// Selectors that work something out: cached with createSelector, so the result is only
// recalculated when selectAlerts returns something new
export const selectActiveAlerts = createSelector(
  [selectAlerts],
  (alerts) => alerts.filter((a) => a.isActive)
);

// Selectors that take an extra value
export const selectAlertById = createSelector(
  [selectAlerts, (_state: RootState, id: string) => id],
  (alerts, id) => alerts.find((a) => a.id === id)
);
```

### 4.2 Rules

1. Keep selectors next to their slice, in a selectors file.
2. Use `createSelector` for anything that filters or calculates. Never work data out inside a
   component.
3. Keep selectors small, and build bigger ones out of smaller ones.
4. Never create a new object or array inside a selector, unless it's wrapped in `createSelector`.
5. Prefer several small `useAppSelector` calls over one call returning a big object.

> ❌ **Common mistake:** `useAppSelector(state => ({ a: state.x.a, b: state.y.b }))` creates a new
> object on every draw, so the component redraws every time. Use two `useAppSelector` calls, or
> `createSelector`.

---

## 5. Async work and fetching data

Pick the pattern that matches how complex the work is and whether its results should be cached.

### 5.1 RTK Query (the default for server data)

RTK Query should be the first choice for fetching server data. It caches results, merges identical
requests made at the same time, refetches in the background, and can show a change before the server
confirms it.

> **The original app built on its own `apiBaseQuery` (`src/store/api/apiBaseQuery.ts`), never on
> `fetchBaseQuery`.** Its reason: `fetchBaseQuery` skips the shared request code that attached the
> login token, unwrapped the `ApiResponse<T>` wrapper, and showed a "session expired" screen. Neither
> `apiBaseQuery` nor the plan it pointed to (`scheduling-rtk-query-plan.md`) exists in this
> repository. The example below is the library's standard shape. Aurum will need the same kind of
> wrapper, because every answer from the API comes inside `ApiResponse<T>`.

```typescript
// services/api.ts
import { createApi, fetchBaseQuery } from '@reduxjs/toolkit/query/react';

export const apiSlice = createApi({
  reducerPath: 'api',
  baseQuery: fetchBaseQuery({
    baseUrl: Config.API_URL,
    // Attach the login token to every request
    prepareHeaders: (headers, { getState }) => {
      const token = (getState() as RootState).auth.token;
      if (token) headers.set('Authorization', `Bearer ${token}`);
      return headers;
    },
  }),
  // Labels used to know which cached results to refetch after a change
  tagTypes: ['Price', 'Alert', 'PriceSource'],
  endpoints: () => ({}), // each feature adds its own endpoints in its own file
});
```

### 5.2 `createAsyncThunk` (for more involved work)

Use `createAsyncThunk` when the work does more than fetch data: several steps in a row, saving to the
phone, or moving to another screen.

```typescript
// features/auth/authThunks.ts
import { createAsyncThunk } from '@reduxjs/toolkit';

export const loginUser = createAsyncThunk(
  'auth/login',
  async (credentials: LoginRequest, { rejectWithValue }) => {
    try {
      const response = await authApi.login(credentials);
      // Keep the token in the phone's encrypted storage
      await SecureStore.setItemAsync('token', response.token);
      return response;
    } catch (error) {
      return rejectWithValue(parseApiError(error));
    }
  }
);
```

### 5.3 Which to use

| Situation | Use | Why |
|-----------|-----|-----|
| Reading, creating, updating and deleting server data | RTK Query | Caching and refetching built in |
| Sign-in | `createAsyncThunk` | Several side effects |
| File upload with a progress bar | `createAsyncThunk` | Needs progress tracking |
| Search as you type | RTK Query, with `skip` until there's input | Caches each search separately |
| Live data over a socket (such as live gold prices) | RTK Query streaming updates | Handles connecting and disconnecting |

---

## 6. Speed in React Native

React Native has its own speed concerns. Redux use needs to keep work off the main thread and avoid
stutter on slower phones.

### 6.1 Long lists (`FlatList`)

```typescript
const AlertList = () => {
  const alerts = useAppSelector(selectActiveAlerts);
  // Same function every draw, so list rows aren't redrawn for no reason
  const renderItem = useCallback(
    ({ item }: { item: PriceAlert }) => <AlertCard alert={item} />,
    []
  );
  return (
    <FlatList
      data={alerts}
      renderItem={renderItem}
      keyExtractor={(item) => item.id}
      removeClippedSubviews={true}  // drop rows that are off screen
      maxToRenderPerBatch={10}
      windowSize={5}
    />
  );
};

// React.memo: only redraw a card when its own alert changes
const AlertCard = React.memo(({ alert }: Props) => {
  return <View>...</View>;
});
```

### 6.2 Rules

1. Wrap every list row that reads from Redux in `React.memo`.
2. For big collections, use the flat shape that `createEntityAdapter` gives (below).
3. When selecting an object, compare with `shallowEqual` (compare each field, not the object itself).
4. Don't send many actions back to back. Group them with `unstable_batchedUpdates`, or use RTK
   listeners.
5. Measure with React DevTools and Flipper before optimising. Measure, don't guess.
6. Only run the "can this be turned into JSON" check while developing (`__DEV__`), to avoid slowing
   down the released app.

### 6.3 `createEntityAdapter` for collections

For collections (alerts, users, messages), `createEntityAdapter` stores items by ID in a flat shape,
and gives you ready-made functions to add, update and remove them.

```typescript
import { createEntityAdapter } from '@reduxjs/toolkit';

const alertsAdapter = createEntityAdapter<PriceAlert>({
  sortComparer: (a, b) => a.name.localeCompare(b.name),
});

const initialState = alertsAdapter.getInitialState({
  status: 'idle' as const,
});

// Gives the shape { ids: [], entities: {} }
// and the functions addOne, addMany, updateOne, removeOne, setAll, and more
```

---

## 7. Side effects

A side effect is anything an action causes beyond changing the store: saving to the phone, moving
screens, calling the server.

### 7.1 RTK listeners (first choice)

Use `listenerMiddleware` to run code when a particular action happens. It replaces older libraries
(redux-saga, redux-observable) with something simpler and easier to test.

```typescript
import { createListenerMiddleware } from '@reduxjs/toolkit';

export const listenerMiddleware = createListenerMiddleware();

// When sign-out finishes: delete the token, clear cached server data, go to the sign-in screen
listenerMiddleware.startListening({
  actionCreator: logoutUser.fulfilled,
  effect: async (_action, listenerApi) => {
    await SecureStore.deleteItemAsync('token');
    listenerApi.dispatch(apiSlice.util.resetApiState());
    navigationRef.reset({ index: 0, routes: [{ name: 'Login' }] });
  },
});
```

### 7.2 Which tool for which job

| Job | Tool | Notes |
|-----|------|-------|
| React to an action | Listener | First choice |
| Analytics and logging | A small custom middleware | One job only |
| Multi-step async work | Listener | Can be cancelled, and can start sub-tasks |
| Refreshing an expired login token | A wrapper around RTK Query's base setup | Retries the request after refreshing |

---

## 8. Testing

Test Redux code separately from components. Slices, selectors and thunks each get their own tests.

### 8.1 What to test first

MSW (Mock Service Worker) fakes the server for tests. RNTL is React Native Testing Library.

| What | Priority | Tool | How much to cover |
|------|----------|------|-------------------|
| Slices | High | Jest | 90%+ |
| Selectors | High | Jest | 90%+ |
| Thunks | Medium | Jest + MSW | 80%+ |
| Components that read from Redux | Medium | RNTL + a test store | The important paths |
| RTK Query endpoints | Medium | Jest + MSW | 80%+ |

### 8.2 A slice test

```typescript
describe('authSlice', () => {
  it('sets loading while sign-in is in progress', () => {
    const state = authReducer(initialState, loginUser.pending('', credentials));
    expect(state.status).toBe('loading');
    expect(state.error).toBeNull();
  });

  it('stores the user when sign-in works', () => {
    const payload = { user: mockUser, token: 'abc123' };
    const state = authReducer(
      initialState,
      loginUser.fulfilled(payload, '', credentials)
    );
    expect(state.user).toEqual(mockUser);
    expect(state.status).toBe('succeeded');
  });
});
```

---

## 9. Saving to the phone and working offline

### 9.1 Setting up redux-persist

redux-persist saves parts of the store to the phone so they survive a restart. List the slices to
save (an allow list), rather than saving everything. Use encrypted storage for anything sensitive,
like login tokens.

```typescript
import AsyncStorage from '@react-native-async-storage/async-storage';
import { persistReducer, persistStore } from 'redux-persist';
import * as SecureStore from 'expo-secure-store';

const persistConfig = {
  key: 'root',
  storage: AsyncStorage,
  whitelist: ['settings', 'offlineQueue'],  // only these slices are saved
  blacklist: ['api'],                       // never save RTK Query's cache
  version: 1,
  // Upgrades saved data when the store's shape changes between versions
  migrate: createMigrate(migrations, { debug: __DEV__ }),
};
```

### 9.2 Rules

- Never save RTK Query's cache. It manages its own.
- Always list what to save. Never rely only on a list of what not to save.
- Keep tokens in SecureStore (Expo) or the phone's Keychain, never in AsyncStorage, which isn't
  encrypted.
- Give the saved data a version number, and write an upgrade step whenever the store's shape changes.
- Put a sensible time limit on loading saved data while the loading screen shows.

---

## 10. Common mistakes

| Mistake | Instead |
|---------|---------|
| Putting everything in Redux | Only data many screens share |
| Changing state outside a Toolkit reducer | Only change it inside reducers, where Immer makes it safe |
| Storing values that can't be turned into JSON (`Date`, `Map`, `Set`) | Use plain objects, arrays, or date strings (ISO format) |
| Sending actions from inside a reducer | Use listeners or thunks for side effects |
| Importing the store directly in a component | Always use `useAppSelector` and `useAppDispatch` |
| Writing action name constants by hand | Use `createSlice`; it makes them for you |
| One huge slice | Split into small slices by feature |
| The old `connect()` wrapper | Use the hooks in all new code |

> ✅ **Rule of thumb:** if you find yourself writing the same code over and over, you're probably not
> using Redux Toolkit as intended. It exists to remove repetitive code. If something feels tedious,
> check the Toolkit docs for a built-in way.
