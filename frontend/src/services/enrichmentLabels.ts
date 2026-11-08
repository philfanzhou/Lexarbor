import type { EnrichmentTask } from './enrichmentApi'

export const stateLabels: Record<string, string> = { Pending: '待处理', Running: '处理中', Waiting: '等待', Succeeded: '已完成', Failed: '失败', Cancelled: '已取消', Superseded: '已被新一轮替代' }
export const stepLabels: Record<string, string> = { Prepare: '发音准备', Fields: '默认字段', Audio: '音频生成', Expand: '词形与派生词扩展' }
const reasons: Record<string, string> = {
  Disabled: '生成或字段补齐已关闭', ConfigurationMissing: '等待模型、口音或补充词书配置就绪', PreparationPending: '等待前置发音准备完成',
  StorageDisabled: '音频存储关闭',
  ServicePaused: '服务暂停，等待恢复探测', Throttled: '服务限流，等待后继续；不扣失败额度',
  Retry: '等待下次自动尝试', LeaseExpired: '执行租约已过期，等待恢复'
}
const errors: Record<string, string> = {
  'expansion.invalid_response': '词汇扩展结果未通过校验',
  'expansion.outcome_unknown': '词汇扩展请求结果未知，等待恢复',
  'expansion.destination_changed': '补充词书已变化，请重试',
  'expansion.input_too_large': '来源词义超过模型处理上限',
  'text.phonetic_constraint_mismatch': '准备读音与词库音标不符，请核对音标后重试',
  'text.template_unexpanded': '朗读模板未完整展开，请重试发音准备',
  'text.pronunciation_control_unsupported': '准备计划无法表达所需读音，请核对语音配置后重试',
  'text.pronunciation_text_mismatch': '朗读文字与词条的发音约束不一致',
  'text.invalid_plan': 'AI 返回的发音方案未通过检查',
  'batch.timeout': 'AI 请求超时', 'batch.response_too_large': 'AI 返回内容过长',
  'batch.invalid_response': 'AI 返回内容未通过检查',
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
export const taskStateLabel = (task: EnrichmentTask) => task.current.fieldsNotRequired && !task.missing ? '无需补全' : task.automaticRecoveryAvailable ? '等待自动恢复' : stateLabels[task.state]
export function taskLabel(task: EnrichmentTask) {
  if (task.current.fieldsNotRequired && !task.missing) return '短语无需补单词音标和词性；历史保留'
  if (task.automaticRecoveryAvailable) return '等待后台自动恢复，无需手动重试'
  if (task.current.automaticRecovery && task.state === 'Failed') return '自动恢复已停止，需复核剩余原因'
  if (task.current.automaticRecovery && task.missing) return '正在自动恢复；失败次数和进度会跨重启保留'
  if (task.inheritsDefault) return '专用音标已清空，继承默认；无需生成专用音频'
  if (!task.applicable) return '当前不再需要此目标；历史保留'
  if (task.state === 'Failed' && task.current.consumedFailures >= task.current.failureBudget) return '失败额度已耗尽；服务恢复不会清零'
  if (task.meaningId && task.step === 'Audio' && task.missing) return `专用音频仍缺失：${stateLabels[task.state]}；默认播放回落不代表专用生成完成`
  return stateLabels[task.state] ?? task.state
}

export const activityLabels: Record<string, string> = { Fields: '音标与词性', Expand: '词形与派生词', Audio: '单词读音' }
export const activityDescriptions: Record<string, string> = {
  Fields: '补齐已有词汇缺失的音标和词性。', Expand: '生成新词并加入补充词书，已有词直接复用。', Audio: '补齐英式、美式及释义专用读音。'
}
export const requirementLabels: Record<string, string> = {
  text: '配置文本模型', supplement: '检查补充词书', ukSpeech: '检查英式语音配置', usSpeech: '检查美式语音配置', storage: '配置音频存储'
}
export const requirementLinks: Record<string, string> = {
  text: '/settings/text', supplement: '/books', ukSpeech: '/settings/speech', usSpeech: '/settings/speech', storage: '/settings/audio-storage'
}
export const callLabels: Record<string, string> = { Started: '请求已发出，结果待确认', Succeeded: '成功', Throttled: '服务限流', TransientFailure: '暂时失败', TerminalFailure: '本轮停止' }
export const cleanupLabels: Record<string, string> = { Waiting: '等待清理', Claimed: '正在清理', Observed: '已确认清理' }

export function guidance(task: EnrichmentTask): { title: string; description: string; link?: string; action?: string } {
  if (task.current.fieldsNotRequired && !task.missing) return { title: '短语无需补全', description: '已确认这是短语，无需补单词音标和词性；读音生成单独继续。' }
  if (!task.applicable) return { title: '无需处理', description: task.inheritsDefault ? '此释义已恢复使用单词的默认读音。' : '词条或生成需求已经变化，这条任务仅保留历史。' }
  if (!task.missing) return { title: '已完成，无需重试', description: task.step === 'Expand' ? '本轮扩展已处理。没有可靠的新词时也会正常完成；可在词条中查看已有关系。' : '当前需要的内容已经具备，系统不会重复生成。' }
  if (task.waitReason === 'StorageDisabled') return { title: '需要配置音频存储', description: '存储就绪后，缺失的读音会自动继续生成。', link: '/settings/audio-storage', action: '检查音频存储' }
  if (task.waitReason === 'PreparationPending') return {
    title: '等待前置发音准备', description: '文本模型正在准备读音和字段，完成后此步骤会自动继续；无需手动重试。'
  }
  if (task.waitReason === 'ConfigurationMissing' || task.waitReason === 'Disabled') return {
    title: '需要检查配置', description: task.step === 'Expand' ? '文本模型和补充词书均须就绪。请检查上方对应任务卡片，补充词书须存在且启用。' : task.step === 'Audio' ? '需要文本模型、语音模型和当前口音的音色配置。配置就绪后自动继续。' : '文本模型配置就绪后自动继续，无需另开功能开关。',
    link: task.step === 'Audio' ? '/settings/speech' : '/settings/text', action: task.step === 'Audio' ? '检查语音配置' : '检查文本模型'
  }
  if (task.state === 'Failed') {
    if (task.automaticRecoveryAvailable) return { title: '等待自动恢复', description: '后台会自动重新处理这项技术性失败，无需点击重试；已有成功结果会保留。' }
    if (task.current.automaticRecovery) return { title: '自动恢复已停止，待复核', description: `${errorLabel(task.current.errorCode)}。本次自动恢复不会因重启再次开启，请核对原因后决定是否重试。` }
    const configuration = task.current.errorCode === 'provider.configuration_rejected' || task.current.errorCode === 'provider.capability_unsupported' || task.current.errorCode === 'text.pronunciation_control_unsupported'
    return { title: '本轮已停止，需要处理', description: `${errorLabel(task.current.errorCode)}。${configuration ? '先修正对应模型配置，再重试此步骤。' : '先检查词条内容与失败原因，再重试此步骤；已完成的其他内容会保留。'}`,
      ...(configuration ? { link: task.step === 'Audio' || task.current.errorCode === 'text.pronunciation_control_unsupported' ? '/settings/speech' : '/settings/text', action: '检查模型配置' } : {}) }
  }
  if (task.current.automaticRecovery) return { title: task.state === 'Waiting' ? '自动恢复：等待下次尝试' : '自动恢复中', description: `已消耗 ${task.current.consumedFailures}/${task.current.failureBudget} 次失败额度。${task.current.forceSingle ? '此任务会单独请求 AI，避免受同批其他项目影响。' : ''}` }
  if (task.state === 'Running') return { title: '正在处理，无需操作', description: '页面会自动更新结果，离开页面不会停止后台任务。' }
  if (task.waitReason === 'Throttled' || task.waitReason === 'ServicePaused' || task.waitReason === 'Retry' || task.waitReason === 'LeaseExpired') return {
    title: '系统会自动继续', description: task.waitReason === 'Throttled' ? '模型服务暂时限制请求频率。系统会退避后再试，不消耗失败额度，无需手动重试。' : '后台正在等待服务恢复或下一次尝试，无需手动启动。'
  }
  return { title: '已排队，无需操作', description: '后台会逐步处理新增和历史缺失，离开页面不影响任务执行。' }
}
