import { onBeforeRouteLeave } from 'vue-router'

export function useSettingsDraftGuard(hasDraft: () => boolean) {
  onBeforeRouteLeave(() => !hasDraft() || window.confirm('当前页有未保存的修改。确定放弃修改并离开吗？'))
}
