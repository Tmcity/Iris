# T-038 independent language probe — prepared, not run

Uses actual resource getters, language selection, Core editor, temporary JsonSettingsService, SettingsWindow/VM/session, and GeneralSettingsSaveCoordinator. The boundary fixtures were copied from the previous independent network UI review; only temporary directory naming and the initial saved UI language were adjusted. No real user configuration, desktop input, proxy, or production pipe is used.

The six scenarios cover fixed language despite worker culture, a real language ComboBox and joint history save, fresh JSON loading followed by the production language selector, joint conflict preservation/reload, unrelated external-field merge, and unsupported language rejection. The fresh-load probe is not a claim that a real Iris executable was restarted, and its explicit per-test language scope does not validate the production Main cleanup-scope issue; that remains a separate required review.

Compile/run only when the developer's current build/test work is finished and the assemblies are stable. No ProjectReferences are used. Planned command: `dotnet run --project Review.csproj -c Release`. Preserve all errors and logs and bind the actual product assemblies used before making a runtime claim.
