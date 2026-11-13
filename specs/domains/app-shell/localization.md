# Localization

## Role

The UI's multilingual support: string dictionaries swapped on the fly (`en`, `ru`), resolved
from `ui.language`, with a flag selector in the window header and a key-parity test guarding
the dictionaries.

## Key Files

- `src/PrivateNetRouting.App/Localization/Loc.cs` — `Loc.S`, `Loc.F`, `Loc.Apply`, `Loc.LanguageChanged`
- `src/PrivateNetRouting.App/Localization/Strings.en.xaml`, `Strings.ru.xaml` — the dictionaries
- `src/PrivateNetRouting.Core/Configuration/AppLanguage.cs` — `Supported`, `Resolve`
- `src/PrivateNetRouting.App/LanguageFlag.cs` — `LanguageOption` + the drawn flag marks

## Behavior

- XAML references strings as `{DynamicResource Key}`; code uses `Loc.S(key)` / `Loc.F(key,
  args)` (invariant `string.Format`). A missing key renders as `[key]`.
- `Loc.Apply(language)` swaps the merged `Strings.<code>.xaml` dictionary (cached, identified
  by its source URI) and fires `LanguageChanged`; unsupported codes fall back to `en`.
- `AppLanguage.Resolve(preference, systemUiCulture)`: `"auto"`/empty -> the system culture's
  primary subtag matched against `Supported` (exact or primary, e.g. `ru-RU` -> `ru`), an
  explicit code matched the same way, anything else -> `en`.
- The selector (`LanguageFlag`, 21×14 drawn marks — a simplified Union Jack / three stripes,
  identity colors outside the theme tokens) shows the resolved language — there is no "system"
  option; `auto` is shown as the language it resolves to. A manual pick writes the concrete
  code to `ui.language`.
- The CLI is always English (no `Loc` usage there).

## Error Handling

- An unsupported/unknown code resolves to English — never a broken window.
- A swapped dictionary that lacks a key degrades to the `[key]` placeholder.

## Invariants

- A new language = one `Strings.<code>.xaml` + one code in `AppLanguage.Supported` — nothing
  else changes.
- The dictionaries are the only non-English content in the repository (rule #1).
- `LocalizationDictionariesTests` keeps every dictionary's key set and placeholder set equal to
  the `en` baseline, and `Supported` equal to the files on disk.

## Related Specs

- [App shell](README.md) — where the selector sits
- [Configuration](../configuration/README.md) — `ui.language`, `AppLanguage`
