# Localization audit and visual evidence

Run from the repository root after a Release build:

```powershell
& build/audit-localization.ps1 -EvidenceDirectory artifacts/localization-audit
python build/localization/probe_exceptions.py --root . --output artifacts/localization-exception-probe
```

The scanner uses the installed SDK's Roslyn assemblies (no additional package), parses all `src` C# and XAML, inventories Chinese literals and comments, and rejects early static resource caches. `--compiled` loads the actual App/Core assemblies and checks resource values and composite formats. XML newline normalization is accounted for explicitly. `app-catalog.json` and `core-catalog.json` are the reviewed bilingual catalogs; resource keys, values, nonempty text and format parameters must match them.

`exceptions.json` records each non-UI occurrence's exact text, owner, syntax context, site fingerprint, reason and occurrence budget. Comments are documentation, never executable UI. Non-comment exceptions include diagnostic/log templates, prompt text, stable stored names and language data. A matching path/text alone cannot approve a new use. The mutation probe actually rescans owned copies of App.xaml.cs: baseline must pass; a new TextBlock using the existing log text and an extra copy of the original site must both fail. Nothing in `src` is mutated by that probe.

The only intentionally Chinese English resource is the native language-choice name `App:LanguageChineseNative`. Provider preset IDs, saved display names and prompt data are unchanged; the actual Add provider menu maps the stable preset key to a localized display label.

Migration.cs and the migration catalogs are preserved one-time implementation inputs, not a routine build step. Do not rerun `--migrate` over the finished localization.

For owned-window rendering, set `IRIS_T038_EVIDENCE` to a new directory and run the `LocalizationRenderingTests` class. The fixture uses synthetic data and bounded external seams, never real user input or clipboard. It captures actual menus and full scroll sequences; native popup dismissal is retried at most three times on the same menu. Notification text is captured from the actual service into an owned content host; this does not validate native balloon appearance.

`review_images.py` makes original-resolution contact sheets and carries prior visual review only when the PNG SHA matches. Review progress is manually recorded after viewing images. T-038's final report identifies the delivery image set and the exact review chain; directory names containing "accepted" refer only to developer visual review, not architect acceptance.
