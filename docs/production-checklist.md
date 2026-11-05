# Production checklist per unit (design draft)

Status: **built** (see "As built" at the end; the design below is what was agreed). Revision 7: six rounds of decisions from manufacturing are in (section 3). What is
still open is in section 4. The electrode set design is section 6. Replaces the earlier idea
of a free-text "manufacturer notes" field on the unit, which is now a smaller part of this (section 8).

## 1. Goal

Today a `BciDevice` records only the electronics side of its life: reserved, flashed and confirmed (`BciDeviceStatus`, the
`BciDeviceEvent` history, firmware versions). Everything after the board is flashed (casing, assembly, electrodes, mounting,
QC, packaging) lives outside the system. We want one record per unit that shows, for each production step, **when it was done
and which account did it**, so the manufacturers can see where every unit stands, including units that already shipped.

## 2. The steps, in order

| # | Step | Done by | Carries |
|---|---|---|---|
| 1 | Electronics flashed | Quarry (automatic) | nothing |
| 2 | Casing manufactured | Manufacturer, by hand | nothing |
| 3 | Casing assembled | Manufacturer, by hand | nothing |
| 4 | Electrodes assigned | Manufacturer, by hand | one electrode set (section 6) |
| 5 | Mounting assigned | Manufacturer, by hand | one mounting clip type, from a short list |
| 6 | QC passed | Manufacturer, by hand | nothing; only a pass is recorded, never a fail |
| 7 | Packaged | Manufacturer, by hand | nothing |

The steps are *shown* in this order, but the order is not enforced (section 3). The list is fixed in code as an enum (like
`BciDeviceEventType`), not configurable at runtime. Every device type
(`msv1`/`msv2`/`msv3`) goes through all seven. Adding a step later is a code change with no migration, because steps are rows
(section 5).

## 3. Decided

1. **Mounting** is one of a few clip types, and **electrodes** are assigned as an electrode set: assemblies made of parts, with counts
   (section 6). Both are lists that **Manufacturers** maintain themselves, with an edit tool. The mounting
   list starts with two placeholders, "Padded clips" and "Bose tight clips", that the Manufacturer corrects or replaces (the
   real types are not known yet). Neither list is ever deleted from, only retired (decision 15).
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
   event history shows who changed what. **`Staff` get no access for now** (neither the checklists nor the lists); a separate
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
14. **The list tools** (mounting types and electrode sets) live under `/Manufacturing`, `Manufacturer` role only.
15. **Lists are retire-only.** A mounting type or an electrode set is never deleted; a retired one stays on the units that
    carry it and can't be picked again (like `Config.RetiredAt`).

Consequences of the order not being strict:

- There is no "undo from the end" rule and no missing-earlier-steps check; the page just shows each step done or not done.
- Existing units start with nothing done, except step 1, filled automatically from `ManufacturedAt` where there is one. A
  manufacturer completes the rest in any order.
- The date warning is only for future dates. An earlier proposal to warn when a later step is dated before an earlier one is
  dropped, since out-of-order dates are now normal (electrodes assigned before the flash).

## 4. Still open

Nothing is open for the checklist itself. Decided in the last round and now folded into section 3:

- A unit that is paired with a participant needs an explicit confirmation before any change (decision 9).
- Marking Packaged while QC isn't done shows a quiet hint ("QC not recorded"); it never blocks (decision 12).
- Steps can be recorded on `Voided` units too (decision 13).
- The mounting clip list is entered by the Manufacturer in the edit tool; the placeholders "Padded clips" and "Bose tight clips"
  are the first two entries, to be corrected or replaced (decision 1).
- The list tools live under `/Manufacturing`, Manufacturer only (decision 14), and lists are retire-only (decision 15).

Nothing remains open from the manufacturing side; the document is ready for a final read-through.

## 5. Data model (proposed)

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
  Source            int       Manual | Quarry
  MountingTypeId    int?      FK MountingType, set only on step 5
  ElectrodeSetId    guid?     FK ElectrodeSet, set only on step 4 (section 6)
  Note              string?   the operator's remark on this step
  UNIQUE (BciDeviceId, Step)

MountingType
  Id, Key (unique), DisplayName, RetiredAt?      maintained by Manufacturers (section 9)

```

- **The unit's checklist is those rows.** A step with no row is not done.
- **Who is "performed by".** The account that marked the step done. If another manufacturer later edits the date or value,
  the step keeps its original account and the edit is in the event history, so "performed by" stays an honest statement.
- **Quarry's step.** Quarry logs in as a Manufacturer account, so `confirm` already knows the user. The server writes step 1
  in the same transaction as the confirm (`UnitProvisioning`), `Source = Quarry`. No new Quarry call is needed. Steps on a
  unit that is not flashed yet are ordinary rows on a `Reserved` (or held) `BciDevice`; the unit has to exist, so its serial
  must already be allocated or held.
- **Electrodes:** step 4 points at one electrode set (section 6). Step 4 can be done with no set.
- **Strings:** SQLite `TEXT` has no length limit, and nothing enforces one, so `Note` gets a length check in the service
  (proposal: 1000 characters, no control characters except newline).
- **Account display.** `PerformedByUserId` is the Identity user id, shown as the email. If an account is ever deleted the row
  keeps the id and the page shows "(removed account)".

## 6. Electrode sets

An **electrode set** is the complete electrode complement of a unit, defined in three fixed levels:

```
Electrode set        name, description, version, the account that last modified it and when
  Assembly  (1..n)   name, description, version, part number (optional), count
    Part    (1..n)   name, description, version, part number (optional), count
```

Manufacturers define and retire sets in an edit tool; step 4 assigns **one set** to a unit. The software prescribes nothing about
the contents: the manufacturer decides which assemblies a set has, what parts each has, and the counts.

For orientation only (examples of what a manufacturer would enter, not built in): a set could hold two recording assemblies
(each a plug, wires, a central hub, 6 arms and 7 electrode cups) and one reference assembly (a swingarm, a central hub, 2 arms and
2 electrode cups). The two recording assemblies may differ from each other. If they are identical, that is one assembly with
count 2 ("2 of"); if they differ, they are two assemblies, each with count 1.

### Shape

```
ElectrodeSet
  Id            guid
  Name          string    1..100 chars; (Name, Version) is unique among all sets, so a revision can share its name
  Description   string?   up to 1000 chars
  Version       string    1..32 chars, typed by the manufacturer (a code such as "A" or "1.0")
  PreviousId    guid?     the set this one was copied from, if it is a revision (set by New revision)
  CreatedAt, CreatedByUserId
  LastModifiedAt, LastModifiedByUserId     the Manufacturer-role account that last changed it, and when; shown on the set
  RetiredAt?, RetiredByUserId?

ElectrodeAssembly
  Id, ElectrodeSetId, Position
  Name          string    1..100 chars; unique within the set (with Version)
  Description   string?   up to 500 chars
  Version       string    1..32 chars, typed by the manufacturer
  PartNumber    string?   up to 64 chars, optional
  Count         int       1..99

ElectrodePart
  Id, ElectrodeAssemblyId, Position
  Name          string    1..100 chars; unique within the assembly (with Version)
  Description   string?   up to 500 chars
  Version       string    1..32 chars, typed by the manufacturer
  PartNumber    string?   up to 64 chars, optional
  Count         int       1..999
```

- **`Position`** is the order the manufacturer arranged things in, kept for display.
- **Version is just a field the manufacturer types**, at every level (a code like `A` or `1.0` is the habit). The software gives it
  no meaning: no ordering, no increment, no format check, no behaviour hangs on it. The one rule is that a set's name and version
  together are unique (among all sets, retired included), so that two sets can always be told apart on a unit's page; that
  constrains the pair, not what the version looks like.
- **Part number is optional and unenforced**, on parts and assemblies, kept for the future (a later parts catalog, exact
  matching, supplier references). It has no uniqueness rule and nothing uses it yet beyond display and search.
- **Limits** (guards, not design numbers): 20 assemblies per set, 100 parts per assembly. Control characters are refused, as for notes.
- **Parts are not shared.** The same hub used in two assemblies is typed in both. The tool makes that cheap by letting you
  **copy an assembly from another set** (a copy, not a link) and by suggesting names and versions already used (decision: no
  shared parts table yet).

### Assigning to a unit

A unit's step 4 points at **one** electrode set (`ProductionStep.ElectrodeSetId`), as step 5 points at **one** mounting type
(`MountingTypeId`): one of each per unit. The set is the whole complement, including the
counts of each assembly, so no separate quantity is needed at the unit level. Step 4 may be marked done with no set, which is how
shipped units whose electrodes are unknown get completed (decision 7). Assigning or changing a set follows every rule of
section 3: any order, a unit that isn't flashed yet, voided units, the paired-unit confirmation.

### Changing a set

What a shipped unit carries must not change underneath it, so **a set that has been assigned to any unit is frozen**: its assemblies,
parts, name, description and counts can no longer be edited. Until then it can be edited freely. To change a frozen set the tool
offers *New revision*: it copies the set with all its assemblies and parts into a new, editable set (same name, the version left for
the manufacturer to change, `PreviousId` pointing back), and the old one can then be retired. Units keep pointing at the revision they were assigned. A typo
in a frozen set is therefore fixed by a new revision, not an edit: the same rule as firmware builds and releases ("a fix is a new
version").

Retired sets stay visible on the units that carry them and can't be picked again (decision 15). A revision may reuse its
predecessor's name once that one is retired; the version is shown next to the name regardless.

### What it makes possible

- *What exactly is on this unit?* The unit's detail page expands the set into its assemblies and parts, with totals
  (assembly count times part count).
- *Which units carry this set, or this revision of it?* A direct query. The tool lists each set with how many units carry it,
  so retiring is an informed choice.
- *Which units carry this part?* A query over the nested rows, by part number when one was entered, otherwise by name and
  version.

### Settled with manufacturing

- Parts are identified by name and version; an optional part number is kept for the future.
- One electrode set and one mounting type per unit.
- "Manufacturer account" means the Manufacturer-role account that last modified the set (the creator is kept as well).
- Versions are plain typed fields with no enforced behaviour.

## 7. History

The checklist row shows the current state only. Every change (done, edited, undone) also appends a `BciDeviceEvent`, as the
existing lifecycle does, so there is one timeline per unit. This adds event types such as `StepRecorded`, `StepEdited` and
`StepUndone` (values 5, 6 and 7; the existing ones run 0-4 and are never reused), plus a nullable `Step` on the event so the page
can say which step. Changing a unit's electrode set is a step edit, and records the old and new set. An electrode set's
creation, revision and retirement are recorded on the set itself (who and when, section 6); they are not unit events. An edit records the old and new date. The event's existing `Note` carries the reason for an undo. An event
on a unit that is paired with a participant is marked as such (decision 9).

## 8. Notes

The original ask was notes on a unit. The per-step `Note` and the reason on an undo cover most of it. **Open:** is one
free-text note on the unit as a whole still wanted (`BciDevice.Notes`, a single nullable column, edited by the Manufacturer)?
It costs one column and is easy to add later, so it can wait until someone misses it.

## 9. Interfaces

- **Manufacturing page** (`/Manufacturing/Units`, Razor, cookie auth, `Manufacturer` policy): a unit's detail shows the steps
  with date, account and note; each has *Mark done* (date and time pre-filled, editable), *Edit* and *Undo*, in any order. On a
  paired unit each change asks for a confirmation first. A list view with a column per step and a filter ("casing done, not yet assembled") is what makes this
  useful on the floor. Layout follows the existing `ManufacturingPageModel` pages.
- **Service:** the rules (future-date warning, paired-unit confirmation, history) live in `UnitProvisioning` or a sibling service it
  shares, tested alone, and the page only maps results, as with reserve/confirm/void/rekey today.
- **Mounting types and electrode sets:** edit tools under `/Manufacturing` (add, rename, retire), `Manufacturer` role;
  The electrode tool lists sets with their unit counts, edits unfrozen ones, offers *New revision* on frozen ones and
  *Retire*, and has no delete (decisions 14, 15; section 6).
- **API:** nothing needed for step 1 or for the page. If Quarry or a floor app should ever record steps 2-7, that is a change to
  the JSON contract: start from `scripts/gen-api-vectors.py`, regenerate `testdata/api-vectors.json`, `./sync-vectors.sh --fix`,
  update Quarry's client (CLAUDE.md, "The JSON API's contract with its clients"). The first version skips it.
- **Staff view:** none for now (decision 8); a separate one may come later.

## 10. Migration and rollout

One migration: `ProductionSteps`, `MountingTypes`, `ElectrodeSets`, `ElectrodeAssemblies` and `ElectrodeParts`, plus `BciDeviceEvent.Step` (and `BciDevice.Notes` if kept). A data step
fills step 1 for existing units from `ManufacturedAt`. The migration also seeds the two placeholder mounting types (decision 1). Shipped units are completed by hand through the page afterwards
(decision 7). Deploy as usual: `scripts/deploy.sh plan` first, and the script handles the migration.

## 11. Out of scope

- Stock counts of casings, electrodes or mounts (only the mounting-type list and the electrode set list).
- A shared parts table or shared assemblies (copying is supported, linking is not), more than three levels of nesting, and
  per-piece serial or lot tracking (section 6, unless manufacturing asks for them).
- Anything about participants: the checklist is internal and never appears in the participant portal or the API they use.
- Printing labels or packing slips.
- Any effect on registration (decision 11), and any Staff view for now (decision 8).

## 12. Next

1. Read it through once more and agree it (the spreadsheet, if it still adds anything, goes here).
2. Settle small wording or limit changes.
3. Build: model and migration, service rules with tests, the Units page and mounting list, then the electrode set tool and
   step-4 assignment, and the API only if wanted.

## 13. As built

Differences and details settled while building (the rest is as designed above):

- **Where:** models in `Models/ProductionStep.cs`; rules in `Manufacturing/ProductionChecklist.cs`, `MountingTypeService.cs` and
  `ElectrodeSetService.cs`; pages `Pages/Manufacturing/Unit`, `MountingTypes`, `ElectrodeSets`, `ElectrodeSet` (cards on
  `/Manufacturing`, a checklist-progress column and step filters on `/Manufacturing/Units`, which can include units not issued
  through the reserve flow so shipped units can be completed). Migration `ProductionChecklist`. Tests:
  `Manufacturing/ProductionChecklistTests.cs`, `ProductionListsTests.cs`.
- **Time is UTC** everywhere on these pages (entered, shown, labelled), so there is no browser time-zone guesswork.
- **Source** has a third value, `Backfill`, for step 1 rows the migration made from `ManufacturedAt`; Quarry confirms are `Quarry`,
  the manual confirm page records `Manual`.
- **Step 4 and step 5 may be done with no value** (electrode set or mounting type "not specified"), like step 4 in decision 7, so
  shipped units with unknown parts can be completed.
- **`BciDeviceEvent`** gained `Step` and `WasPaired`. An edit's old and new values are written into the event's `Note`.
- **A set's `FrozenAt`** is a stored timestamp set at its first assignment and never cleared, so undoing that assignment doesn't
  thaw a set that history points at. A set that was assigned can still be copied by *New revision*; you type the new version
  (the name and version pair must be new).
- **Removing** an assembly or part from a set that is still editable is allowed (that is editing, not deleting the set).
- **Not built:** reordering assemblies and parts (they keep the order they were added in), the API (decision in section 9), the
  optional unit-level note (section 8).
