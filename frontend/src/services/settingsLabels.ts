import type { Component } from 'vue'
import {
  Camera, ChatDotSquare, Coin, Connection, DataLine, Document, EditPen, Key, Link, Lock, Microphone, Monitor, Notebook, Odometer,
  Operation, Reading, Upload
} from '@element-plus/icons-vue'
import type { SettingField, SettingValue } from './settingsCatalogApi'

/**
 * Everything the settings pages say in Chinese, keyed by configuration key. The server
 * answers with English contract text; a key missing here falls back to that, so a field
 * added on the server still shows before it is translated.
 */

export interface SettingGroupLabel {
  id: string
  path: string
  area: 'access' | 'data' | 'ai' | 'learning' | 'operations'
  label: string
  description: string
  icon: Component
  /** Marks a group whose values need a restart, so the page can say so up front. */
  caution?: string
}

export const settingAreas = {
  access: '访问与安全',
  data: '数据与存储',
  ai: 'AI 能力',
  learning: '学习功能',
  operations: '功能与运维'
} as const

const signInCaution = '这里的配置在启动时读取。保存后需要重启才生效；配置错误会导致无法登录，请先在新的身份提供方完成应用登记。'

export const settingGroups: SettingGroupLabel[] = [
  { id: 'identity', path: '/settings/identity', area: 'access', label: '登录与身份', icon: Key, description: '信任哪个身份提供方，以及哪些账户可以管理 Lexarbor。', caution: signInCaution },
  { id: 'rate-limits', path: '/settings/rate-limits', area: 'access', label: '请求限流', icon: Odometer, description: '按客户端地址限制管理员登录的请求频率。' },
  { id: 'network', path: '/settings/network', area: 'access', label: '网络与代理', icon: Connection, description: '部署在反向代理后面时，指定哪些上游可以传达真实客户端地址。' },
  { id: 'data-protection', path: '/settings/data-protection', area: 'access', label: '数据保护', icon: Lock, description: '保护管理员会话的根密钥，由部署管理。' },
  { id: 'database', path: '/settings/database', area: 'data', label: '数据库', icon: Coin, description: '连接的 PostgreSQL 数据库，以及启动时的初始化和慢查询记录。' },
  { id: 'audio-storage', path: '/settings/audio-storage', area: 'data', label: '音频存储', icon: Upload, description: '存放读音音频的 S3 存储桶、凭据和上传恢复参数。' },
  { id: 'review', path: '/settings/review', area: 'ai', label: '审核助手', icon: ChatDotSquare, description: '自动审核投稿的开关；审核建议使用「视觉推理」的模型连接。' },
  { id: 'text', path: '/settings/text', area: 'ai', label: '视觉推理', icon: Camera, description: '照片识别、审核建议、词条补全和发音规划共用的推理模型连接。' },
  { id: 'speech', path: '/settings/speech', area: 'ai', label: '语音生成', icon: Microphone, description: '语音服务，以及英式、美式各自的音色和风格。生成读音还需要「视觉推理」的模型连接和「音频存储」。' },
  { id: 'background', path: '/settings/background', area: 'ai', label: '后台策略', icon: Operation, description: '后台任务的发现、并发、失败额度和暂停退避。' },
  { id: 'learning', path: '/settings/learning', area: 'learning', label: '词汇学习', icon: Reading, description: '作答时限、间隔复习的节奏，以及易错词的判定。' },
  { id: 'learning-connection', path: '/settings/learning-connection', area: 'learning', label: '学习服务连接', icon: Link, description: '学习功能访问本站词汇目录、提交释义时使用的地址和密钥。' },
  { id: 'submissions', path: '/settings/submissions', area: 'operations', label: '投稿功能', icon: EditPen, description: '投稿通过的释义写入哪本词书。' },
  { id: 'logging', path: '/settings/logging', area: 'operations', label: '日志', icon: Document, description: '控制台日志的最低级别。' },
  { id: 'telemetry', path: '/settings/telemetry', area: 'operations', label: '遥测', icon: DataLine, description: '可选的 OpenTelemetry 链路追踪和指标导出。' },
  { id: 'service', path: '/settings/service', area: 'operations', label: '服务与运行信息', icon: Monitor, description: '本实例在日志中的名字，以及不可配置的运行信息。' }
]

/** The API keys are kept in the database rather than configured: a page beside the categories, outside the catalog. */
export const apiKeysGroup = { id: 'api-keys', path: '/settings/api-keys', label: '接入 API Key', icon: Notebook }

/** Where the work a category starts can be followed: the progress of the models' background tasks. */
export const progressLink = { to: '/enrichment', label: '查看自治进度、服务等待与异常恢复' }
export const progressGroups: readonly string[] = ['text', 'speech', 'background']

interface FieldLabel {
  label: string
  description?: string
  unit?: string
  /** Longer guidance shown under the control, as part of the field's own line. */
  multiline?: boolean
  placeholder?: string
}

export const settingLabels: Record<string, FieldLabel> = {
  // Sign-in and identity
  'IdentityService:Authority': { label: '身份提供方地址', description: '签发管理员令牌的身份提供方（例如 SignaCore）的根地址，不能带路径。', placeholder: 'https://signacore.example.com' },
  'IdentityService:Issuer': { label: '令牌签发者', description: '访问令牌的 iss 必须等于它，须与提供方 Discovery 文档里的 issuer 完全一致。' },
  'IdentityService:Audience': { label: '令牌受众', description: '访问令牌的 aud 必须等于它。' },
  'IdentityService:RequireHttpsMetadata': { label: '强制 HTTPS 获取签名元数据', description: '签名密钥决定谁能成为管理员，因此默认只在可信网络上获取。' },
  'AdminAuthentication:OidcCode:ClientId': { label: '登录应用 Client ID', description: '在身份提供方登记的机密（Confidential）应用 ID。' },
  'AdminAuthentication:OidcCode:ClientSecret': { label: '登录应用 Client Secret', description: '应用密钥，只在服务端使用，不会发往浏览器。' },
  'AdminAuthentication:OidcCode:RedirectUri': { label: '登录回调地址', description: '登记在提供方的完整外部地址，路径必须正好是 /admin/auth/callback。' },
  'AdminAuthentication:OidcCode:PostLogoutRedirectUri': { label: '登出返回地址', description: '可选。登记在提供方的登出跳转地址，路径必须正好是 /admin/auth/logout/return；留空则登出后不跳回。' },
  'AdminAuthentication:OidcCode:Scope': { label: '授权范围', description: '空格分隔：openid，可再加 profile。' },
  'AdminAuthentication:RequiredRole': { label: '管理员角色', description: '访问令牌必须携带的角色，具备它的账户才能管理 Lexarbor。' },
  'AdminAuthentication:Provider': { label: '登录提供方', description: '已移除的配置，只保留为启动检查。' },
  'AdminAuthentication:HttpTestOrigins': { label: 'HTTP 测试来源', description: '已移除的配置，只保留为启动检查。' },

  // Request rate limits
  'RateLimits:AdminLogin:Enabled': { label: '启用登录限流', description: '限制每个客户端地址发起登录的频率。' },
  'RateLimits:AdminLogin:PermitLimit': { label: '登录请求次数上限', unit: '次', description: '每个窗口内允许的登录请求数。' },
  'RateLimits:AdminLogin:WindowSeconds': { label: '登录限流窗口', unit: '秒' },

  // Network
  'Network:TrustedProxies': { label: '受信任的代理地址', description: '每项一个 IP，例如 172.18.0.2。只有这些地址发来的 X-Forwarded-For 会被采信。', placeholder: '输入地址后回车添加' },
  'Network:TrustedNetworks': { label: '受信任的代理网段', description: '每项一个 CIDR，例如 172.18.0.0/16。', placeholder: '输入网段后回车添加' },
  'Network:ForwardLimit': { label: '转发跳数上限', unit: '跳', description: '回溯的可信跳数，必须等于真实的代理层数；只有一层代理时填 1。' },

  // Data protection
  'DataProtection:RootKey': { label: '根密钥', description: '由部署环境注入的根密钥，页面不会显示它的值。' },
  'DataProtection:RootKeyFile': { label: '根密钥文件', description: '部署没有注入根密钥时，从这里读取；默认 data/data-protection-root-key。' },

  // Database
  'ConnectionStrings:Default': { label: 'PostgreSQL 连接串', description: '业务数据库的连接串，必填且没有默认值。页面只显示服务器和库名，不显示其中的凭据。' },
  'Database:InitializeOnStartup': { label: '启动时初始化数据库', description: '启动时创建缺失的数据库并执行待处理的迁移。关闭后迁移需要你自行执行，就绪检查会一直报告未就绪。' },
  'Database:LegacySqlitePath': { label: '旧版 SQLite 数据库路径', description: '启动时在这里检查是否还留有 PostgreSQL 之前的旧数据库；存在该文件会阻止启动。' },
  'Database:SlowCommandThresholdMilliseconds': { label: '慢查询阈值', unit: '毫秒', description: '超过该时长的数据库命令会记录一条警告；0 表示关闭。' },

  // Audio storage
  'Audio:Enabled': { label: '启用音频存储', description: '开启后后台上传与播放才可用。启用只代表本地配置完整，连通性和权限请用实际试听验证。' },
  'Audio:InternalEndpoint': { label: '服务访问地址', description: 'Lexarbor 连接 S3 时使用的地址，容器内通常是服务名。', placeholder: 'http://seaweedfs:8333/' },
  'Audio:PublicEndpoint': { label: '浏览器播放地址', description: '浏览器播放音频时访问的地址。', placeholder: 'https://audio.example.com/s3/' },
  'Audio:Region': { label: '区域' },
  'Audio:Bucket': { label: '存储桶', description: '应为私有桶。' },
  'Audio:KeyPrefix': { label: '对象前缀', description: '以 / 结尾，例如 audio/。' },
  'Audio:AccessKey': { label: 'Access Key', description: '对象存储的访问密钥 ID。' },
  'Audio:SecretKey': { label: 'Secret Key', description: '对象存储的私密访问密钥。' },
  'Audio:PlaybackTtlSeconds': { label: '播放链接有效期', unit: '秒', description: '签名播放链接的有效时长。' },
  'Audio:MaxUploadBytes': { label: '单个上传大小上限', unit: '字节', description: '单个音频文件的最大体积，5 MiB 即 5242880。' },
  'Audio:MaxConcurrentUploads': { label: '并发上传数', description: '同时执行的上传数。' },
  'Audio:MaxQueuedUploads': { label: '排队上传数', description: '等待执行的上传数，超出的请求会被拒绝。' },
  'Audio:OperationTimeoutSeconds': { label: '操作超时', unit: '秒', description: '单次存储操作的最长耗时。' },
  'Audio:RecoveryBatchSize': { label: '恢复批大小', description: '每轮恢复检查处理的中断操作数。' },
  'Audio:RecoveryIntervalSeconds': { label: '恢复间隔', unit: '秒', description: '两轮恢复检查之间的间隔。' },
  'Audio:RecoveryLeaseSeconds': { label: '恢复租约', unit: '秒', description: '单个操作的最长占用时间，超过后由恢复流程接管；必须大于操作超时。' },
  'Audio:RecoveryRetrySeconds': { label: '恢复重试间隔', unit: '秒', description: '失败的操作首次重试前的等待时间。' },
  'Audio:RecoveryMaxRetrySeconds': { label: '恢复最长重试间隔', unit: '秒', description: '不得小于恢复重试间隔。' },
  'Audio:RecoveryRecheckSeconds': { label: '恢复复查间隔', unit: '秒', description: '已完成的操作在多长时间内仍会被复查。' },

  // Review assistant
  'MeaningSubmissions:Ai:AutoReviewEnabled': { label: '启用 AI 自动审核投稿', description: '默认关闭。审核建议使用「视觉推理」里的模型连接。' },

  // The reasoning model
  'Models:Reasoning:Endpoint': { label: '推理服务地址', description: '填写地址、模型和密钥后，审核建议、词条补全、词汇扩展和发音规划随之可用，无需另外开启。照片识别和审核建议可以使用不要求密钥的地址。' },
  'Models:Reasoning:Model': { label: '推理模型', description: '需要支持图片输入和 JSON 输出。' },
  'Models:Reasoning:ApiKey': { label: '推理 API Key', description: '后台补齐和扩展必须提供密钥。' },
  'Models:Reasoning:TimeoutSeconds': { label: '推理请求超时', unit: '秒' },

  // Speech generation
  'Models:Speech:Endpoint': { label: '语音服务地址', description: '服务地址需包含 API 版本，例如 https://api.minimax.io/v1；服务器根据地址识别语音协议。' },
  'Models:Speech:Model': { label: '语音模型' },
  'Models:Speech:ApiKey': { label: '语音 API Key' },
  'Models:Speech:TimeoutSeconds': { label: '语音请求超时', unit: '秒' },
  'Models:Speech:Uk:Voice': { label: '英式音色', description: '填写提供方支持的音色 ID；留空则英式一侧等待，不影响美式。' },
  'Models:Speech:Uk:Style': { label: '英式风格描述', description: '只有支持指令的 OpenAI 模型会使用。', multiline: true },
  'Models:Speech:Uk:Emotion': { label: '英式情绪', description: '只有 MiniMax 使用。' },
  'Models:Speech:Uk:Pitch': { label: '英式音高', description: '只有 MiniMax 使用。' },
  'Models:Speech:Uk:Volume': { label: '英式音量', description: '只有 MiniMax 使用。' },
  'Models:Speech:Uk:Speed': { label: '英式语速', description: '留空则英式一侧等待。' },
  'Models:Speech:Us:Voice': { label: '美式音色', description: '填写提供方支持的音色 ID；留空则美式一侧等待，不影响英式。' },
  'Models:Speech:Us:Style': { label: '美式风格描述', description: '只有支持指令的 OpenAI 模型会使用。', multiline: true },
  'Models:Speech:Us:Emotion': { label: '美式情绪', description: '只有 MiniMax 使用。' },
  'Models:Speech:Us:Pitch': { label: '美式音高', description: '只有 MiniMax 使用。' },
  'Models:Speech:Us:Volume': { label: '美式音量', description: '只有 MiniMax 使用。' },
  'Models:Speech:Us:Speed': { label: '美式语速', description: '留空则美式一侧等待。' },

  // Background policies
  'Enrichment:Concurrency': { label: '任务执行并发', description: '目标执行与语音生成的并发上限。' },
  'Enrichment:TextConcurrency': { label: '文本请求并发', description: '词条补齐和词汇扩展的文本请求并发上限。' },
  'Enrichment:FailureBudget': { label: '每步失败额度', description: '含首次尝试；耗尽后该步骤停止，等待人工处理。' },
  'Enrichment:ServiceFailureThreshold': { label: '服务暂停阈值', description: '连续失败多少次后暂停整个服务。' },
  'Enrichment:ServicePauseSeconds': { label: '初始暂停时间', unit: '秒' },
  'Enrichment:MaxServicePauseSeconds': { label: '最长暂停时间', unit: '秒', description: '不得小于初始暂停时间。' },
  'Enrichment:ThrottleInitialSeconds': { label: '初始退避时间', unit: '秒', description: '被服务方限流后的首次等待。' },
  'Enrichment:ThrottleMaxSeconds': { label: '最长退避时间', unit: '秒', description: '不得小于初始退避时间。' },
  'Enrichment:DiscoveryBatchSize': { label: '发现批大小', description: '每轮发现的目标数上限。' },
  'Enrichment:ScanIntervalSeconds': { label: '扫描间隔', unit: '秒' },

  // Meaning submissions
  'MeaningSubmissions:BookId': { label: '投稿收录词书 ID', description: '审核通过的释义写入的词书；留空则关闭投稿功能。' },

  // Vocabulary learning
  'VocabularyLearning:SlowAnswerMs': { label: '慢答阈值', unit: '毫秒', description: '普通题的作答超过该时长，记为慢答。' },
  'VocabularyLearning:FillLettersSlowAnswerMs': { label: '填空题慢答阈值', unit: '毫秒', description: '填字母题的作答超过该时长，记为慢答。' },
  'VocabularyLearning:QuestionExpiryMinutes': { label: '题目有效期', unit: '分钟', description: '发出的题目在多长时间内可以作答。' },
  'VocabularyLearning:TimeZone': { label: '时区', description: '学习日按哪个时区划分。留空为固定的 UTC+08:00，不使用夏令时。', placeholder: 'Asia/Shanghai' },
  'VocabularyLearning:Fsrs:DesiredRetention': { label: '目标记忆保持率', description: '间隔复习想维持的记住概率，大于 0 且小于 1。' },
  'VocabularyLearning:Fsrs:FamiliarThresholdDays': { label: '熟悉门槛', unit: '天', description: '记忆稳定度达到该天数，进入「熟悉」阶段。' },
  'VocabularyLearning:Fsrs:MasteredThresholdDays': { label: '掌握门槛', unit: '天', description: '记忆稳定度达到该天数，进入「掌握」阶段；必须大于熟悉门槛。' },
  'VocabularyLearning:Fsrs:LearningStepsMinutes': { label: '追加的学习步骤', unit: '分钟', description: '新词学习时，排在内置的 1 分钟、10 分钟两个步骤之后的步骤，每项大于 0。', placeholder: '输入分钟后回车添加' },
  'VocabularyLearning:Fsrs:RelearningStepsMinutes': { label: '追加的重学步骤', unit: '分钟', description: '遗忘后重学时，排在内置的 10 分钟步骤之后的步骤，每项大于 0。', placeholder: '输入分钟后回车添加' },
  'VocabularyLearning:Fsrs:MaximumIntervalDays': { label: '最长复习间隔', unit: '天', description: '同一个词两次复习之间的最长间隔。' },
  'VocabularyLearning:ErrorProne:WindowDays': { label: '历史窗口', unit: '天', description: '用最近多少天的作答记录判断易错词。' },
  'VocabularyLearning:ErrorProne:MinimumAttempts': { label: '最少作答次数', unit: '次', description: '作答达到该次数后，错误率规则才对这个词生效。' },
  'VocabularyLearning:ErrorProne:ErrorRateThreshold': { label: '错误率阈值', description: '错误率达到该比例，即判为易错词。' },
  'VocabularyLearning:ErrorProne:MinimumForgetCount': { label: '遗忘次数阈值', unit: '次', description: '遗忘达到该次数，即判为易错词。' },

  // The learning service reaching this Lexarbor's catalog
  'Lexarbor:BaseUrl': { label: '词汇目录地址', description: '学习功能访问本站词汇目录接口的基础地址。', placeholder: 'https://lexarbor.example.com' },
  'Lexarbor:ApiKey': { label: '目录读取 Key', description: '读取词汇目录使用的 API Key；留空则不带密钥。' },
  'Lexarbor:AllowInsecureHttp': { label: '允许不安全的 HTTP', description: '允许把密钥发送到 HTTP 地址。密钥默认只发送到 HTTPS 地址。' },
  'Lexarbor:SupplementaryBookId': { label: '补充词书 ID', description: '被排除在常规选书之外的补充词书。' },
  'Lexarbor:Integration:ApiKey': { label: '释义提交 Key', description: '学习功能提交释义时单独使用的密钥；留空则关闭该客户端。' },

  // Logging
  'Logging:LogLevel:Default': { label: '默认日志级别', description: '没有单独规则的日志类别都使用它。' },
  'Logging:LogLevel:Microsoft.AspNetCore': { label: 'ASP.NET Core' },
  'Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command': { label: 'EF Core 数据库命令' },

  // Telemetry
  'Telemetry:Otlp:Traces:Enabled': { label: '导出链路追踪' },
  'Telemetry:Otlp:Traces:Endpoint': { label: '链路追踪导出地址', description: '留空则使用导出器的默认地址。' },
  'Telemetry:Otlp:Traces:Protocol': { label: '链路追踪协议' },
  'Telemetry:Otlp:Traces:AuthenticationHeaderName': { label: '链路追踪认证头', description: '携带授权值的请求头名称。' },
  'Telemetry:Otlp:Metrics:Enabled': { label: '导出指标' },
  'Telemetry:Otlp:Metrics:Endpoint': { label: '指标导出地址', description: '留空则使用导出器的默认地址。' },
  'Telemetry:Otlp:Metrics:Protocol': { label: '指标协议' },
  'Telemetry:Otlp:Metrics:AuthenticationHeaderName': { label: '指标认证头', description: '携带授权值的请求头名称。' },
  LEXARBOR_TELEMETRY_OTLP_AUTHORIZATION: { label: 'OTLP 授权值', description: '随上面配置的认证头一起发送。' },

  // Service identity
  'Service:InstanceId': { label: '实例标识', description: '出现在结构化日志里，用来区分实例。留空则每次启动随机生成。' }
}

export const sectionTitles: Record<string, string> = {
  'identity/provider': '身份提供方',
  'identity/hosted-login': '管理员登录应用',
  'identity/access': '访问规则',
  'rate-limits/admin-login': '管理员登录',
  'network/forwarding': '受信任的转发',
  'data-protection/root-key': '根密钥',
  'database/storage': '连接与启动',
  'audio-storage/connection': '连接',
  'audio-storage/credentials': '凭据',
  'audio-storage/advanced': '上传、播放与恢复',
  'review/review': '自动审核',
  'text/reasoning': '推理模型连接',
  'speech/tts': '语音服务',
  'speech/uk': '英式发音（UK）',
  'speech/us': '美式发音（US）',
  'background/policy': '发现、并发与失败处理',
  'learning/general': '作答与日程',
  'learning/fsrs': '间隔复习',
  'learning/error-prone': '易错词判定',
  'learning-connection/catalog': '词汇目录访问',
  'learning-connection/integration': '释义提交',
  'submissions/feature': '投稿功能',
  'logging/levels': '日志级别',
  'telemetry/traces': '链路追踪',
  'telemetry/metrics': '指标',
  'telemetry/authentication': '导出认证',
  'service/identity': '实例标识'
}

export const sectionNotes: Record<string, string> = {
  'identity/provider': '校验访问令牌时信任的签发方。',
  'identity/hosted-login': '管理员通过身份提供方的托管登录页进入，使用授权码流程。',
  'identity/access': '哪些账户可以管理 Lexarbor。',
  'rate-limits/admin-login': '统计 /admin/auth/start 的请求。',
  'network/forwarding': '直接对外暴露端口时保持为空。配置后若不填任何代理，服务会拒绝启动。',
  'data-protection/root-key': '根密钥用于加密管理员会话。丢失它只会让管理员需要重新登录，不影响词汇数据。',
  'database/storage': '连接串由部署提供，网页只读；其余三项在启动时读取。',
  'audio-storage/connection': '保存后立即生效。存储地址、桶和前缀属于存储目的地：已有音频或正在进行的操作时，服务器会拒绝修改。',
  'audio-storage/credentials': '已有密钥不会显示原值，只显示是否已设置。',
  'audio-storage/advanced': '这些参数只在启动时读取。',
  'review/review': '审核建议使用「视觉推理」的模型连接；这里的开关保存后立即生效。',
  'text/reasoning': '保存后立即生效。照片识别、审核建议、词条补全、词汇扩展和发音规划共用这一个连接。',
  'speech/uk': '留空则英式一侧等待，不影响美式。',
  'speech/us': '留空则美式一侧等待，不影响英式。',
  'learning/fsrs': '配置的步骤排在内置步骤之后，不能替换内置步骤。',
  'learning-connection/catalog': '密钥只会发送到 HTTPS 地址，除非允许不安全的 HTTP。',
  'learning-connection/integration': '留空则关闭学习功能的释义提交。',
  'submissions/feature': '投稿通过的释义写入这里指定的词书。',
  'logging/levels': '登录回调相关的日志类别始终被抑制，不受这里影响。',
  'telemetry/authentication': '请求头的名称是配置项，授权值只存在于环境变量里。'
}

/** The server's fixed English reasons, said in Chinese. An unknown reason shows as it is. */
export function reasonLabel(reason: string): string {
  if (reason.startsWith('Overridden by deployment')) return '由环境变量或启动参数覆盖，网页只读。请修改部署配置后重启。'
  if (reason.includes('root key')) return '根密钥保护所有管理员会话，更换它会让所有管理员登录失效。请通过部署环境或密钥文件管理。'
  if (reason.includes('running process is bound')) return '运行中的进程已绑定这个数据库。更换位置需要停机后离线操作。'
  if (reason.includes('never enters configuration')) return '授权值只存在于进程的环境变量中，不进入配置。请通过部署环境设置。'
  if (reason.startsWith('Removed.')) return '已移除的配置，网页和配置文件里都不要再设置。'
  return reason
}

const logLevels: Record<string, string> = {
  Trace: 'Trace（全部）', Debug: 'Debug（调试）', Information: 'Information（信息）', Warning: 'Warning（警告）',
  Error: 'Error（错误）', Critical: 'Critical（严重）', None: 'None（不输出）'
}
const optionLabels: Record<string, Record<string, string>> = {
  'Logging:LogLevel:Default': logLevels,
  'Logging:LogLevel:Microsoft.AspNetCore': logLevels,
  'Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command': logLevels,
  'Telemetry:Otlp:Traces:Protocol': { Grpc: 'gRPC', HttpProtobuf: 'HTTP Protobuf' },
  'Telemetry:Otlp:Metrics:Protocol': { Grpc: 'gRPC', HttpProtobuf: 'HTTP Protobuf' }
}
const emotionLabels: Record<string, string> = { '': '自动', calm: '平静', happy: '开心', sad: '悲伤', angry: '生气', fearful: '害怕', disgusted: '厌恶', surprised: '惊讶' }

export function optionLabel(field: SettingField, option: string): string {
  return optionLabels[field.key]?.[option] ?? (field.kind === 'select' && option in emotionLabels ? emotionLabels[option] : option)
}

export function fieldLabel(field: SettingField): string {
  return settingLabels[field.key]?.label ?? field.name
}
export function fieldDescription(field: SettingField): string | null {
  return settingLabels[field.key]?.description ?? field.description
}

function bytes(value: number): string {
  return value % 1048576 === 0 ? `${value / 1048576} MiB` : `${(value / 1048576).toFixed(1)} MiB`
}

/** A value the way the page reads it aloud: short, in Chinese units where there are any. */
export function formatValue(field: SettingField, value: SettingValue | undefined): string {
  if (field.kind === 'secret') return field.hasValue ? '已设置（不显示原值）' : '未设置'
  if (value === undefined || value === null) return field.kind === 'triState' ? '自动（按环境判断）' : '未设置'
  if (value === '') return '空'
  if (value === true) return '开启'
  if (value === false) return '关闭'
  if (Array.isArray(value)) {
    if (value.length === 0) return '无'
    const unit = settingLabels[field.key]?.unit
    return value.map(item => unit ? `${item} ${unit}` : String(item)).join('、')
  }
  if (field.kind === 'select') return optionLabel(field, String(value))
  if (field.key === 'Audio:MaxUploadBytes' && typeof value === 'number') return `${bytes(value)}（${value} 字节）`
  const unit = settingLabels[field.key]?.unit
  return unit ? `${value} ${unit}` : String(value)
}

/** A configuration key split where its display may wrap: after every ":" or "." that separates two parts. */
export function keySegments(key: string): string[] {
  return key.match(/[^:.]+[:.]*/g) ?? [key]
}

/** The text a control offers as its placeholder: the default, or a hint for a list. */
export function placeholder(field: SettingField): string {
  if (field.kind === 'list' || field.kind === 'integerList') return settingLabels[field.key]?.placeholder ?? '输入后回车添加'
  return settingLabels[field.key]?.placeholder ?? (field.defaultValue ? formatValue(field, field.defaultValue) : '')
}
