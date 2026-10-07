import { useEffect, useId, useRef, useState } from 'react'

import type { ProjectListItem, SourceStatus } from '../../bridge'
import { projectPosterUrl, projectThumbnailUrl } from '../../bridge'
import { AppIcon } from '../../components/AppIcons'
import { getProjectDisplayName } from './projectState'
import './projects.css'

const pageSize = 6

const sourceStatusLabels: Readonly<Record<SourceStatus, string>> = {
  notSelected: '未選択',
  available: '選択済み',
  reselectionRequired: '再選択が必要',
}

export interface ProjectListCardsProps {
  readonly projects: readonly ProjectListItem[]
  readonly disabled: boolean
  readonly onOpen: (draftId: string) => void
  /** Omitted when the actor cannot delete; the native host still enforces authority. */
  readonly onDelete?: (draftId: string, name: string) => void
}

export function ProjectListCards({
  projects,
  disabled,
  onOpen,
  onDelete,
}: ProjectListCardsProps) {
  const [page, setPage] = useState(0)
  const listId = useId()
  const firstProjectRef = useRef<HTMLButtonElement>(null)
  const focusAfterPaging = useRef(false)
  const pageCount = Math.max(1, Math.ceil(projects.length / pageSize))
  const currentPage = Math.min(page, pageCount - 1)

  // Clamp the rendered slice immediately and retain that page if the list grows again.
  useEffect(() => {
    setPage((previous) => Math.min(previous, pageCount - 1))
  }, [pageCount])

  useEffect(() => {
    if (focusAfterPaging.current) {
      focusAfterPaging.current = false
      firstProjectRef.current?.focus()
    }
  }, [currentPage])

  const changePage = (nextPage: number): void => {
    if (disabled || nextPage < 0 || nextPage >= pageCount || nextPage === currentPage) {
      return
    }

    focusAfterPaging.current = true
    setPage(nextPage)
  }

  if (projects.length === 0) {
    return (
      <div className="project-list-cards">
        <div className="empty-state project-list-cards-empty" role="status">
          <strong>Projectはまだありません</strong>
          <p>作成権限がある場合は「新しいProjectを作成」から始められます。</p>
        </div>
      </div>
    )
  }

  const start = currentPage * pageSize
  const visibleProjects = projects.slice(start, start + pageSize)

  return (
    <div className="project-list-cards">
      <ul
        className="project-list-cards-grid"
        id={listId}
        role="list"
        aria-label="閲覧を許可されたProjectの一覧"
      >
        {visibleProjects.map((project, index) => {
          const name = getProjectDisplayName(project) ?? '名称未入力'
          return (
            <li className="project-card" key={project.draftId}>
              <ProjectCardThumbnail project={project} name={name} />
              <div className="project-card-header">
                <h3 className="project-card-heading">
                  <button
                    className="project-link project-card-open"
                    type="button"
                    ref={index === 0 ? firstProjectRef : undefined}
                    disabled={disabled}
                    title={name}
                    aria-label={`${name}を開く`}
                    onClick={() => onOpen(project.draftId)}
                  >
                    <span className="project-card-name">{name}</span>
                    <span className="project-card-open-label" aria-hidden="true">開く</span>
                  </button>
                </h3>
                <span className={`state-badge ${project.state}`}>
                  {project.state === 'finalized' ? '作成済み' : '下書き'}
                </span>
              </div>
              <dl className="project-card-metadata">
                <div>
                  <dt><AppIcon name="video" size={16} />元動画</dt>
                  <dd>
                    <span className={`source-status ${project.sourceStatus}`}>
                      {sourceStatusLabels[project.sourceStatus]}
                    </span>
                  </dd>
                </div>
                <div>
                  <dt>更新日時</dt>
                  <dd>
                    <time dateTime={project.updatedAt}>{formatUtc(project.updatedAt)}</time>
                  </dd>
                </div>
              </dl>
              {onDelete !== undefined && (
                <div className="project-card-actions">
                  <button
                    className="danger-button project-card-delete"
                    type="button"
                    disabled={disabled}
                    aria-label={`${name}を削除`}
                    onClick={() => onDelete(project.draftId, name)}
                  >
                    削除
                  </button>
                </div>
              )}
            </li>
          )
        })}
      </ul>
      <nav className="project-list-pagination" aria-label="Project一覧のページ切替">
        <p className="project-list-page-status" role="status" aria-live="polite" aria-atomic="true">
          {`${currentPage + 1} / ${pageCount} ページ（${start + 1}–${start + visibleProjects.length} / ${projects.length} 件）`}
        </p>
        <div className="project-list-page-actions">
          <button
            className="secondary-button"
            type="button"
            disabled={disabled || currentPage === 0}
            aria-controls={listId}
            onClick={() => changePage(currentPage - 1)}
          >
            前のページ
          </button>
          <button
            className="secondary-button"
            type="button"
            disabled={disabled || currentPage === pageCount - 1}
            aria-controls={listId}
            onClick={() => changePage(currentPage + 1)}
          >
            次のページ
          </button>
        </div>
      </nav>
    </div>
  )
}

function ProjectCardThumbnail({
  project,
  name,
}: Readonly<{ project: ProjectListItem; name: string }>) {
  // FR-053: the generated thumbnail first, then the poster frame of the source video.
  const candidates = [
    project.projectId !== null && project.thumbnail != null
      ? projectThumbnailUrl(project.projectId, project.thumbnail)
      : null,
    project.poster != null ? projectPosterUrl(project.draftId, project.poster) : null,
  ].filter((value): value is string => value !== null)
  const [failedUrls, setFailedUrls] = useState<readonly string[]>([])
  const url = candidates.find(candidate => !failedUrls.includes(candidate)) ?? null

  if (url === null) {
    return (
      <div className="project-card-thumbnail project-card-thumbnail-empty">
        サムネイルなし
      </div>
    )
  }

  return (
    <div className="project-card-thumbnail">
      <img
        src={url}
        alt={`${name}のサムネイル`}
        loading="lazy"
        decoding="async"
        draggable={false}
        onError={() => setFailedUrls(current => current.includes(url) ? current : [...current, url])}
      />
    </div>
  )
}

function formatUtc(value: string): string {
  const date = new Date(value)
  const year = date.getUTCFullYear().toString().padStart(4, '0')
  const month = (date.getUTCMonth() + 1).toString().padStart(2, '0')
  const day = date.getUTCDate().toString().padStart(2, '0')
  const hour = date.getUTCHours().toString().padStart(2, '0')
  const minute = date.getUTCMinutes().toString().padStart(2, '0')
  return `${year}/${month}/${day} ${hour}:${minute} UTC`
}