import type { EnrichmentTask } from './enrichmentApi'

export const stateLabels: Record<string, string> = { Pending: '待处理', Running: '处理中', Waiting: '等待', Succeeded: '已完成', Failed: '失败', Cancelled: '已取消' }
export const stepLabels: Record<string, string> = { Prepare: '发音准备', Fields: '默认字段', Audio: '音频生成' }
const reasons: Record<string, string> = {
  Disabled: '生成或字段补齐已关闭', ConfigurationMissing: '缺少该侧配置', StorageDisabled: '音频存储关闭',
  ServicePaused: '服务暂停，等待恢复探测', Throttled: '服务限流，等待后继续；不扣失败额度',
  Retry: '等待下次自动尝试', LeaseExpired: '执行租约已过期，等待恢复'
}
const errors: Record<string, string> = {
  'text.phonetic_constraint_mismatch': '准备读音与词库音标不符，请核对音标后重试',
  'text.template_unexpanded': '朗读模板未完整展开，请重试发音准备',
  'text.pronunciation_control_unsupported': '准备计划无法表达所需读音，请核对语音配置后重试',
  'provider.capability_unsupported': '服务或模型不支持所需读音，请修正配置；不会忽略约束生成',
  'provider.configuration_rejected': '服务拒绝配置或凭据，请修正服务配置',
  'provider.throttled': '供应商限流，不扣失败额度',
  'preparation.failed': '发音准备失败，需先恢复准备步骤',
  'fields.candidates_missing': '准备结果缺少可用字段', 'fields.equivalent_meaning': '字段与已有释义冲突',
  'fields.plan_too_large': '准备结果超过字段处理上限，请核对数据',
  'text.outcome_unknown': '文本请求结果未知，等待服务器恢复核对', 'tts.outcome_unknown': '语音请求结果未知，等待服务器恢复核对',
  'audio.invalidation_referenced': '旧文件仍被引用，等待安全清理', 'audio.invalidation_expired': '清理执行权限已过期，等待恢复',
  'audio.invalidation_failed': '旧文件清理失败，后台将继续处理', 'enrichment.failed': '本次补齐失败'
}
export const reasonLabel = (value: string | null) => value ? reasons[value] ?? '等待服务器处理' : '无'
export const errorLabel = (value: string | null) => value ? errors[value] ?? '操作失败，请核对数据或配置' : '无'
export function taskLabel(task: EnrichmentTask) {
  if (task.inheritsDefault) return '专用音标已清空，继承默认；无需生成专用音频'
  if (!task.applicable) return '当前不再需要此目标；历史保留'
  if (task.state === 'Failed' && task.current.consumedFailures >= task.current.failureBudget) return '失败额度已耗尽；服务恢复不会清零'
  if (task.meaningId && task.step === 'Audio' && task.missing) return `专用音频仍缺失：${stateLabels[task.state]}；默认播放回落不代表专用生成完成`
  return stateLabels[task.state] ?? task.state
}
