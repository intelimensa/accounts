# Production checklist per unit (design draft)

Status: **built** (design revision 8; see "As built" at the end for what the build settled). A first version (commit `d355941`:
electrode sets and mounting types as sets of assemblies and parts) was tried and replaced by this one: electrodes and mounting
both reference assemblies from one **parts and assemblies catalog**, defined by hand or by XML import (sections 6 and 7). The
checklist itself (sections 1 to 5) is unchanged apart from what steps 4 and 5 carry. Replaces the earlier idea of a free-text
"manufacturer notes" field on the unit, which is now a smaller part of this (section 9).

## 1. Goal

Today a `BciDevice` records only the electronics side of its life: reserved, flashed and confirmed (`BciDeviceStatus`, the
`BciDeviceEvent` history, firmware versions). Everything after the board is flashed (casing, assembly, electrodes, mounting,
QC, packaging) lives outside the system. We want one record per unit that shows, for each production step, **when it was done
and which account did it**, so the manufacturers can see where every unit stands, including units that already shipped, and
**what exactly it is made of** where that matters (its electrodes and its mounting).

## 2. The steps, in order

| # | Step | Done by | Carries |
|---|---|---|---|
| 1 | Electronics flashed | Quarry (automatic) | nothing |
| 2 | Casing manufactured | Manufacturer, by hand | nothing |
| 3 | Casing assembled | Manufacturer, by hand | nothing |
| 4 | Electrodes assigned | Manufacturer, by hand | one assembly from the catalog (the electrodes) |
| 5 | Mounting assigned | Manufacturer, by hand | one assembly from the catalog (the mounting clips) |
| 6 | QC passed | Manufacturer, by hand | nothing; only a pass is recorded, never a fail |
| 7 | Packaged | Manufacturer, by hand | nothing |

The steps are *shown* in this order, but the order is not enforced (section 3). The list is fixed in code as an enum (like
`BciDeviceEventType`), not configurable at runtime. Every device type (`msv1`/`msv2`/`msv3`) goes through all seven. Adding a step
later is a code change with no migration, because steps are rows (section 5).

## 3. Decided (the checklist)

1. **Steps 4 and 5 each point at one assembly** of the catalog (section 6), or at none (shipped units whose parts are unknown).
   There is no separate list of mounting types or electrode sets any more: a mounting clip type or an electrode set is just an
   assembly, and the assembly picker offers every active assembly.
2. **The order is the display order, not a rule.** Steps appear in the order above, but any step can be marked done at any time,
   on any unit that exists: electrodes can be assigned to a serial that hasn't been flashed yet (a held or reserved unit), casing
   steps can come before the flash, and Quarry's step 1 may be the last one to arrive. Nothing is refused for being out of order.
3. **Steps can be undone**, in any order, recorded as an event, never a silent delete.
4. **Date and time.** Marking a step done pre-fills the current date and time; the operator can edit it before saving, and edit
   it afterwards. A date in the future gets a warning.
5. **QC passed and Packaged are two separate steps.** QC records only the pass; there is no failed-QC state.
6. **All device types use all seven steps.**
7. **Steps are editable after the fact**, so the checklist of a unit that has already shipped can be corrected or completed (its
   date, its value, marking a step done or undone). This is how existing units get their history; there is no separate import.
8. **Who edits:** only the `Manufacturer` role marks, edits and undoes steps, on any unit and whoever recorded the step; the
   event history shows who changed what. **`Staff` get no access for now** (neither the checklists nor the catalog); a separate
   Staff view of this data may come later.
9. **Editing a unit that is already paired with a participant needs an explicit confirmation** (a confirm step on the page, and
   the event records that the unit was paired at the time). Same rules otherwise.
10. **Rekeying never resets any step.** If work really has to be redone, a manufacturer undoes the step by hand.
11. **Registration is unchanged.** It depends only on the unit being flashed and confirmed (`Manufactured`), as today.
    `BciDeviceStatus` stays exactly as it is, and the checklist never gates registration.
12. **A hint, not a rule, on Packaged:** marking Packaged while QC isn't done shows a quiet note ("QC not recorded") and still
    saves.
13. **Voided units** can have steps recorded, viewed and edited like any other, since the work happened. Nothing else about
    voiding changes.
14. **The catalog tools live under `/Manufacturing`, `Manufacturer` role only.** Any Manufacturer account may issue part numbers
    (a small team today; this may be tightened later).
15. **Tags decide which assemblies a step accepts.** Parts and assemblies carry tags (section 6). Each of steps 4 and 5 requires a
    tag, set in configuration (`Manufacturing:StepTags`; by default `electrode-set` and `mounting-clip`; blank = no requirement), so
    only assemblies carrying it can be assigned there. The picker shows only those.

Consequences of the order not being strict:

- There is no "undo from the end" rule and no missing-earlier-steps check; the page just shows each step done or not done.
- Existing units start with nothing done, except step 1, filled automatically from `ManufacturedAt` where there is one. A
  manufacturer completes the rest in any order.
- The date warning is only for future dates: out-of-order dates are normal (electrodes assigned before the flash).

## 4. Nothing open for the checklist

Everything above is agreed. The open points are in section 12 and concern the catalog's details only.

## 5. Data model: the checklist

One row per (unit, step), rather than two or three columns per step on `BciDevice`. Rows keep `BciDevice` from growing, let a
new step appear with no migration, and make "which units lack step N" a simple query.

```
ProductionStep
  Id                guid
  BciDeviceId       guid      FK BciDevice (Restrict, as BciDeviceEvent)
  Step              int       enum ProductionStepType, stored as an integer, values never reused (as RekeyReason)
  PerformedAt       datetimeoffset   the date and time of the step; pre-filled with now, editable
  RecordedAt        datetimeoffset   when the server saw it, never editable
  PerformedByUserId string?   the account that marked it done (the Quarry account for step 1)
  Source            int       Manual | Quarry | Backfill
  AssemblyId        guid?     FK CatalogItem (an assembly), set only on steps 4 and 5
  Note              string?   the operator's remark on this step
  UNIQUE (BciDeviceId, Step)
```

- **The unit's checklist is those rows.** A step with no row is not done.
- **Who is "performed by".** The account that marked the step done. If another manufacturer later edits the date or value,
  the step keeps its original account and the edit is in the event history, so "performed by" stays an honest statement.
- **Quarry's step.** Quarry logs in as a Manufacturer account, so `confirm` already knows the user. The server writes step 1
  in the same transaction as the confirm (`UnitProvisioning`), `Source = Quarry` (`Manual` on the manual confirm page). No new
  Quarry call is needed. `Backfill` is the migration's step 1 for units confirmed before this existed. Steps on a unit that is
  not flashed yet are ordinary rows on a `Reserved` (or held) `BciDevice`; the unit has to exist, so its serial must already be
  allocated or held.
- **Time is UTC** everywhere on these pages (entered, shown, labelled).
- **Strings:** SQLite `TEXT` has no length limit and nothing enforces one, so `Note` gets a length check in the service (1000
  characters, no control characters except newline).
- **Account display.** `PerformedByUserId` is the Identity user id, shown as the email. If an account is ever deleted the row
  keeps the id and the page shows "(removed account)".

## 6. The parts and assemblies catalog

### The idea

Manufacturing keeps two kinds of things in one catalog, each identified by a **part number**:

- A **part** is indivisible: a single-body 3D-printed piece, a screw, a cable.
- An **assembly** contains one or more children, each a part or another assembly, each with a count (default 1). Assemblies nest
  to any depth, with parts as the leaves.

The Manufacturer first enters the available parts, then defines assemblies that contain them. A unit's electrodes (step 4) and
mounting (step 5) each reference one assembly. Which assemblies suit which step is not enforced: the picker lists every active
assembly.

### The part-number rule (decided)

**A part number never changes meaning.** Name and description may be corrected, but a number always denotes the same physical
thing. A revision of a part gets a **new part number**, and a record says which older one it **supersedes**. The same holds for
assemblies: if one screw is replaced inside an assembly, the assembly and every assembly above it that contains it get new part
numbers too. This is the convention car manufacturers use for their parts. The description should ideally point to a frozen CAD
file for that exact part; that is not enforced (an optional free-text `Reference` field is kept for it).

Why it matters: a unit that references assembly `10005` carries the same parts forever, so there is no snapshot, no version
bookkeeping, and "what is on this unit" is always the live tree. The software has to enforce the rule, not just rely on it:

- **An assembly's contents are frozen at its first reference**, whether another assembly lists it as a child or a unit is assigned
  it. Until then it is a **draft** whose children and counts can be edited freely (this is how a tree is built). A frozen
  assembly's contents never change; to change them you make a **copy under a new part number** (a revision).
- A frozen assembly therefore only contains frozen assemblies (a child is frozen the moment its parent lists it), so a whole tree is
  immutable below any assembly a unit points at.
- **Name, description and reference stay editable** on parts and assemblies, as the one place meaning can drift, so every change
  to them is kept in a history (who, when, which field, old and new value).
- **Nothing is deleted, only retired.** A retired item can't be added to an assembly or assigned to a unit, and stays where it
  already is. A part number issued by mistake and never used is simply retired.

### Tags

Every part and assembly can carry **tags**: short labels (1 to 40 lowercase letters, digits, `-` or `_`; typed in any case, stored
lowercase), at most 20 per item, written as a comma-separated list. They are separate from name and description and are used to
**filter the catalog** (an item must carry every tag asked for) and to **say what an assembly may be used as**: each of steps 4 and 5
requires a tag, configured in `Manufacturing:StepTags` (defaults `electrode-set` for electrodes and `mounting-clip` for mounting;
a blank value means no requirement). Only assemblies carrying the step's tag can be assigned there, and the picker lists only those.

Tags are freely editable, with each change kept in the history, except that **a tag a unit's step relied on is locked on that
assembly**: when a unit is assigned an assembly at a step, the step's required tag becomes locked on it, permanently, like the
assembly's contents (undoing the assignment doesn't unlock it, because the history still points at it). A locked tag can't be removed;
other tags can always be removed, and any tag can always be added. A unit that already carries an assembly keeps it whatever its tags
are later. A copy ("Copy as new part number") gets the same tags, unlocked.

### Supersedes

An item may supersede one or more older items ("old part numbers"). This is recorded, not enforced: it changes no contents. It
is used to warn ("a newer revision exists: X") when someone assigns a unit a superseded assembly, which stays allowed. Rules:
the old and new item must be the same kind (part or assembly), an item can't supersede itself, and the links form no cycle.
Chains (A superseded by B superseded by C) are followed to the newest. Several old numbers may be superseded by one new one, and
one old number by several. A free-text note on a link, and an "interchangeable or not" flag as car makers keep, are left for later.

### Data model

One table holds both kinds, so a part number is unique across parts *and* assemblies by construction and a reference never
needs to say which kind it means.

```
CatalogItem
  Id               guid
  Kind             int       Part | Assembly
  PartNo           string    1..64 chars, letters, digits, '-' and '_'; unique, compared case-insensitively; a string, never a number
  Name             string    1..100 chars
  Description      string?   up to 1000 chars
  Reference        string?   up to 200 chars, free text (for example a CAD file or revision); not interpreted
  CreatedAt, CreatedByUserId
  LastModifiedAt, LastModifiedByUserId     of the item's own fields and (for a draft assembly) its children
  FrozenAt         datetimeoffset?          assemblies only; set at the first reference, never cleared
  RetiredAt, RetiredByUserId

CatalogChild                                  the contents of an assembly
  AssemblyId, ChildId (FK CatalogItem), Position int, Count int (1..9999)
  UNIQUE (AssemblyId, ChildId)                one line per child; Count covers repeats

CatalogSupersession
  NewId, OldId (FK CatalogItem), CreatedAt, CreatedByUserId       UNIQUE (NewId, OldId)

CatalogTag                                    one row per item and tag
  ItemId (FK CatalogItem), Tag string (1..40: lowercase letters, digits, '-', '_'), FrozenAt datetimeoffset?     KEY (ItemId, Tag)

CatalogChange                                 history of name / description / reference edits
  Id, ItemId, At, ByUserId, Field ("Name", "Description", "Reference" or "Tags"), OldValue, NewValue
```

- **Part numbers carry no meaning for the software.** Numbering ranges (for example 1xxxx for assemblies, 5xxxx for parts) are
  the manufacturer's convention and are never parsed or checked.
- **No cycles.** An assembly may not contain itself, directly or through its children (checked on every edit and import), and
  nesting is limited to 20 levels. Reusing one sub-assembly in several places is fine.
- **A child is a part or an assembly, never retired,** at the time it is added.
- **Limits:** at most 500 children per assembly (guards, not design numbers). Control characters are refused in every text field.
- **Effective quantities.** The flattened parts list of an assembly multiplies counts down the tree and adds a part that appears in
  several branches. It is computed when needed, never stored.
- **Unit counts and queries.** "Which units carry part X" walks each unit's assembly down. At this scale (hundreds of items,
  thousands of units) this is done in memory with one query for the catalog graph and one for the unit assignments.

### Assigning to a unit

Step 4 or 5 is marked with one assembly: not retired, with at least one child (an empty draft is refused). Assigning it freezes
it. If it has been superseded the page says so and saves anyway. Changing the assembly of a step is a step edit and records the
old and new part number in the event. The unit page shows the assembly's tree, expanded, with effective quantities.

## 7. The XML format (import and export)

The catalog can be exported to and imported from XML, so the manufacturer can keep it in files, review it in a diff, and move it
between servers. The file is a plain, human-writable document, not an attempt to be clever.

```xml
<Catalog Format="1">
  <Parts>
    <Part PartNo="50002" Name="Electrode" Description="Silver chloride electrode"/>
    <Part PartNo="10002" Name="FrontPlateL" Description="front plate with &quot;L&quot; marking"/>
    <Part PartNo="50031" Name="ElectrodePiston" Description="Cylinder-shaped electrode receptacle" Supersedes="50001"/>
  </Parts>
  <Assemblies>
    <Assembly PartNo="10005" Name="ElectrodeTerminal" Description="Short electrode with arm, cap, piston and cup">
      <Part PartNo="50002"/>
      <Part PartNo="50031" Count="1"/>
    </Assembly>
    <Assembly PartNo="10000" Name="Electrode bundle" Description="Recording electrode bundle">
      <Part PartNo="10002"/>
      <Assembly PartNo="10005" Count="4"/>
    </Assembly>
  </Assemblies>
</Catalog>
```

### Format rules

- One root `<Catalog Format="1">`. The format version is required so the format can change later.
- **Definitions** are `<Part>` elements in `<Parts>` and `<Assembly>` elements in `<Assemblies>`. A definition has `PartNo` and `Name`
  (required), `Description`, `Reference`, `Tags` and `Supersedes` (optional), and `Retired="true"` on export for a retired item.
  `Supersedes` is a space-separated list of part numbers (so a part number can't contain a space); `Tags` is a comma-separated list.
- **References** are `<Part PartNo=… [Count=…]/>` and `<Assembly PartNo=… [Count=…]/>` *inside* an assembly. A reference has only
  `PartNo` and `Count` (an integer from 1 to 9999, default 1). The element name must match the kind of the item it names.
- Child order in the file is the display order.
- **Unknown elements and attributes are errors,** so a typo such as `Descripton` is reported instead of silently ignored.
- Text is XML: a double quote inside an attribute is `&quot;` (or use single-quoted attributes). There are no backslash escapes.
  Line breaks inside attribute values are not preserved (XML turns them into spaces).
- **Safe parsing:** DTDs prohibited and no external resolver (no XXE), files at most 5 MiB, 20,000 elements, nesting depth 20.
  A published XSD may be added so a file can be checked before upload.

### Import

Imports are **upsert by part number and never delete**: items missing from the file are left alone. The whole file is validated
first and **applied in one transaction or not at all**; every problem found is reported with its line number (up to 200), never just
the first. A "validate only" option runs all the checks and applies nothing.

For each item in the file:
- A new part number creates the item. An existing one must be the same kind.
- A **part** (or any item) updates its name, description and reference; each change is recorded in the history.
- A **draft assembly** has its children replaced by the file's list.
- A **frozen assembly** must list exactly the same children and counts (order is ignored); otherwise that item is an error and the
  file is rejected. Importing the same file twice therefore changes nothing.
- `Supersedes` links are added, never removed. `Retired="true"` retires the item; an absent or false value never un-retires.
- **Tags:** an absent `Tags` attribute leaves an existing item's tags alone; a present one (even `Tags=""`) sets them exactly. Removing
  a locked tag is an error.
- Every reference must resolve, to an item in the file or already in the catalog. Cycles, a part number used for both kinds, the
  same child twice in one assembly, and retired children (new references to them) are errors.
- References made by the import freeze their targets once the file has been applied (a child listed by a new parent is frozen).

### Export

The whole catalog, or **one assembly with everything below it** (a self-contained file to share or to review). In the second case
a `Supersedes` that points outside the export is left out. Parts first, then assemblies with children before parents. Importing an
export into an empty catalog reproduces it.

## 8. History

The checklist row shows the current state only. Every change (done, edited, undone) also appends a `BciDeviceEvent`, as the
existing lifecycle does, so there is one timeline per unit. Event types `StepRecorded`, `StepEdited` and `StepUndone` (values 5, 6
and 7; the existing ones run 0-4 and are never reused), a nullable `Step` on the event, and a `WasPaired` flag for the extra
confirmation. An edit records the old and new date, and for steps 4 and 5 the old and new part number, in the event's `Note`. The
catalog's own history is `CatalogChange` (section 6) plus who and when on each item; those are not unit events.

## 9. Notes

The original ask was notes on a unit. The per-step `Note` and the reason on an undo cover most of it. **Open:** is one free-text
note on the unit as a whole still wanted (`BciDevice.Notes`, a single nullable column, edited by the Manufacturer)? It costs one
column and is easy to add later, so it can wait until someone misses it.

## 10. Interfaces

- **Unit checklist** (`/Manufacturing/Unit`, Razor, cookie auth, `Manufacturer` policy): the seven steps with date, account, note,
  value and history; *Mark done* (date and time pre-filled, editable), *Edit* and *Undo*, in any order; a confirmation for a paired
  unit. Steps 4 and 5 have an assembly picker (suggestions only from assemblies carrying the step's required tag, superseded ones flagged) and, once set, the expanded
  tree with quantities. `/Manufacturing/Units` lists units with a progress column and step filters.
- **Catalog** (`/Manufacturing/Catalog`): parts and assemblies, searchable by number or name and filterable by tags (the tags in use
  are listed and clickable; the filter takes a comma-separated list, an item must carry all of them). A part page (create, edit
  name, description, reference and tags, retire, what supersedes it, which assemblies and units use it). An assembly page: a draft is
  edited (add a child by part number with a count, change a count, remove, retire); a frozen one is shown read-only with its tree,
  its quantities, the units that carry it, and **"Copy as new part number"** (type the new number; the children are copied into a
  draft; a checkbox records that it supersedes the original). The history of name and description changes is shown.
- **Import and export** (`/Manufacturing/Catalog/Import`): upload, validate only or apply, the line-numbered error list, and the
  export links.
- **Service:** the rules (freezing, cycles, supersedes, import) live in catalog services used by every page, tested alone;
  the pages only map results, as with reserve/confirm/void/rekey today.
- **API:** none. If a floor app should ever read or write the catalog, that is a new JSON contract and starts from
  `scripts/gen-api-vectors.py` (CLAUDE.md, "The JSON API's contract with its clients").
- **Staff view:** none for now (decision 8).

## 11. Migration and rollout

The first version's migration (`ProductionChecklist`, unpushed and never deployed) is **replaced**, not stacked: a new one creates
`ProductionSteps`, `CatalogItems`, `CatalogChildren`, `CatalogSupersessions`, `CatalogChanges` and the two `BciDeviceEvent`
columns, and fills step 1 for confirmed units from `ManufacturedAt` (as before). The placeholder mounting types are dropped: the
manufacturer enters real assemblies. The code of the first version's mounting-type and electrode-set pages and services is removed.
Deploy as usual: `scripts/deploy.sh plan` first, and the script handles the migration.

## 12. Confirmed

1. **One part-number space for parts and assemblies.** A number can't be both.
2. **An empty draft assembly can't be assigned to a unit.**
3. **Name, description and reference edits keep a history** (section 6).
4. **Import can retire but never un-retire,** and never deletes.
5. **No note or "interchangeable" flag on a supersedes link** in the first version (neither is in the XML).
6. **A locked tag is the one a unit's step relied on, locked on that assembly for good** (section 6, "Tags"); all other tags stay editable.

## 13. Out of scope for the first version

- **The replace action:** pick a part and its replacement, list every assembly above it, and create the new revisions with new part
  numbers in one go. Without it, a revision is made one assembly at a time with "Copy as new part number". It is the first thing to
  add after the catalog works.
- A reordering UI for children (order follows what was added or imported).
- Stock counts, per-piece serial or lot tracking, an interchangeability table, enforcing the CAD reference, a Staff view, an API.
- Any effect on registration (decision 11).

## 14. Next

1. Agree this revision (and answer section 12 if anything is wrong).
2. Build, in this order: the catalog model and migration, the catalog service (freezing, cycles, supersedes) with tests, the XML
   import and export with tests, the catalog pages, then re-point steps 4 and 5 at assemblies and remove the old electrode and
   mounting code.
3. Then the replace action.

## 15. As built

What the build settled, beyond the design above:

- **Where:** `Models/CatalogItem.cs`, `Models/ProductionStep.cs`; rules in `Manufacturing/CatalogService.cs` (with `CatalogGraph`, the
  in-memory walk used for trees, quantities, usage and cycle checks), `CatalogXml.cs` (import and export), `ProductionChecklist.cs`;
  pages `Pages/Manufacturing/Catalog`, `CatalogItem`, `CatalogImport`, `Unit`, `Units`. One migration, `ProductionChecklist`.
- **Part numbers** are stored with a `NOCASE` collation and a unique index, so the one number space is enforced by the database too.
- **Freezing** is set by `AddChildAsync` (a listed child assembly), `CopyAsNewAsync` (children of the copy), an import (what a draft
  assembly lists) and a unit's step 4 or 5. Because a listed assembly freezes at once, a cycle can't be built through the pages;
  the cycle check in `AddChildAsync` is defence in depth, and the import checks cycles over the merged graph.
- **Copy as new part number** works on parts too, and from frozen or retired sources. A copy's children that are retired stay in
  the copy (it is a copy). A superseded-by link can be removed again on the item page; imports never remove one.
- **Counts** are 1 to 9999; an assembly has at most 500 lines and 20 levels; XML files at most 5 MiB and 20,000 elements.
- **Unit pages:** the assembly picker for steps 4 and 5 is a part-number field with suggestions (typing a number works), and a
  superseded assembly is flagged on the page and in the saved-step warning, naming the newest revision. After an edit the page
  returns to the row or line that was changed.
- **Import report:** created / updated / unchanged counts, links added, items retired, and every problem with its line number
  (first 200). "Validate only" runs the same checks and counts.
- **Not built:** the replace action (next), reordering children, an XSD, the optional unit note, an API.
- **Tags** are rows (`CatalogTag`), so filtering and locking are queries. `CatalogService.SetTagsAsync` edits them (one history
  entry, field "Tags"); the XML import goes through the same `ApplyTags`. The locking happens in `ProductionChecklist` when a step is
  marked or edited to an assembly (`FreezeTagAsync`). `Manufacturing:StepTags` is validated at startup (only the two assembly steps,
  valid tags). A separate migration, `CatalogTags`, adds the table.

