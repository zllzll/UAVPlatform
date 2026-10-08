/**
 * 前端入口。
 *
 * 顺序要点：`index.css` 必须在 React 挂载前导入，否则三维画布的父容器在首帧量不到尺寸
 * （`Viewer3D` 用 `Canvas` 自动填满父元素，父元素高度来自 CSS grid）。
 */
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './app/App'
import './index.css'

const container = document.getElementById('root')
if (!container) {
  throw new Error('未找到 #root 挂载点，index.html 可能被改动。')
}

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
