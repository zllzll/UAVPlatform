/**
 * 悬停提示：浮层本体 + 把提示挂到任意元素上的 hook + 被 CSS 截断文本的现成包装。
 *
 * 为什么浮层挂在 document.body 上而不是就地绝对定位：左右两侧面板自己就是滚动容器，
 * 就地绝对定位的浮层会在容器下沿被裁掉，越靠近底部的参数越看不到完整说明。
 *
 * 为什么不做成常驻文本：一屏塞着几十个输入框，常驻的灰色说明会把真正要看的参数淹掉；
 * 现场用法基本是「先扫一遍名字，拿不准的才看说明」。
 *
 * 为什么单独抽成模块：左侧参数面板、右侧信息面板、状态面板都有「显示不全的文字」，
 * 各写一份迟早会走形（浮层的翻转、视口夹取、滚动即收起这些细节很容易漏）。
 */
import { useEffect, useState } from 'react'
import { createPortal } from 'react-dom'

/** 悬停提示的浮层本体。 */
export function HintTip({ text, rect }: { text: string; rect: DOMRect }): React.ReactElement {
  const width = Math.min(320, Math.max(180, rect.width))
  // 浮层还没渲染，量不到真实高度，按字数估一个够用的值来决定往上翻还是往下弹。
  const height = Math.min(240, 22 + Math.ceil(text.length / 26) * 16)
  const flip = rect.bottom + 6 + height > window.innerHeight - 8
  const style: React.CSSProperties = {
    left: Math.min(Math.max(8, rect.left), Math.max(8, window.innerWidth - width - 8)),
    top: flip ? Math.max(8, rect.top - 6 - height) : rect.bottom + 6,
    width,
  }
  return createPortal(
    <span className="hinttip" style={style} role="tooltip">
      {text}
    </span>,
    document.body,
  )
}

/** 把「鼠标移上来 / 键盘聚焦才出现」的提示挂到任意元素上。
 *
 * 用法：把 handlers 展开到触发元素上（整行 <label> 或控件本身），tip 是浮层节点。
 * 注意：提示挂在**最内层**带提示的元素上；两层嵌套都带提示时内层会盖住外层。
 *
 * `onlyWhenTruncated` 用于「本来就可能显示不全」的文本：此时只有在元素自身真的被
 * 截断（scrollWidth 大于 clientWidth）时才弹——不然一个本来就完整的名字也会弹出一模一样的浮层。
 * 注意这个判定挂在**带 `overflow: hidden` 的那一层**上，套在里面的子元素量不出截断。
 */
export function useHoverTip(
  text?: string,
  options?: { onlyWhenTruncated?: boolean },
): {
  handlers: {
    onMouseEnter: (event: React.MouseEvent<HTMLElement>) => void
    onMouseLeave: () => void
    onFocus: (event: React.FocusEvent<HTMLElement>) => void
    onBlur: () => void
  }
  tip: React.ReactNode
} {
  const [rect, setRect] = useState<DOMRect | null>(null)
  const onlyWhenTruncated = options?.onlyWhenTruncated ?? false

  useEffect(() => {
    if (!rect) return
    // 面板一滚动锚点位置就失效了，直接收起来比让浮层僵在原地自然。
    const close = () => setRect(null)
    window.addEventListener('scroll', close, true)
    window.addEventListener('resize', close)
    return () => {
      window.removeEventListener('scroll', close, true)
      window.removeEventListener('resize', close)
    }
  }, [rect])

  const open = (event: React.MouseEvent<HTMLElement> | React.FocusEvent<HTMLElement>) => {
    if (!text) return
    const element = event.currentTarget
    if (onlyWhenTruncated && element.scrollWidth <= element.clientWidth + 1) return
    setRect(element.getBoundingClientRect())
  }

  return {
    handlers: {
      onMouseEnter: open,
      onMouseLeave: () => setRect(null),
      onFocus: open,
      onBlur: () => setRect(null),
    },
    tip: rect && text ? <HintTip text={text} rect={rect} /> : null,
  }
}

/** 会被 CSS 截断的一行文本：鼠标移上去（或键盘聚焦）弹出完整内容。
 *
 * 传进来的元素自己必须是截断的那一层（`.ellipsis` 之类的类名，
 * 或者父级用 `.hintwrap > *` 统一截断），否则量不出截断、也不会弹。
 */
export function TruncatedText({
  text,
  className,
}: {
  text: string
  className?: string
}): React.ReactElement {
  const tip = useHoverTip(text, { onlyWhenTruncated: true })
  return (
    <span className={className} {...tip.handlers}>
      {text}
      {tip.tip}
    </span>
  )
}
