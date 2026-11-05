# 最終0.2.8：限流發現及原生更新目錄占用修正

最终正常审阅归档为 `422525535cdc46fcc10a6533d0f88e2cac2a89e8`；[最终只读执行准备](F:/Iris/tasks/T-039C.coordination/EXECUTION_READY_V028_FINAL_20261105.md)已通过，当前仍未获得028发布授权。

正式0.2.7、原CI和所有原版本保持。首輪真實0.2.2→0.2.7界面升級在原App正常退出後，Update.exe備份current目录報Windows共享衝突32、退出1；原事務、全部日志與進程順序已冻结。用户已用正式0.2.7手工修復，實際97項安裝字節與三項原數據哈希匹配，当前0.2.7/PID8820、開始UTC09:31:12.4245570Z；這次恢復建立後續基線，首輪UI升級仍記失敗。

| 項目 | 最終核對 |
|---|---|
| 發布源碼 | `0c9b0a1881d07224597163b1e9b2f080ab60d434` |
| 規範來源樹 | `6928cec3575944b9154abbc129ac848ab943b872` |
| 發現修正 | 官方latest feed優先，僅明確404保留兼容發布回退，full下載固定版本tag |
| 原生修正 | handoff及outside copy明確使用自有外部工作目录；已驗證的maintenance handoff在原生操作前移開繼承的current工作目录 |
| 實證 | 原啟動邊界用例1/1失败、修後7/7成功；同一Sandbox兩個自有輔助進程實際重現0x80070020，外部工作目录下helper仍運行時rename成功 |
| Release | 0警告、0錯誤 |
| 全量 | 3771總計 / 3769成功 / 0失敗 / 2既有error183環境跳過 |
| 唯一新候選 | `F:/Iris/artifacts/t039c/release-0.2.8-public-base-candidate-2`；首版候選1與源碼2b2bbe7保持 |
| 來源/資產/載荷 | 721來源blob/工作字節、19資產、97實際full載荷匹配，Git tree獨立計算與來源/外層/版本/feed核對通過 |
| 本地Setup SHA256 | `a595455e28ebe036fb151110d73a1dc1def4e3c6d72a880467b349d77688de34` |
| 本地full SHA256 | `50a0723141c32034f9137bd0fb4d2832706c0a53c7a20f83ff28401fb56d5665` |
| 正式前版 | 0.2.7 full 64627391字節 / SHA256 `f8f9de897b637f8c98a07d601420885bf65529701d014fc52b59f87ce75bace8`；原0.2.2 history也保持 |

兩项修正没有改變目的倉庫、公共契約/schema、依賴或用户保存資料。最终源碼正常本地提交，Release/full與候選2來自该固定source；本地候選SHA不替代後續正式CI的實際SHA。[最終獨立候選核對](F:/Iris/tasks/done/T-039C.evidence/next-source-v028-native-fixed-20261105-2/review.json)、[首輪真實失敗](F:/Iris/tasks/done/T-039C.evidence/ui-native-update-failure-v027-20261105-1/binding.json)、[手工修復](F:/Iris/tasks/done/T-039C.evidence/manual-repair-v027-20261105-1/binding.json)及[新0.2.7基線](F:/Iris/tasks/done/T-039C.evidence/manual-old-ready-v027-before-final028-20261105-1/binding.json)已準備歸檔。

## 待確認的唯一發布與後續手工路徑

完整審閱歸檔、Git原始字節和最終只讀預檢完成後，僅請求用户確認創建/推送annotated `v0.2.8`→上述最終source，由現有Actions發布修正版本；main/codex不推送，已发布027及原失败版本完整保留。此前028 source2b2bbe7/candidate1的材料已被新原生實證取代；没有获得028發布授权。

由於目前安装的027仍帶有旧维护启动实现，在正式028资产核对完成後，先由用户手工安装最终修正028并沿用同一Sandbox/资料，建立后续可验证基线。随后再准备下一递增正式版本的可审发布材料，单独请求该版授权；用户再执行真正的設定檢查→下載Ready→確認安装→native退出與一次正常重启→旧资料與原服務新翻譯→卸载默认保留资料驗收。

首輪失败与手工修复不被改记为成功UI升级。C/父T039與T040/NFR、首次.NET安装性能與M6延期状态保持，最终028尚未發布。
