# 真实数据切片测试

本地测试直接使用真实数据，不匿名化，也不限制为画像。原始切片不入库，保存在仓库外由 `RM_REAL_SLICE_ROOT` 指定的目录：`<RM_REAL_SLICE_ROOT>/<UTC stamp>/slice.json`；该变量没有默认值，未设置时录制和读取切片都会拒绝。对外发布的图片使用现有 `readmeShowcase` 虚构场景。录制由 Claude 在授权后端会话中执行；工具线程不连接安装版。

录制器从生产 data 层及 FrontendSources 引用的 API 模块发现显式 GET 描述和订阅 URL 构造器。普通指标由生产默认看板槽位选择并排除设备字段，资源表只选默认可见列的事实和身份池，资源条按默认项目/来源/缩放；字段使用默认槽位及CPU、设备详情页的生产字段集合，GPU专用计数器使用生产构造器。它不再把目录中的全部指标ID或可选来源拼成请求。目录帧通过生产decoder解码，字段与指标使用同帧catalogRevision的内容。发送前严格匹配方法、路径和订阅正文，唯一允许的 POST 是统一订阅通道，拒绝重定向和控制、设置、草稿写入。BackendSessionOwner 的 epoch 是取消旧请求的会话标记，不是 HTTP 凭据；独立 Node 工具需要维护者明确提供授权 base URL 和可信访问凭据输入，不能自行读取安装配置。

切片 schemaVersion 1 保存 durationMs、events 和可选 readiness。录制先等待字段目录帧到达、device-topology 退出 warming（默认上限120秒，可用 --readiness-timeout-seconds 改为不超过600秒），之后才开始 --seconds 指定的完整时长。readiness 保存实际等待耗时和准备期原始事件；定时区间的 atMs 从0开始，以最后准备期值建立0时刻基线。事件类型包括 response/frame/end，结束事件保留状态、原始拒绝正文和结束原因；响应另保存 contentType。正常EOF/传输错误按生产1秒起步、指数退避到30秒、有帧即重置为1秒的规则重连。所有4xx（包括统一通道内的selector拒绝）保存end并失败，不重试。回放不改 payload，浏览器继续使用生产解码器。只有 productionReadPlan.categories.observation 中的 GET 与订阅可被切片覆盖；分类按生产源码 owner 推导，不另维护 HTTP 端点名单。观测类包括指标/字段/目录、资源事实和资源条、CPU拓扑/驻留、GPU观测、设备拓扑、本机状态；该owner内的override/score/binding/settings/control描述仍归旅程。设置、控制、管理、operations、updates、disk-usage、recordings、trusted access、audit及其他未覆盖内容均保留旅程夹具，即使原片记录了它们。全场景共用回放时钟，新连接先收当前帧，再接收后续原始时间点的帧；结束时保持最后一帧，不自行循环。scenario.slice 或 RM_REAL_SLICE 启用，缺省关闭；全局环境变量在场景重置后仍有效。

画像 schemaVersion 1 记录观察到的规模、九语名称长度、设备与 GPU/引擎数量、值分布和更新间隔；bootstrap 明确标记假设与仓库事实来源。learner 用一个或多个切片替换 bootstrap。生成器由画像与 seed 确定结果，提供 realistic、realistic-extreme、processes-1000、longest-names、gpus-4 及批量 seed。生成器扩充现有 mock 的场景和输出，不启动第二套服务器。

新旅程 realistic-data-sweep 遍历 monitor、details、control、software、disk、settings，以 realistic 默认种子和三个 seed 检查页面错误、数据对应的空值/占位符、文字越界与主页面切换 50ms 预算。每个发现包含页面、场景、seed 和 DOM/长任务证据；真实全门另行区分夹具前提、基础设施错误与产品问题。旧旅程不改场景；设置 RM_REAL_SLICE 即可检验其原有断言是否适用于真实数据。

资源表滚入视口后才产生可见需求，首个事实帧按场景节奏到达。sweep 以首个软件行出现为就绪条件（8 秒有界等待），不能把滚动后固定 250ms 的加载状态当作产品卡死。主页面切换的长任务观察持续到行池出现并完成绘制；之后另行滚遍虚拟表，按行索引核对所有软件行的 CPU/内存值和名称绘制边界。证据同时记录 250ms 时的行数、实际订阅选择器、mock 发布时刻、行池大小和逐行检查结果。长名称统计以至少 24 个 Unicode 码点为准，合法省略号不算越界。

实现进度和命令、验证结果见工作树 .local/reports/final-report.md。尚无首个真实切片时不得把生成数据验收称为真实切片验收。

## 入口

从 `src/UI/Frontend` 运行（录制前必须由维护者提供授权 base URL 与密钥环境变量）：

```powershell
node --experimental-transform-types --conditions=browser tests/realData/recordBackendSlice.ts --plan
node --experimental-transform-types --conditions=browser tests/realData/recordBackendSlice.ts --base-url $env:RM_CAPTURE_BASE_URL --seconds 90 --readiness-timeout-seconds 180 --output $sliceFile --key-env RM_CAPTURE_KEY
node tests/realData/learnProfile.ts --out tests/fixtures/realDataProfile.json $sliceDir
node tests/realData/generateScenario.ts --count 20 --seed-start 1 --mode realistic --out $batchDir
npm run test:real-data
```

`$sliceFile` 是上述原始数据根内新 UTC 目录的 `slice.json`；`$sliceDir` 是其目录；`$batchDir` 是已登记的验证任务内输出目录。learner 原子替换输出画像；生成器输出 JSON 可直接 POST 给 mock。

mock 的 `/__test/scenario` 接受 `{"preset":"realistic","seed":17}`、`{"preset":{"mode":"realistic-extreme","multiplier":3},"seed":17}` 或 `{"slice":"<RM_REAL_SLICE_ROOT>/<stamp>"}`。设置 `RM_REAL_SLICE` 后使用原浏览器门入口运行任何旅程；未给路径时不开启真实数据。

录制计划静态扫描生产 `.ts/.tsx` 的 GET 描述和 JSON 读取调用，订阅构造器来自 FrontendSources 可达的数据/API 模块。带身份参数的动态详情 GET 和原始二进制磁盘树不猜参数，也不发起扫描写操作；回放这些未覆盖部分时继续用旅程夹具。


## 旅程接管与真实全门分类

`manualPublishPaths`、`pausedSubscriptionPaths`、`/__test/subscriptions/publish` 和 `/__test/metrics/publish` 表示旅程接管该流。mock保留同一个连接上的夹具publisher，接管后屏蔽对应回放回调，直到场景reset；不会因真实回放占位而让手动发布找不到subscriber。取消暂停可恢复夹具自动推送；不会重新让切片抢回控制。`readmeShowcase`继续完全使用虚构数据。

场景仅改变观测字段的名字/行数/数值而没有安装帧控制器时，真实观测仍优先；原断言若要求某个虚构名字或精确值，属于夹具专用断言(A)，不能自动当作产品缺陷。真实全门需逐项区分A（夹具前提）、B（回放错误，应修复重跑）及C（有证据的产品问题）。给A旅程添加显式fixture字段/来源pin是后续方案，本轮不改旧断言，也不因某个scenario patch静默关闭整个真实数据层。

每个mock进程缓存一个按路径、长度和mtime绑定的观测数据集。场景reset创建新时钟/索引并清除接管标记，避免每条旅程重新解析数百MB原片；未将原片复制进仓库或提交learned硬件画像。
