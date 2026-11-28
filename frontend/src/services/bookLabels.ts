import type { BookUnit } from '@/types'

/** A unit as the unit picker names it: `第 2 单元 School Life`, or `第 2 单元` when it has no title. */
export const unitOptionLabel = (unit: BookUnit) => `第 ${unit.number} 单元 ${unit.title ?? ''}`
