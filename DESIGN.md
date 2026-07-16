# Waffle Browse Design System

## Direction

Waffle Browse uses **Toasted Soft Grid**: a warm, compact desktop-utility design that keeps the speed and information density of a native file manager while removing the hard, debug-tool feeling of the original UI.

The name "Waffle" is expressed indirectly:

- the multi-panel workspace is the grid;
- rounded, closely packed controls provide the snack-like tactility;
- cream, espresso, butter, and toasted-honey colors provide warmth;
- precise hairlines and compact spacing keep the interface crisp.

Never use literal waffle photography, food textures, mascots, checkerboard backgrounds, or novelty decoration. The UI must remain a professional Windows productivity tool.

Visual reference: `docs/waffle-soft-grid-reference.png`.

## Product Principles

1. **Native first.** Preserve Windows Explorer behavior, keyboard navigation, native context menus, and familiar system icons.
2. **Dense, not cramped.** File lists stay compact; surrounding chrome gains enough breathing room to make the hierarchy obvious.
3. **One warm accent.** Toasted honey marks focus, selection, and the current workspace. It is not decorative fill.
4. **Soft grid.** Panels are independent compact modules separated by a 4px gutter, not boxes fused by heavy borders.
5. **Quiet depth.** Use surface contrast and 1px hairlines before shadows. Avoid gradients, glass effects, and glow.

## Color Tokens

### Dark theme

| Token | Value | Usage |
| --- | --- | --- |
| `window` | `#1C1A17` | Window and workspace canvas |
| `panel` | `#24211C` | Panel body |
| `toolbar` | `#211F1B` | Global toolbar and panel chrome |
| `control` | `#2E2A24` | Search, address fields, segmented controls |
| `line` | `#494136` | Panel borders and dividers |
| `line-subtle` | `#38332C` | Row and toolbar separators |
| `text` | `#F5EFE4` | Primary labels and file names |
| `text-muted` | `#BFB5A6` | Metadata, status, placeholder text |
| `honey` | `#D99018` | Active panel ring and keyboard focus |
| `honey-soft` | `#4A3516` | Selected tab and selected row surface |
| `honey-text` | `#FFD98A` | Text on subtle honey surfaces |
| `shell` | `#171612` | Native Explorer host background |

### Light theme

| Token | Value | Usage |
| --- | --- | --- |
| `window` | `#F5F0E7` | Window and workspace canvas |
| `panel` | `#FFFCF7` | Panel body |
| `toolbar` | `#F0E9DE` | Global toolbar and panel chrome |
| `control` | `#FFFFFF` | Search and address fields |
| `line` | `#D8CFC1` | Panel borders and dividers |
| `line-subtle` | `#E7DFD4` | Row and toolbar separators |
| `text` | `#25211D` | Primary labels and file names |
| `text-muted` | `#756D62` | Metadata and status text |
| `honey` | `#B86F08` | Active panel ring and keyboard focus |
| `honey-soft` | `#F8E6B8` | Selected tab and selected row surface |
| `honey-text` | `#5A3505` | Text on honey surfaces |
| `shell` | `#FFFFFF` | Native Explorer host background |

Semantic error, warning, and success colors must remain distinct from the honey brand accent.

## Typography

- UI family: `Segoe UI`, falling back to the Windows system sans serif.
- Icon families: `Segoe MDL2 Assets` for existing Windows actions and Microsoft Fluent UI System Icons for exact layout diagrams. Never substitute improvised geometry when a library icon exists.
- App title: 20px, semibold.
- Standard UI and file rows: 13–14px, regular.
- Tab labels and compact controls: 12–13px, medium.
- Metadata/status: 12px, regular.
- Prefer truncation with a tooltip over wrapping inside tabs, toolbars, and file rows.

## Spacing

Use a 4px base unit.

| Step | Value | Typical usage |
| --- | --- | --- |
| `xxs` | 4px | Icon-to-label gap, tight inset |
| `xs` | 8px | Panel gutters, compact groups |
| `sm` | 12px | Toolbar group padding |
| `md` | 16px | Major control separation |
| `lg` | 24px | Empty-state or dialog padding |

The workspace uses 4px between panels and approximately 6px at the outer window edge. Compact workspaces should favor visible file content over decorative canvas.

## Shape and Elevation

- Main panels: square outer corners. The native Explorer host cannot be safely clipped to a rounded WPF border, so the active ring stays continuous instead of exposing broken arcs.
- Search and address fields: 7px radius.
- Tabs and compact buttons: 6px radius.
- Segmented-control container: 7px radius.
- Panel borders: 2px at all times so focus changes never resize the native shell host.
- Standard dividers: 1px.
- Shadows: none by default. A very subtle shadow is permitted only for transient menus or dialogs.

## Components

### Global toolbar

- Height target: 43–47px.
- Left: existing app icon and "Waffle Browse" title.
- Center: the global search, visually dominant and at least 420px wide when space allows.
- Right: theme control followed by one grouped layout preset control.
- Status copy is muted and truncates before it pushes the right-side controls.

### Search

- Placeholder: "파일 및 폴더 검색".
- 32px target height with a 7px radius.
- Honey focus ring; do not fill the entire input with the accent.
- Search action uses the Windows search glyph and remains accessible by name and tooltip.

### Layout presets

- Present presets as one segmented group.
- Preserve all current commands: single, two columns, two rows, three panels, four panels.
- Use exact layout diagrams: single view, two equal columns, two equal rows, primary-left three-panel split, and 2×2 grid. Each segment keeps a descriptive tooltip and automation name.

### Explorer panel

- Independent compact module with a 4px gap to adjacent panels.
- Inactive border uses `line`; active border uses `honey`.
- Tab strip and address toolbar are quieter than the file list and separated with subtle hairlines.
- Native Explorer remains the content authority; do not replace it merely to force styling.

### Tabs

- Selected tab uses `honey-soft` and `honey-text`.
- Hover uses a low-contrast warm surface.
- Tabs keep compact horizontal padding and expose close through a standard Windows close glyph.
- The add-tab action is always visible at the end of the strip.

### Navigation and address bar

- Back, forward, and up use Segoe MDL2 Assets.
- Icon buttons are 30px square with a 6px radius.
- Address input fills remaining width at 32px high with 2px vertical content padding. The surrounding row compensates with a 4px inset so panel chrome remains compact while path text stays fully visible.
- Hover and focus never change layout dimensions.

### File and search results

- Preserve compact details view and aligned metadata columns.
- Prefer row separators and selection tint over individual row cards.
- Keep file-type icons native where possible.

## Interaction States

- Hover: subtle warm surface change.
- Pressed: a slightly darker/lighter version of the hover surface.
- Selected: `honey-soft` surface with readable foreground.
- Keyboard focus: 2px honey ring or equivalent border change without layout shift.
- Disabled: reduce contrast while keeping the label readable.
- Drag target: translucent honey fill with a honey border; invalid targets remain red.

## Accessibility

- Keep primary text at WCAG AA contrast against its surface.
- Do not communicate active panel or selected tab by color alone; retain border/fill and clear structure.
- Every icon-only button needs a tooltip and `AutomationProperties.Name` or `AutomationId`.
- Preserve logical tab order and existing native Explorer keyboard behavior.
- Focus/active state changes must not resize or recreate content unnecessarily.
- Support text scaling by truncating flexible regions before clipping persistent controls.

## Implementation Guardrails

- Do not alter indexing scope, filesystem permissions, native shell security behavior, or user-selected roots for visual reasons.
- Do not replace native file operations or navigation shortcuts.
- Keep theme values centralized in `AppThemeResources` and resource keys; avoid isolated hard-coded colors in controls.
- Reuse `AppIcon.png` and Segoe MDL2 Assets. Do not introduce decorative raster assets unless the product gains a real empty state or onboarding surface that needs them.
- Validate both light and dark themes, one-panel and four-panel layouts, tab overflow, search focus, theme switching, and active-panel transitions.
