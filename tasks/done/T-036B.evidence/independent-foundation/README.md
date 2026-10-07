

## 开发期独立复核补充：旧userinfo的空密码表示

仍属B已冻结的userinfo迁移/完整凭据对范围，不新增端口。A已验收MigrationChecks按首个字面冒号拆分；没有冒号时用户名为整段userinfo、密码为空字符串。HTTP/HTTPS允许显式空密码，SOCKS5仍受各1..255 UTF-8字节约束。

实际新Core/Infrastructure Release DLL独立检查：普通user:password迁移、错误地址绑定旧键安全失败、超int未来版本只读通过；`http://review-user@proxy.example:8080`加载失败且未stage。当前NetworkProxyCodec.ReadUserInfo对split<0直接抛错，与A上述规则不同。请保留该合法旧表示，迁移为用户名review-user及空密码；勿修改输入或偷偷回退System，仍保留原文件失败保护及无明文新备份。

独立复现C:/Users/28910/AppData/Local/Temp/iris-t036b-independent-foundation/Program.cs、migration-1.log，binding.json固定所测DLL。该日志是开发期检查，不是最终交付验收。请在对应真实迁移/codec测试中覆盖无冒号与显式尾冒号两种空密码表示；SOCKS5空密码仍应拒绝。

探针仅用实际JsonSettingsService和内存SecretStore、自有临时JSON，未调用HTTP或读取用户文件。第一次日志保留。


## 开发期独立复核补充：无效URI不能绕过userinfo保护

独立冻结的实际Core/Infrastructure DLL在migration-3.log复现：旧URL `http://review-user:review-password@bad host.invalid:8080/` 无法通过Uri.TryCreate，当前ImportProxyAsync因userInfo=false返回，Load成功；随后只将General.LogLevel改为Warning，真实WriteSafeSettingsAsync将含合成密码的原内容复制进新.bak。输出`MALFORMED_USERINFO newBackupContainsSyntheticPassword=True`。这是对已冻结“新配置/备份无凭据明文”的违反，不是允许的历史备份保留。

请修复解析失败路径：不能以Uri.TryCreate失败推断没有userinfo。对疑似authority凭据但不能安全解析/迁移的旧URL，应在发布Current或写任何新备份前安全失败、保留原文件；正常Update的输入校验也须防止同类原始userinfo被重新写入。不要把所有invalid Custom转System或直连；不含凭据的invalid Custom仍按原规范保留意图并阻止发送。日志/异常不回显值。

同一独立程序已同时覆盖用户名-only空密码边界、普通有效userinfo迁移、旧键地址不匹配保护及超intfuture只读。目录C:/Users/28910/AppData/Local/Temp/iris-t036b-independent-foundation，Program.cs、migration-3.log、binding.json。migration-2仅为独立项目引用副本丢失的编译记录，改用未变更的六个依赖程序集后复现，所测Core/Infrastructure冻结DLL未替换。请使用真实JsonSettingsService增加畸形authority及直接Update反例，不以假存储通过代替。
