import './theme'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import App from './App'
import { ComparisonStateProvider } from './features/compare'
import { PlayerNavigationProvider } from './features/player'
import { ProjectStateProvider } from './features/projects/ProjectStateProvider'
import { ReviewStateProvider } from './features/review'
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
        <PlayerNavigationProvider>
          <ComparisonStateProvider>
            <ReviewStateProvider>
              <App />
            </ReviewStateProvider>
          </ComparisonStateProvider>
        </PlayerNavigationProvider>
      </ProjectStateProvider>
    </AppStateProvider>
  </StrictMode>,
)
