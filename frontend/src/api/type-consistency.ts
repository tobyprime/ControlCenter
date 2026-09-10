// TOB-401（审查问题1）：镜像类型 ↔ 生成类型一致性防线。
// 业务模块的手写镜像类型（视图层消费的必填化/字面量收窄视图）此前仅靠 `data as X` 桥接生成类型，
// 后端契约改名时 vue-tsc 与 drift 门禁双双放行。本文件以类型级断言强制：
//   每个镜像类型必须可赋值给其对应的生成 schema 类型 —— 后端字段改名/删除/类型收窄失真
//   → 再生成（npm run gen:api）后此处编译变红，镜像层被迫同步更新。
// 新增接口或生成 schema 时，必须在此补一条对应断言。
import type {
  AgentCreatedResponse,
  AgentResponse,
  AlertQueueItemResponse,
  AlertQueueResponse,
  AlertRuleResponse,
  AlertRuleTypeResponse,
  ActiveAlertCountResponse,
  CollectorAgentSummary,
  CollectorCreatedResponse,
  CollectorDataTypeResponse,
  CollectorResponse,
  ControlInvokeResponse,
  ControlLogEntry as GenControlLogEntry,
  ControlTypeInfoResponse,
  ControllerDeclaration as GenControllerDeclaration,
  DashboardCardResponse,
  DashboardLayoutResponse,
  InteractionModeResponse,
  LogLineInfo as GenLogLineInfo,
  LogServiceInfo as GenLogServiceInfo,
  LogServicesResponse,
  LogTailResponse,
  MetricKeyResponse,
  MetricOverviewItem as GenMetricOverviewItem,
  MetricsSampleResponse,
  NapcatSettingsResponse,
  PullConfigResponse,
  PullMappingRequest,
  PullMappingResponse,
  PullUpsertRequest,
  SaveAlertSettingsRequest,
  CreateAlertRuleRequest,
  UpdateAlertRuleRequest,
  SeriesPoint as GenSeriesPoint,
  SeriesResponse,
  MetricSeriesResponse,
  SessionInfoResponse,
  TerminalEntryResponse,
  TerminalSessionResponse,
} from './gen/types.gen'
import type { Agent, AgentCreated } from './agents'
import type {
  ActiveAlertCount,
  AlertQueue,
  AlertSettings,
  AlertSettingsInput,
  NapcatSettings,
  QueueItem,
} from './alerts'
import type { AlertRule, AlertRuleInput, AlertRuleTypeInfo, AlertRuleUpdate } from './alertRules'
import type { SessionInfo } from './auth'
import type {
  Collector,
  CollectorAgentSummary as MirrorCollectorAgentSummary,
  CollectorCreated,
  CollectorDataType,
  CollectorLatestSample,
  PullConfig,
  PullMapping,
  PullMetricMappingInput,
  PullUpsertInput,
} from './collectors'
// controls 的 ControllerDeclaration 与生成类型同名，镜像侧走别名引入
import type {
  ControlInvokeOutcome,
  ControlLogEntry,
  ControlTypeInfo,
  ControllerDeclaration as ControllerDeclarationMirror,
} from './controls'
import type { DashboardCardWire } from './dashboard'
import type { InteractionModeInfo } from './interactions'
import type {
  LogLineInfo,
  LogServiceInfo,
} from './logs'
import type {
  MetricKeyInfo,
  MetricOverviewItem,
  MetricSeries,
  SeriesPoint,
  TargetSeries,
} from './metrics'
import type { TerminalRecordInfo, TerminalSessionInfo } from './terminal'

// 单条断言：M（镜像）的每个字段都必须存在于 G（生成 schema）中，且整体可赋值给 G。
// 生成 schema 字段全为 optional，仅靠可赋值性无法发现字段改名/删除（镜像多出的字段被静默放行），
// 故先做键覆盖检查（keyof M ⊆ keyof G），再做整体可赋值检查（镜像字段类型不得比生成字段更宽）。
type MirrorOf<M, G> = [keyof M] extends [keyof G] ? ([M] extends [G] ? true : false) : false

export type ApiMirrorTypeConsistency = [
  // agents
  MirrorOf<Agent, AgentResponse>,
  MirrorOf<AgentCreated, AgentCreatedResponse>,
  // collectors
  MirrorOf<Collector, CollectorResponse>,
  MirrorOf<CollectorAgentSummary, CollectorAgentSummary>,
  MirrorOf<CollectorCreated, CollectorCreatedResponse>,
  MirrorOf<CollectorDataType, CollectorDataTypeResponse>,
  MirrorOf<CollectorLatestSample, MetricsSampleResponse>,
  MirrorOf<PullConfig, PullConfigResponse>,
  MirrorOf<PullMapping, PullMappingResponse>,
  MirrorOf<PullUpsertInput, PullUpsertRequest>,
  MirrorOf<PullMetricMappingInput, PullMappingRequest>,
  // alerts
  MirrorOf<AlertSettings, { napcat?: NapcatSettingsResponse }>,
  MirrorOf<NapcatSettings, NapcatSettingsResponse>,
  MirrorOf<AlertSettingsInput, SaveAlertSettingsRequest>,
  MirrorOf<AlertQueue, AlertQueueResponse>,
  MirrorOf<QueueItem, AlertQueueItemResponse>,
  MirrorOf<ActiveAlertCount, ActiveAlertCountResponse>,
  // alert rules
  MirrorOf<AlertRuleTypeInfo, AlertRuleTypeResponse>,
  MirrorOf<AlertRule, AlertRuleResponse>,
  MirrorOf<AlertRuleInput, CreateAlertRuleRequest>,
  MirrorOf<AlertRuleUpdate, UpdateAlertRuleRequest>,
  // auth
  MirrorOf<SessionInfo, SessionInfoResponse>,
  // controls
  MirrorOf<ControlTypeInfo, ControlTypeInfoResponse>,
  MirrorOf<ControllerDeclarationMirror, GenControllerDeclaration>,
  MirrorOf<ControlInvokeOutcome, ControlInvokeResponse>,
  MirrorOf<ControlLogEntry, GenControlLogEntry>,
  // interactions
  MirrorOf<InteractionModeInfo, InteractionModeResponse>,
  // metrics
  MirrorOf<MetricKeyInfo, MetricKeyResponse>,
  MirrorOf<SeriesPoint, GenSeriesPoint>,
  MirrorOf<MetricSeries, MetricSeriesResponse>,
  MirrorOf<TargetSeries, SeriesResponse>,
  MirrorOf<MetricOverviewItem, GenMetricOverviewItem>,
  // terminal
  MirrorOf<TerminalSessionInfo, TerminalSessionResponse>,
  MirrorOf<TerminalRecordInfo, TerminalEntryResponse>,
  // logs（响应体信封 inline 断言）
  MirrorOf<{ services: LogServiceInfo[] }, LogServicesResponse>,
  MirrorOf<LogServiceInfo, GenLogServiceInfo>,
  MirrorOf<{ lines: LogLineInfo[] }, LogTailResponse>,
  MirrorOf<LogLineInfo, GenLogLineInfo>,
  // dashboard（wire 卡片 + 布局信封；sort→order 映射在模块内）
  MirrorOf<DashboardCardWire, DashboardCardResponse>,
  MirrorOf<{ cards: DashboardCardWire[] }, DashboardLayoutResponse>,
]

// 值级锚点：强制上述断言在编译期求值；任何一条为 false 时此行报错（true 不可赋值链断裂）。
export const apiMirrorTypeConsistency: true = null as unknown as ApiMirrorTypeConsistency[number]
