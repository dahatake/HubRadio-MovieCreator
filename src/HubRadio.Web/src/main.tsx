import './theme'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'

import App from './App'
import { ComparisonStateProvider } from './features/compare'
import { DeliveryStateProvider } from './features/delivery'
import { LayoutStateProvider } from './features/layout'
import { PlayerNavigationProvider } from './features/player'
import { PreviewModeProvider } from './features/preview-modes'
import { ProjectStateProvider } from './features/projects/ProjectStateProvider'
import { ReviewStateProvider } from './features/review'
import { AppStateProvider } from './state'
import { SettingsStateProvider } from './features/settings'
import './styles.css'

const rootElement = document.getElementById('root')

if (rootElement === null) {
  throw new Error('React root element was not found.')
}

createRoot(rootElement).render(
  <StrictMode>
    <AppStateProvider>
      <ProjectStateProvider>
        <PreviewModeProvider>
          <PlayerNavigationProvider>
            <LayoutStateProvider>
              <ComparisonStateProvider>
                <ReviewStateProvider>
                  <DeliveryStateProvider>
                    <SettingsStateProvider>
                      <App />
                    </SettingsStateProvider>
                  </DeliveryStateProvider>
                </ReviewStateProvider>
              </ComparisonStateProvider>
            </LayoutStateProvider>
          </PlayerNavigationProvider>
        </PreviewModeProvider>
      </ProjectStateProvider>
    </AppStateProvider>
  </StrictMode>,
)
