# 真实数据切片测试

本地测试直接使用真实数据，不匿名化，也不限制为画像。原始切片不入库，保存在 `F:/code/Resource-Manager-Development/real-slices/<UTC stamp>/slice.json`。对外发布的图片使用现有 `readmeShowcase` 虚构场景。本轮不连接安装版；首次录制由 Claude 执行。

录制器从生产 data 层及 FrontendSources 引用的 API 模块发现显式 GET 描述和订阅 URL 构造器。普通指标由生产默认看板槽位选择并排除设备字段，资源表只选默认可见列的事实和身份池，资源条按默认项目/来源/缩放；字段使用默认槽位及CPU、设备详情页的生产字段集合，GPU专用计数器使用生产构造器。它不再把目录中的全部指标ID或可选来源拼成请求。目录帧通过生产decoder解码，字段与指标使用同帧catalogRevision的内容。发送前严格匹配方法、路径和订阅正文，唯一允许的 POST 是统一订阅通道，拒绝重定向和控制、设置、草稿写入。BackendSessionOwner 的 epoch 是取消旧请求的会话标记，不是 HTTP 凭据；独立 Node 工具需要维护者明确提供授权 base URL 和可信访问凭据输入，不能自行读取安装配置。

切片 schemaVersion 1 保存 durationMs、events 和可选 readiness。录制先等待字段目录帧到达、device-topology 退出 warming（默认上限120秒，可用 --readiness-timeout-seconds 改为不超过600秒），之后才开始 --seconds 指定的完整时长。readiness 保存实际等待耗时和准备期原始事件；定时区间的 atMs 从0开始，以最后准备期值建立0时刻基线。事件类型包括 response/frame/end，结束事件保留状态、原始拒绝正文和结束原因；响应另保存 contentType。正常EOF/传输错误按生产1秒起步、指数退避到30秒、有帧即重置为1秒的规则重连。所有4xx（包括统一通道内的selector拒绝）保存end并失败，不重试。回放不改 payload，浏览器继续使用生产解码器。匹配覆盖到的 GET 与订阅优先使用切片，未覆盖的仍使用旅程夹具。全场景共用回放时钟，新连接先收当前帧，再接收后续原始时间点的帧；结束时保持最后一帧，不自行循环。scenario.slice 或 RM_REAL_SLICE 启用，缺省关闭；全局环境变量在场景重置后仍有效。

画像 schemaVersion 1 记录观察到的规模、九语名称长度、设备与 GPU/引擎数量、值分布和更新间隔；bootstrap 明确标记假设与仓库事实来源。learner 用一个或多个切片替换 bootstrap。生成器由画像与 seed 确定结果，提供 realistic、realistic-extreme、processes-1000、longest-names、gpus-4 及批量 seed。生成器扩充现有 mock 的场景和输出，不启动第二套服务器。

新旅程 realistic-data-sweep 遍历 monitor、details、control、software、disk、settings，以 realistic 默认种子和三个 seed 检查页面错误、数据对应的空值/占位符、文字越界与主页面切换 50ms 预算。每个发现包含页面、场景、seed 和 DOM/长任务证据；只报告产品问题。旧旅程不改场景；设置 RM_REAL_SLICE 即可检验其原有断言是否适用于真实数据。

资源表滚入视口后才产生可见需求，首个事实帧按场景节奏到达。sweep 以首个软件行出现为就绪条件（8 秒有界等待），不能把滚动后固定 250ms 的加载状态当作产品卡死。主页面切换的长任务观察持续到行池出现并完成绘制；之后另行滚遍虚拟表，按行索引核对所有软件行的 CPU/内存值和名称绘制边界。证据同时记录 250ms 时的行数、实际订阅选择器、mock 发布时刻、行池大小和逐行检查结果。长名称统计以至少 24 个 Unicode 码点为准，合法省略号不算越界。

实现进度和命令、验证结果见工作树 .local/reports/final-report.md。尚无首个真实切片时不得把生成数据验收称为真实切片验收。

## 入口

从 `src/UI/Frontend` 运行（录制前必须由维护者提供授权 base URL 与密钥环境变量）：

```powershell
node --experimental-transform-types --conditions=browser tests/realData/recordBackendSlice.mjs --plan
node --experimental-transform-types --conditions=browser tests/realData/recordBackendSlice.mjs --base-url $env:RM_CAPTURE_BASE_URL --seconds 90 --readiness-timeout-seconds 180 --output $sliceFile --key-env RM_CAPTURE_KEY
node tests/realData/learnProfile.mjs --out tests/fixtures/realDataProfile.json $sliceDir
node tests/realData/generateScenario.mjs --count 20 --seed-start 1 --mode realistic --out $batchDir
npm run test:real-data
```

`$sliceFile` 是上述原始数据根内新 UTC 目录的 `slice.json`；`$sliceDir` 是其目录；`$batchDir` 是已登记的验证任务内输出目录。learner 原子替换输出画像；生成器输出 JSON 可直接 POST 给 mock。

mock 的 `/__test/scenario` 接受 `{"preset":"realistic","seed":17}`、`{"preset":{"mode":"realistic-extreme","multiplier":3},"seed":17}` 或 `{"slice":"F:/code/Resource-Manager-Development/real-slices/<stamp>"}`。设置 `RM_REAL_SLICE` 后使用原浏览器门入口运行任何旅程；未给路径时不开启真实数据。

录制计划静态扫描生产 `.ts/.tsx` 的 GET 描述和 JSON 读取调用，订阅构造器来自 FrontendSources 可达的数据/API 模块。带身份参数的动态详情 GET 和原始二进制磁盘树不猜参数，也不发起扫描写操作；回放这些未覆盖部分时继续用旅程夹具。

