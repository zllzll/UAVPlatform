/**
 * 只给「界面偏好」用的一小段持久化状态：下次打开网页还是上次的样子。
 *
 * 为什么不用后端配置存这些：通讯 / 存储 / 相对位置 / 显示开关都是后端
 * `config\platform.json` 说了算，界面这边再存一份迟早会和后端的值打架。
 * 这里存的只是「上次在看哪一页、面板收没收起、视角停在哪」这类纯前端偏好——
 * 丢了只是回到默认布局，不影响采集，也不会污染现场参数。
 *
 * 读取时做一次类型校验：localStorage 里可能留着旧版本写的值（比如某个模式后来被删掉了），
 * 类型对不上就退回默认值，别把脏值喂给组件。
 */
import { useCallback, useState } from 'react'

export function usePersistentState<T extends string | boolean | number>(
  key: string,
  initial: T,
): [T, (next: T) => void] {
  const [value, setValue] = useState<T>(() => {
    try {
      const raw = window.localStorage.getItem(key)
      if (raw === null) return initial
      const parsed: unknown = JSON.parse(raw)
      return typeof parsed === typeof initial ? (parsed as T) : initial
    } catch {
      return initial
    }
  })

  const update = useCallback(
    (next: T) => {
      setValue(next)
      try {
        window.localStorage.setItem(key, JSON.stringify(next))
      } catch {
        // 隐私模式 / 存储被禁用时 localStorage 会直接抛异常：记不住就算了，不能让界面崩掉。
      }
    },
    [key],
  )

  return [value, update]
}
