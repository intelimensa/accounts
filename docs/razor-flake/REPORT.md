# Flaky Razor compile in `accounts` — investigation notes

Written 2026-10-18 (late evening). Status: **seen five times, not reproduced on purpose, cause unknown (leading suspect: VS Code's C# extensions building the same project in the background), a workaround is known.**
Nothing here is committed; `docs/razor-flake/` is yours to keep, trim or delete.

## In one paragraph

Now and then `dotnet build` / `dotnet test` of the accounts project fails with dozens of compile errors **in Razor pages whose
source is fine** — errors that change from run to run, name identifiers that are truncated fragments of the page text (`tial`, `sp`,
`on`), or call valid markup invalid. The same source builds cleanly minutes later. `dotnet build-server shutdown` fixed it both
times it was tried. In 42 controlled builds afterwards, with nobody else touching the repos, it never happened again, so I could not
pin the cause. VS Code (C# Dev Kit and C# extensions) was open on the workspace root during the failures and, as far as I can tell, closed during
the clean runs, so background editor builds of the same project are the leading suspect. This note records what was seen, what
was ruled out, and what to capture the next time it appears.

## Environment

| | |
|---|---|
| Machine | Mac, Apple silicon (`osx-arm64`), macOS 27.0.1 |
| SDK | .NET SDK 10.0.401 (MSBuild 18.9.11), target `net10.0` |
| Project | `Intelimensa.Accounts` — stock `Microsoft.NET.Sdk.Web`; no `global.json`, no `Directory.Build.*`, no `.editorconfig`; 25 `.cshtml` files, `Pages/_ViewImports.cshtml` has `@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers` |
| Razor | the in-box source generator `Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator` (`Microsoft.CodeAnalysis.Razor.Compiler.dll`, SDK files dated 24 Aug); generated files land in `obj/Debug/net10.0/Microsoft.CodeAnalysis.Razor.Compiler/…` |
| Where it showed | always when building the **test project** (`Intelimensa.Accounts.Tests`), which builds the web project as a project reference; it was seen via `dotnet test` and, once, inside `check-contracts.sh` |
| Compiler server | the shared `VBCSCompiler` was running; at one point it had been up 22 minutes; builds of Quarry (`firmware/quarry`) and AxoSync were run in the same period, and the server is shared per user, so it most likely served them too |

## What the machine's owner could say about it

Asked what else was running during the failures (2026-10-18): *"I do not know, I haven't tracked that. I have the C# extension, and
VS Code was on in the background, but it was open at the workspace root (`~/code/intelimensa`). It could probably have been that."*

- Installed: **C# Dev Kit 3.40.210** and **C# 2.160.4** (`ms-dotnettools.csdevkit`, `ms-dotnettools.csharp`) in VS Code.
- VS Code was open on the **workspace root**, not on one repo. There is no `.vscode`, solution or project at the root, so the extensions
  find every `.csproj`/`.slnx` below it: `accounts`, `axosync` and `firmware/quarry`. They load them, and re-run design-time builds
  and Razor generation in the background when a project file or a source file changes.
- When I looked (right after the 42 clean builds), **no VS Code or language-server process was running.** I did not check during
  them, but it fits: the clean runs most likely had VS Code closed, the failing runs had it open. That is correlation, not proof.
- The failing runs came right after I had edited or restored files in `accounts` and its test project (the mutation tests edit a
  contracts file and `git checkout` it back; the earlier steps edited `Intelimensa.Accounts.Tests.csproj`), which is exactly what makes
  the extension start a background build of the same project.

## What was seen

All times are from the evening of 2026-10-17/18; the failing builds were run by me (Claude) while working in the workspace.

| # | When | What | Result |
|---|---|---|---|
| 1 | in `check-contracts.sh`, the accounts test step | first failure, see "Run 1" below | FAIL |
| 2 | straight after, `dotnet test` of accounts alone, nothing changed | different errors, see "Run 2" | FAIL |
| 3 | after `dotnet build-server shutdown` | same sources | **pass** (130 tests) |
| 4 | later, in a mutation test (one record field renamed in `Api/Auth/AuthContracts.cs`) | Razor errors in pages that have nothing to do with it, see "Run 4" | FAIL |
| 5 | after `dotnet build-server shutdown`, same mutation | the expected test failures only, no Razor errors | pass |

`check-contracts.sh` now retries a failed `dotnet test` once after `dotnet build-server shutdown`, which hides the flake there.

### Run 1 (representative; the real output had about 25 error lines)

```
Pages/Staff/Devices.cshtml(104,35): warning CS0168: The variable 'device' is declared but never used
…/Pages/Staff/Configs_cshtml.g.cs(113,143): error CS0832: An expression tree may not contain an assignment operator   (×6, also in Login_cshtml.g.cs)
Pages/Manufacturing/Index.cshtml(99,42):  error CS0103: The name 'model' does not exist in the current context
Pages/Manufacturing/Index.cshtml(178,25): error CS0103: The name 'tial' does not exist in the current context
Pages/Manufacturing/Index.cshtml(178,30): error CS0103: The name 'name' does not exist in the current context
Pages/Manufacturing/Index.cshtml(210,22): error CS0103: The name 'partial' does not exist in the current context
Pages/Account/Study.cshtml(46,74):        error CS1061: 'StudyModel' does not contain a definition for 'sp' …
Pages/Account/Study.cshtml(47,58):        error CS1061: 'StudyModel' does not contain a definition for 'sp' …
Pages/Account/Study.cshtml(50,43):        error CS1061: 'StudyModel' does not contain a definition for 'on' …
```

### Run 2 (nothing had changed since run 1)

```
Pages/Staff/Devices.cshtml(104,28): error RZ1021: Markup in a code block must start with a tag and all start tags must be matched with end tags. …   (also (108,28))
…/Pages/Account/Login_cshtml.g.cs(116,163): error CS1001: Identifier expected   (also (141,151))
Pages/Account/Login.cshtml(18,45): error CS1010: Newline in constant
```

### Run 4

```
…/Pages/Account/Study_cshtml.g.cs(149,151): error CS1001: Identifier expected
Pages/Manufacturing/Index.cshtml(99,42): error CS1002: ; expected
Pages/Manufacturing/Index.cshtml(99,48): error CS1010: Newline in constant
```

## What the signature says

1. **The reported lines are fine.** They are the lines that use a tag helper or a pseudo-tag, and nothing else:

   | cited | source line |
   |---|---|
   | `Manufacturing/Index.cshtml:99, 178, 210` | `<partial name="_BuildSelect" model='new BuildSelectModel("r-fw", "Firmware build", Model.Builds)' />` |
   | `Account/Study.cshtml:46`, `:50` | `<input … asp-for="Input.ConsentAcknowledged" />`, `<span asp-validation-for=… >` |
   | `Account/Login.cshtml:18` | `<div asp-validation-summary="ModelOnly" …></div>` |
   | `Staff/Devices.cshtml:104`, `:108` | `<text> &middot; Bootloader @device.BootloaderVersion</text>`, `<text> (updated @updatedAt.ToString("yyyy-MM-dd"))</text>` |

   `Manufacturing/Index.cshtml` had not changed since commit `a315d13` (file dated 6 Oct), so a half-saved edit of *that* file cannot be the
   whole story.
2. **The identifiers are slices of the page text** (`tial` ← `partial`, `sp` ← `asp-for`, `on` ← a longer attribute), as if the generator
   cut text at offsets that belong to a different version of the file, or a different parse of it.
3. **The first symptom of run 1 was a *warning*:** "`device` is declared but never used" at `Devices.cshtml(104,35)`, whose very
   line uses `@device.BootloaderVersion`. So in that run `@expr` inside `<text>` was not parsed as an expression: the Razor *parser*
   behaved differently, not only tag-helper discovery.
4. Errors **move between runs** (run 1 vs 2) although the sources did not change, and one rebuild did not clear it.
5. The generator is configured from `obj/Debug/net10.0/Intelimensa.Accounts.GeneratedMSBuildEditorConfig.editorconfig`: a global
   `build_property.RazorLangVersion = 10.0` plus one section per page (`build_metadata.AdditionalFiles.TargetPath = <base64>`). That file
   is rewritten whenever the set of pages changes (a page was added or removed).

## What was ruled out (42 builds, repos otherwise idle)

Scripts and raw results are in this folder: `experiment-1-sequences.sh` / `results-1.txt`, `experiment-2-concurrency.sh` / `results-2.txt`.

| Condition tried | Builds | Failures |
|---|---|---|
| cold compiler server, then warm rebuilds of accounts (mtime touched each time) | 7 | 0 |
| warm server, with Quarry and AxoSync built in between (the sequence before run 1) | 4 | 0 |
| a source file's content changed, then restored | 2 | 0 |
| a `.cshtml` touched, no content change | 1 | 0 |
| rebuild with nothing changed; rebuild after `build-server shutdown` | 2 | 0 |
| `-p:UseSharedCompilation=false`, interleaved with the other repos | 4 | 0 |
| shared server again, interleaved | 4 | 0 |
| **two builds of the same project at the same instant** (tests project and web project) | 12 | 0 |
| **`Login.cshtml` rewritten in place, truncate-then-write, every 10 ms, during the build** | 5 | 0 |
| fresh server, final rebuild | 1 | 0 |
| **wrong generator option:** `-p:RazorLangVersion=1.0` / `2.0` | 2 | fail, but with **different errors** (`CS0246` type not found), so a wrong language version is *not* this flake. `3.0` built clean. |

So the three most obvious suspects each fail as a *sufficient* cause on their own here: a stale shared compiler server, two command-line
builds overlapping, a page being rewritten while the build reads it. What the failing runs had that these did not is most likely
the editor: see "What the machine's owner could say" and H2.

## Hypotheses (H2 is the one to test first)

| # | Hypothesis | For | Against / untested |
|---|---|---|---|
| H1 | **Stale state in the long-lived `VBCSCompiler`** (it caches loaded analyzers/generators and file contents across builds) | cleared by `build-server shutdown` both times; the server had been up 22+ minutes and had very likely served builds of other repos | not reproduced with interleaved builds or after 24 warm builds; maybe needs a longer uptime or memory pressure: **soak it** (below) |
| H2 | **VS Code's C# Dev Kit / C# extension building the same project in the background** (design-time builds and its own Razor generator run, writing the same `obj/` files my command-line build reads and writes) — **now the leading suspect** | both extensions installed; VS Code open on the workspace root so all three .NET repos are loaded; failures followed file/csproj edits that trigger background builds; the 42 clean builds most likely ran with VS Code closed; text cut at wrong offsets looks like generator input read at two different moments | VS Code was not running when I could test, so the real extension has **not** been tried; synthetic overlap (two command-line builds; in-place rewrites of one page) did not trigger it. A page list that changes (new pages such as `ConfirmEmail.cshtml`, `Admin/Roles.cshtml`, which rewrite the generated editorconfig) is a variant: only you know when that work started |
| H3 | **A bug in the Razor source generator's incremental cache** (10.0.401) — a cached parse reused with a changed text | the "positions don't match the text" signature | no matching upstream issue found (links below); needs the binary log from a failing run |
| H4 | Wrong generator options (e.g. a half-written `GeneratedMSBuildEditorConfig`) | the file configures the generator and is rewritten when pages change | wrong `RazorLangVersion` gives different errors; a truncated file was not tried |

## What to capture the next time it happens, before fixing it

Do **not** run `dotnet build-server shutdown` first: it destroys the evidence.

```bash
cd accounts
ps -axo pid,etime,command | grep -E 'VBCSCompiler|dotnet|Razor|MSBuild|LanguageServer|csdevkit|Code Helper' | grep -v grep   # who else is building (is VS Code up?), how old is the server
git status --short; ls -lt Intelimensa.Accounts/Pages/*/*.cshtml Intelimensa.Accounts/Pages/*.cshtml | head -5   # was anything edited in the last minutes?
cp -R Intelimensa.Accounts/obj /tmp/obj-at-failure                      # the generated *_cshtml.g.cs files show what the generator emitted
dotnet build Intelimensa.Accounts.Tests/Intelimensa.Accounts.Tests.csproj -bl:/tmp/flake.binlog   # same server, binary log (open with MSBuild Structured Log Viewer)
dotnet build Intelimensa.Accounts.Tests/Intelimensa.Accounts.Tests.csproj -p:UseSharedCompilation=false   # passes? then it is the server (H1)
dotnet build-server shutdown && dotnet build …                          # passes? note it
```

Then compare the `*_cshtml.g.cs` for one failing page with a good build's: if the C# is cut at the wrong offsets, it is a source-text
mismatch (H2/H3); if it is structurally different (a tag helper rendered as plain C#), it is discovery or configuration.

## Ideas to try tomorrow, cheap first

1. **The decisive test: do it with VS Code open.** Open VS Code at `~/code/intelimensa` with both extensions on, wait until the projects have loaded (the C# Dev Kit status finishes), then run `experiment-1-sequences.sh`, and also this loop, which mimics what happened before the failures (edit a project file, build at once):
   ```bash
   cd accounts
   for i in 1 2 3 4 5 6; do echo "<!-- $i -->" >> Intelimensa.Accounts.Tests/Intelimensa.Accounts.Tests.csproj; dotnet build Intelimensa.Accounts.Tests -v q --nologo 2>&1 | grep -c ' error '; done
   git checkout Intelimensa.Accounts.Tests/Intelimensa.Accounts.Tests.csproj   # careful: restores the committed file
   ```
   Repeat with VS Code **closed** as the control. If the failures only show with it open, that is the answer.
2. **Isolate scripted builds from the editor** (untested): .NET 8+ can redirect `bin/` and `obj/` for a command-line build with `--artifacts-path <dir>` (or `-p:ArtifactsPath=<dir>`), so `dotnet test` stops sharing `obj/` with the extension. If it works with the test project it could go into `check-contracts.sh`, and it would remove this whole class of races.
3. **Soak the server:** leave a warm `VBCSCompiler` idle for 30+ minutes, then build in a loop with a `.cs` touch each time. Compare with a hand-run of Quarry/AxoSync builds between.
4. **Page list changing mid-build:** run the build in a loop while another shell creates and deletes `Pages/ZzProbe.cshtml` (this rewrites the generated editorconfig).
5. **Rewrite the generated editorconfig non-atomically** (truncate it for a few ms) while `csc` starts — tests H4 directly.
6. **Watch the extension work:** with VS Code open, `ps -axo pid,etime,command | grep -E 'LanguageServer|csdevkit|MSBuild|VBCSCompiler'` right after saving a file or a project file shows the background builds.
7. If it keeps coming back and is not the editor: put `<UseSharedCompilation>false</UseSharedCompilation>` in the test project (the control runs cost about 1–2 s more per build), or run `dotnet build-server shutdown` in the test scripts; and search/open an issue at dotnet/razor or dotnet/roslyn with the binary log.

## Where this leaves the contract tests

`check-contracts.sh` retries once after a server shutdown, so a flake there costs a few seconds and shows as a pass. The accounts
tests themselves have no retry; if `dotnet test` in `accounts` shows garbage Razor errors, try `dotnet build-server shutdown` and run it again
before reading the errors.

## Upstream leads (none matches exactly)

A quick search found nothing about stale shared-compilation state. Loosely related reports of intermittent generator failures:
- [Intermittent Razor source generator failure (duplicate hintName, CS8785) in a Radzen Blazor thread](https://forum.radzen.com/t/several-errors-in-radzen-blazor-for-visual-studio/21642)
- [dotnet/aspnetcore#31152: generator fails (CS8785) on markup mixed into an attribute, .NET 6 preview](https://github.com/dotnet/aspnetcore/issues/31152)
- [dotnet/aspnetcore#36535: discussion of the Razor source generator and hot reload](https://github.com/dotnet/aspnetcore/issues/36535)

Search the dotnet/razor and dotnet/roslyn trackers directly for "RazorSourceGenerator" together with "VBCSCompiler" and "incremental".

## Files in this folder

- `REPORT.md` — this note
- `experiment-1-sequences.sh`, `results-1.txt` — warm/cold servers, interleaved builds, content changes, controls (24 builds)
- `experiment-2-concurrency.sh`, `results-2.txt` — simultaneous builds, in-place rewrites, final consistency check (18 builds)
- The `RazorLangVersion` runs were done by hand: `dotnet build Intelimensa.Accounts/Intelimensa.Accounts.csproj -p:RazorLangVersion=1.0` (then `2.0`, `3.0`)
