# Redux Best Practices for React Native
 
**Architecture, Patterns & Performance Guidelines**
 
---
 
## 1. Project Structure
 
A well-organized project structure is critical for scalability and team productivity. Structure your Redux code using a feature-based approach that groups related logic together.
 
### 1.1 Recommended Directory Layout
 
```
app/
  store/
    index.ts              # Store configuration
    rootReducer.ts        # Combined reducers
    middleware.ts          # Custom middleware
  features/
    auth/
      authSlice.ts        # Slice (reducer + actions)
      authSelectors.ts    # Memoized selectors
      authThunks.ts       # Async operations
      authTypes.ts        # TypeScript interfaces
      __tests__/          # Co-located tests
    products/
      productSlice.ts
      productSelectors.ts
      productApi.ts       # RTK Query API definition
  hooks/
    useAppDispatch.ts     # Typed dispatch hook
    useAppSelector.ts     # Typed selector hook
  services/
    api.ts                # RTK Query base API
```
 
> ✅
**Feature-Based Organization:** Group all Redux logic (slice, selectors, thunks, types, tests) by feature domain rather than by file type. This makes it easy to find, modify, and delete entire features as a unit.
 
### 1.2 File Naming Conventions
 
| File Type | Naming Pattern |
|-----------|---------------|
| Slice files | `featureSlice.ts` (e.g., `authSlice.ts`) |
| Selector files | `featureSelectors.ts` (e.g., `authSelectors.ts`) |
| Thunk files | `featureThunks.ts` (e.g., `authThunks.ts`) |
| RTK Query APIs | `featureApi.ts` (e.g., `schedulingApi.ts`) — colocated in the feature folder,
**not** a shared `store/api/` bucket, and **not** `feature.api.ts`. This is the one file type that does not take the `.slice.ts`/`.reducer.ts` suffix CLAUDE.md lists, because it is not a hand-written reducer |
| Type definitions | `featureTypes.ts` (e.g., `authTypes.ts`) |
 
---
 
## 2. Redux Toolkit Configuration
 
Always use Redux Toolkit (RTK) as the standard approach. Never write Redux logic by hand — RTK eliminates boilerplate and enforces best practices by default.
 
### 2.1 Store Setup
 
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
      serializableCheck: {
        ignoredActions: ['persist/PERSIST'],
      },
      immutableCheck: __DEV__,
    }).concat(apiSlice.middleware),
});
 
setupListeners(store.dispatch);
 
export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
```
 
### 2.2 Typed Hooks
 
Create pre-typed hooks to avoid repeating type annotations throughout the app. Import these instead of the plain react-redux hooks.
 
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
 
> ❌
**Avoid:** Never import `useDispatch` or `useSelector` directly from react-redux in components. Always use the typed wrappers. This eliminates type casting and catches type mismatches at compile time.
 
---
 
## 3. Slice Design Patterns
 
Slices are the fundamental building block of modern Redux. Each slice owns a portion of state and defines the reducers and actions for that state.
 
### 3.1 Slice Structure Template
 
```typescript
// features/auth/authSlice.ts
import { createSlice, PayloadAction } from '@reduxjs/toolkit';
import { loginUser, logoutUser } from './authThunks';
import { AuthState } from './authTypes';
 
const initialState: AuthState = {
  user: null,
  token: null,
  status: 'idle',       // 'idle' | 'loading' | 'succeeded' | 'failed'
  error: null,
};
 
const authSlice = createSlice({
  name: 'auth',
  initialState,
  reducers: {
    clearError(state) {
      state.error = null;
    },
    updateProfile(state, action: PayloadAction<Partial<User>>) {
      if (state.user) {
        Object.assign(state.user, action.payload);
      }
    },
  },
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
 
### 3.2 State Shape Guidelines
 
- Keep state flat and normalized — avoid deeply nested objects.
- Use a consistent status enum: `'idle' | 'loading' | 'succeeded' | 'failed'`.
- Store server-provided IDs, not client-generated ones where possible.
- Only store data in Redux that is truly global or shared across screens.
- Keep form state, animation state, and UI-only state in local component state.
 
### 3.3 When to Use Redux vs Local State
 
| Use Redux For | Use Local State For |
|--------------|-------------------|
| Authentication & session data | Form input values |
| Cached server data shared across screens | UI toggles (modal open, accordion expanded) |
| App-wide settings and preferences | Animation and gesture state |
| Data needed after navigation | Ephemeral data (tooltips, hover state) |
| Offline-capable data | Scroll position |
 
---
 
## 4. Selectors & Memoization
 
Well-designed selectors are crucial for performance in React Native. They prevent unnecessary re-renders by ensuring components only update when their specific data changes.
 
### 4.1 Selector Patterns
 
```typescript
// features/products/productSelectors.ts
import { createSelector } from '@reduxjs/toolkit';
import { RootState } from '../../store';
 
// Simple selectors (no memoization needed)
export const selectProducts = (state: RootState) => state.products.items;
export const selectProductStatus = (state: RootState) => state.products.status;
 
// Derived data selectors (memoized with createSelector)
export const selectActiveProducts = createSelector(
  [selectProducts],
  (products) => products.filter((p) => p.isActive)
);
 
// Parameterized selectors
export const selectProductById = createSelector(
  [selectProducts, (_state: RootState, id: string) => id],
  (products, id) => products.find((p) => p.id === id)
);
```
 
### 4.2 Selector Rules
 
1. Always co-locate selectors with their slice in a dedicated selectors file.
2. Use `createSelector` for any computation or filtering — never derive data inside components.
3. Keep selectors small and composable — build complex selectors from simpler ones.
4. Never create new object or array references inside selectors unless using `createSelector`.
5. Prefer multiple fine-grained `useAppSelector` calls over a single call returning a large object.
 
> ❌
**Common Anti-Pattern:** Calling `useAppSelector(state => ({ a: state.x.a, b: state.y.b }))` creates a new object every render, causing unnecessary re-renders. Use separate `useAppSelector` calls or `createSelector` instead.
 
---
 
## 5. Async Operations & Data Fetching
 
Choose the right async pattern based on the complexity and caching needs of your data operations.
 
### 5.1 RTK Query (Preferred for Server State)
 
RTK Query should be the default choice for any server data fetching. It provides automatic caching, request deduplication, background refetching, and optimistic updates out of the box.
 
> **In this repo, build on `apiBaseQuery` (`src/store/api/apiBaseQuery.ts`), never
> `fetchBaseQuery`.** The generic example below is the library's shape, not ours:
> `fetchBaseQuery` bypasses the axios interceptors that attach the token, unwrap the
> `ApiResponse<T>` envelope, and raise the `SESSION_EXPIRED` overlay. See
> [scheduling-rtk-query-plan.md](scheduling-rtk-query-plan.md) ›
*Phase 3*.
 
```typescript
// services/api.ts
import { createApi, fetchBaseQuery } from '@reduxjs/toolkit/query/react';
 
export const apiSlice = createApi({
  reducerPath: 'api',
  baseQuery: fetchBaseQuery({
    baseUrl: Config.API_URL,
    prepareHeaders: (headers, { getState }) => {
      const token = (getState() as RootState).auth.token;
      if (token) headers.set('Authorization', `Bearer ${token}`);
      return headers;
    },
  }),
  tagTypes: ['Product', 'User', 'Order'],
  endpoints: () => ({}), // Inject endpoints in feature files
});
```
 
### 5.2 createAsyncThunk (For Complex Operations)
 
Use `createAsyncThunk` for operations that need side effects beyond simple data fetching, such as multi-step workflows, local storage writes, or navigation triggers.
 
```typescript
// features/auth/authThunks.ts
import { createAsyncThunk } from '@reduxjs/toolkit';
 
export const loginUser = createAsyncThunk(
  'auth/login',
  async (credentials: LoginRequest, { rejectWithValue }) => {
    try {
      const response = await authApi.login(credentials);
      await SecureStore.setItemAsync('token', response.token);
      return response;
    } catch (error) {
      return rejectWithValue(parseApiError(error));
    }
  }
);
```
 
### 5.3 Decision Matrix
 
| Scenario | Recommended Pattern | Reason |
|----------|-------------------|--------|
| CRUD operations on server data | RTK Query | Built-in caching & invalidation |
| Authentication flows | createAsyncThunk | Complex side effects needed |
| File upload with progress | createAsyncThunk | Progress tracking required |
| Search with debouncing | RTK Query + skip | Automatic caching per query arg |
| WebSocket real-time data | RTK Query streaming | Built-in lifecycle management |
 
---
 
## 6. React Native Performance
 
React Native has unique performance considerations compared to web React. Redux patterns must be adapted to minimize bridge crossings and prevent jank on lower-end devices.
 
### 6.1 FlatList Optimization
 
```typescript
// Avoid: Re-rendering entire list on any state change
const ProductList = () => {
  const products = useAppSelector(selectActiveProducts);
  const renderItem = useCallback(
    ({ item }: { item: Product }) => <ProductCard product={item} />,
    []
  );
  return (
    <FlatList
      data={products}
      renderItem={renderItem}
      keyExtractor={(item) => item.id}
      removeClippedSubviews={true}
      maxToRenderPerBatch={10}
      windowSize={5}
    />
  );
};
 
// ProductCard should be React.memo'd
const ProductCard = React.memo(({ product }: Props) => {
  // Renders only when this specific product changes
  return <View>...</View>;
});
```
 
### 6.2 Performance Rules
 
1. Use `React.memo` on all list item components connected to Redux.
2. Prefer entity adapter's normalized shape for large collections.
3. Use `shallowEqual` as the equality function when selecting objects.
4. Avoid dispatching actions in rapid succession — batch with `unstable_batchedUpdates` or use RTK listeners.
5. Profile with React DevTools and Flipper before optimizing. Measure, don't guess.
6. Keep the serializable middleware enabled in `__DEV__` only to avoid production overhead.
 
### 6.3 Entity Adapter for Normalized State
 
When managing collections of items (users, products, messages), use `createEntityAdapter` to automatically normalize state and generate optimized CRUD reducers.
 
```typescript
import { createEntityAdapter } from '@reduxjs/toolkit';
 
const productsAdapter = createEntityAdapter<Product>({
  sortComparer: (a, b) => a.name.localeCompare(b.name),
});
 
const initialState = productsAdapter.getInitialState({
  status: 'idle' as const,
});
 
// Generates { ids: [], entities: {} } shape
// Provides: addOne, addMany, updateOne, removeOne, setAll, etc.
```
 
---
 
## 7. Middleware & Side Effects
 
### 7.1 RTK Listener Middleware (Preferred)
 
Use `listenerMiddleware` for reactive side effects that respond to dispatched actions. It replaces redux-saga and redux-observable with a simpler, more testable API.
 
```typescript
import { createListenerMiddleware } from '@reduxjs/toolkit';
 
export const listenerMiddleware = createListenerMiddleware();
 
listenerMiddleware.startListening({
  actionCreator: logoutUser.fulfilled,
  effect: async (_action, listenerApi) => {
    await SecureStore.deleteItemAsync('token');
    listenerApi.dispatch(apiSlice.util.resetApiState());
    navigationRef.reset({ index: 0, routes: [{ name: 'Login' }] });
  },
});
```
 
### 7.2 Middleware Selection Guide
 
| Use Case | Tool | Notes |
|----------|------|-------|
| React to actions | Listener middleware | First choice for side effects |
| Analytics & logging | Custom middleware | Lightweight, single-purpose |
| Complex async workflows | Listener middleware | Supports cancellation & forking |
| Token refresh | baseQuery wrapper | RTK Query's automatic retry |
 
---
 
## 8. Testing Strategy
 
Test Redux logic independently from components. Slices, selectors, and thunks should have dedicated unit tests.
 
### 8.1 Testing Priorities
 
| Layer | Priority | Tool | Coverage Target |
|-------|----------|------|----------------|
| Reducers / Slices | High | Jest | 90%+ |
| Selectors | High | Jest | 90%+ |
| Thunks | Medium | Jest + MSW | 80%+ |
| Connected components | Medium | RNTL + mock store | Critical paths |
| RTK Query endpoints | Medium | Jest + MSW | 80%+ |
 
### 8.2 Slice Test Example
 
```typescript
describe('authSlice', () => {
  it('should set loading state on login pending', () => {
    const state = authReducer(initialState, loginUser.pending('', credentials));
    expect(state.status).toBe('loading');
    expect(state.error).toBeNull();
  });
 
  it('should store user on login success', () => {
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
 
## 9. Persistence & Offline Support
 
### 9.1 Redux Persist Configuration
 
Use redux-persist with a whitelist approach — explicitly opt in slices rather than persisting everything. Use secure storage for sensitive data like tokens.
 
```typescript
import AsyncStorage from '@react-native-async-storage/async-storage';
import { persistReducer, persistStore } from 'redux-persist';
import * as SecureStore from 'expo-secure-store';
 
const persistConfig = {
  key: 'root',
  storage: AsyncStorage,
  whitelist: ['settings', 'offlineQueue'],
  blacklist: ['api'],  // Never persist RTK Query cache
  version: 1,
  migrate: createMigrate(migrations, { debug: __DEV__ }),
};
```
 
### 9.2 Persistence Rules
 
- Never persist RTK Query cache — it has its own cache management.
- Always use a whitelist, never rely on blacklist alone.
- Store tokens in SecureStore (Expo) or Keychain, never in AsyncStorage.
- Version your persist config and write migrations for state shape changes.
- Set a reasonable timeout for rehydration in the loading screen.
 
---
 
## 10. Common Anti-Patterns to Avoid
 
| Anti-Pattern | Correct Approach |
|-------------|-----------------|
| Storing everything in Redux | Only global or shared state belongs in Redux |
| Mutating state outside Immer (in createSlice) | Always use Immer's draft state inside reducers |
| Putting non-serializable values in state (Date, Map, Set) | Convert to plain objects/arrays or ISO strings |
| Dispatching inside reducers | Use listener middleware or thunks for side effects |
| Importing the store directly in components | Always use hooks (useAppSelector, useAppDispatch) |
| Writing manual action type constants | Use createSlice — it generates action creators automatically |
| Large monolithic slices | Split into focused slices by feature domain |
| Using connect() HOC | Prefer hooks API (useSelector, useDispatch) in all new code |
 
> ✅
**Golden Rule:** If you find yourself writing boilerplate, you are probably not using Redux Toolkit correctly. RTK exists to eliminate repetitive code — if something feels tedious, check the RTK docs for a built-in solution.
 