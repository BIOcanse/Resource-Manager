# 本仓库的生成目录

非微小工作先加载 `coding-guidance` 和 `resource-manager-development`，并遵守用户已确认的项目合同。Git 与公开发行维护仍由已指定的维护会话负责。

## 交给 Claude 管理会话的事项

Codex 线程无法直接给 Claude Code 会话投递消息。需要 Claude 处理的事项（提交、推送、入库清单、请它核对的结果）写进 `docs/claude_inbox.md`：在文件末尾追加一节，标题写 UTC 时间、线程 ID 和事由，正文写清要做什么、相关文件路径和当前状态。Claude 会在后台监视这个文件，处理完后通过 CTA 回复对应线程。

## 任务与验证目录

权威规则：`docs/generated_workspace_retention.md`。

创建新的 `.local/<任务>` 目录必须通过 `scripts/maintenance/Manage-GeneratedWorkspace.ps1` 的 New 入口登记生命周期；使用其他工作目录的同类任务同样登记。不能用日期命名后直接 `New-Item` 绕过有界保留。重用正在进行的任务时读取已有状态，不将整个任务错误结案。过程结束、结果落盘并释放所有进程和句柄后调用 Complete；成功和失败均结案。收尾使用当前启用的有限保留政策，不能把私有 enabled:false 配置当作永久豁免。

引用另一个生成目录中的源码、工具、包或证据时，将真实路径写入 References，并将相关管理根传给 ReferenceRoots。后续工作仍需要的结果通过 Keep 或实际引用保留；不要为所有批次都设置 Keep。References 只列当前依赖，按最具体管理目录归并；文件内容绑定留在输入清单，不把每轮历史路径或每个源码文件无条件继承到下一轮。未知旧目录必须核对归属和完成证据后才能 Adopt。运行数据、用户数据、源码及发行包不登记为可回收任务。

验证批次通过 `scripts/validation/Manage-ValidationWorkset.ps1` 的 Prepare 创建，整个批次最终结束后调用 Complete。Prune 仍只是原有回执约束的缓存清理，不代表整个批次结束。 共享full-app工作集在产品门通过后使用 `scripts/validation/ProductSdk/Complete-Validation.ps1 -SharedWorkspace`；只关闭当前任务原Job映射证明拥有的子批，共享父和无关warm保持。不得复制历史关闭回收的收尾脚本。先登记本次真实接受输出的消费引用，再结案触发回收。

已闭合生成物默认回收，需要时重跑；保留唯一源码、真实输入、当前有效包和确实仍用的最小证据，不默认复制或归档整轮。相同的编译输出一轮只封存一份，操作前后仍做长度与哈希复核。

任何清理都使用维护入口；不得另写绕过引用、占用和路径检查的递归删除。默认最近3个已完成任务、每根8GiB；配置和明确保留可调整，保护项造成超预算必须报告。
