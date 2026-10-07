# T-036B independent transport development probe

Uses actual Core SettingsEditCoordinator/snapshot source, actual Providers manager/handlers and both named resilience pipelines. Translation/OCR registries and settings/secrets are isolated boundaries; only SocketsHttpHandler.ConnectCallback is replaced by copied NetworkWire byte stream. No TCP/socket/user configuration. Scripted boundary came from the developer tests, but scenario setup/assertions are independent.

transport-1.log passed actual old-client A retry/B next request with held response Stop barrier, streaming SSE in both families, and delayed A after B publication. Production is still under development; rebind final submitted assemblies before acceptance. Foundation migration corrections separately passed all six original scenarios in iris-t036b-foundation-fixed/migration-1.log; old failures remain in iris-t036b-independent-foundation.
