import './theme'
// Base rules precede the feature-local styles imported by App's workspace.
import './styles.css'
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
import { SettingsWorkspaceStateProvider } from './features/settings'

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
                    <SettingsWorkspaceStateProvider>
                      <App />
                    </SettingsWorkspaceStateProvider>
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
