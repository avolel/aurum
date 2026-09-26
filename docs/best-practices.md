# Aurum - TypeScript/React Native Code Smells & Best Practices
 
> For file naming conventions, type vs interface guidelines, component naming, and import aliases, see [CLAUDE.md](../../../CLAUDE.md).
 
---
 
## Code Smells & Anti-Patterns
 
### 1. Comment Mismatch with Filename
 
**Problem:**
```typescript
// File: Field.model.ts
//field.model.ts  // Comment doesn't match actual filename casing
```
 
**Solution:** Keep comments accurate or remove them:
```typescript
// File: Field.model.ts
// Field.model.ts - Field interface and factory
```
 
### 2. Using `any` Type
 
**Problem found in this project:**
```typescript
// BAD: Using any defeats TypeScript's purpose
searchResults: any[];
edi271: any | null;
account: AccountInfo | any | null;
```
 
**Solution:** Define proper types:
```typescript
// GOOD: Use specific types
searchResults: SearchResult[];
edi271: Edi271Response | null;
account: AccountInfo | null;
```
 
### 3. Inconsistent Default Export Patterns
 
**Problem found in this project:**
```typescript
// File 1: Named export only
export interface Field { ... }
 
// File 2: Default export with named
export interface Question { ... }
export default emptyQuestion;
 
// File 3: Different pattern
export const userService = { ... };
export default userService;  // Redundant
```
 
**Solution:** Be consistent:
```typescript
// PREFERRED: Named exports for types, default for factories/instances
export interface Field { ... }
export const createEmptyField = (index: number): Field => ({ ... });
export default createEmptyField;
```
 
### 4. Type Assertions to Bypass Type Checking
 
**Problem:**
```typescript
// BAD: Using `as any` to silence TypeScript
w={width as any}
```
 
**Solution:** Fix the type properly:
```typescript
// GOOD: Proper typing
w={width}  // Ensure width has correct type in interface
```
 
### 5. Duplicate Type Definitions
 
**Problem:**
```typescript
// In store/store.ts
export type RootState = ReturnType<typeof store.getState>;
 
// In store/index.ts (duplicate)
export type RootState = ReturnType<typeof store.getState>;
```
 
**Solution:** Single source of truth:
```typescript
// Only in store/index.ts or store/store.ts, not both
export type RootState = ReturnType<typeof store.getState>;
```
 
### 6. Mixed Naming Conventions for Similar Files
 
**Problem:**
```typescript
// Different patterns for model files:
entra-user.model.ts  // kebab-case
user.model.ts        // lowercase
Field.model.ts       // PascalCase
legacy-form.models.ts // plural
```
 
**Solution:** Consistent pattern:
```typescript
// ALL model files should use PascalCase
EntraUser.model.ts
User.model.ts
Field.model.ts
LegacyForm.model.ts
```
 
---
 
## Best Practices
 
### 1. Use `type` Imports for Types Only
 
```typescript
// GOOD: Explicit type-only imports
import type { Field } from '@models/form/Field.model';
import type { RootState } from '@store';
 
// When you need both value and type
import emptyField, { type Field } from '@models/form/Field.model';
```
 
### 2. Document DTOs That Mirror Backend
 
```typescript
// GOOD: Document the source
/**
 * Mirrors App.Application.Users.DTOs.UserDto
 * JSON is expected to be camelCase (ASP.NET Core default).
 */
export interface UserDto {
  id?: string | null;
  // ...
}
```
 
### 3. Use Descriptive Union Types
 
```typescript
// GOOD: Self-documenting
type AsyncStatus = "idle" | "loading" | "success" | "error";
type TabKey = "edit" | "options" | "logic";
```
 
### 4. Prefer `interface` Extending Over Intersection
 
```typescript
// GOOD: Cleaner, better error messages
interface C2CardContainerProps extends Omit<CardProps, 'width' | 'radius'> {
  width?: number | string;
  radius?: number;
}
 
// AVOID: Harder to debug
type C2CardContainerProps = Omit<CardProps, 'width' | 'radius'> & {
  width?: number | string;
};
```
 
### 5. Constants Should Be Typed Arrays or Enums
 
```typescript
// GOOD: Typed constant array
export const HTML_INPUT_TYPES = [
  'Text',
  'Email',
  'Number',
] as const;
 
type HtmlInputType = typeof HTML_INPUT_TYPES[number];
```
 
### 6. Service Objects Pattern
 
```typescript
// GOOD: Consistent service pattern
export const userService = {
  async getUsers(params: GetUsersQueryParams = {}): Promise<GetUsersApiResponse> {
    return apiService.get<GetUsersApiResponse>("/users", { params });
  },
  // ...
};
 
export default userService;
```
 
### 7. Redux Slice Organization
 
```typescript
// GOOD: Co-locate types with slice
// form.types.ts - only if complex, otherwise inline in reducer
// form.reducer.ts - slice definition
// form.actions.ts - async thunks if needed
```
 
### 8. URL-Synced / Deep-Linkable State
 
Any screen state that should survive a page reload or be reproducible via the status-bar Share
button (which just copies `window.location.href`) — active tab, an open drawer and which id it's
showing, an edit-mode flag, filter state — goes through the shared hooks in
`src/hooks/urlState/` (`useUrlParamState`, `useConsumeUrlParamOnce`, `useUrlParamSync`,
`useUrlParamsBag`, `useDeepLinkedDrawerParams`), never a hand-rolled
`useLocalSearchParams`/`router.setParams` pair. This consolidates a pattern that was independently
reimplemented (with subtly different, sometimes buggy, edge-case handling) across a dozen screens
before being unified — see each hook's doc comment for which shape fits which case:
 
- A single reactively-derived value (a tab key, a filter id) → `useUrlParamState`.
- Applying an incoming deep-link value into local state once → `useConsumeUrlParamOnce`.
- Mirroring local state out to one or more params → `useUrlParamSync`.
- A multi-field filter bag where the URL is the sole source of truth → `useUrlParamsBag`.
- "Open this drawer/modal by id, resolved via an already-fetched list with a service-fetch
  fallback, with a tab" (the Admin Roles/Teams/Users/Form Types shape) → `useDeepLinkedDrawerParams`.
 
```typescript
// GOOD
import { useUrlParamState, enumCodec } from '@hooks/urlState';
const [activeTab, setActiveTab] = useUrlParamState('tab', enumCodec(TAB_KEYS, 'overview'));
 
// BAD — reinvents ref-guarded read + router.setParams write from scratch
const params = useLocalSearchParams<{ tab?: string }>();
const [activeTab, setActiveTab] = useState(params.tab ?? 'overview');
useEffect(() => { router.setParams({ tab: activeTab }); }, [activeTab]);
```
