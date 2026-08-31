import './theme'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import App from './App'
import { ProjectStateProvider } from './features/projects/ProjectStateProvider'
import { AppStateProvider } from './state'
import './styles.css'

const rootElement = document.getElementById('root')

if (rootElement === null) {
  throw new Error('React root element was not found.')
}

createRoot(rootElement).render(
  <StrictMode>
    <AppStateProvider>
      <ProjectStateProvider>
        <App />
      </ProjectStateProvider>
    </AppStateProvider>
  </StrictMode>,
)
