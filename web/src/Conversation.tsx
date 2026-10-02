import { useEffect, useRef, useState, type ChangeEvent, type FormEvent } from 'react'
import { api, ago, type Agent, type ConversationEntry, type ConversationImage, type Project } from './api'
import { Markdown } from './Markdown'

const MAX_IMAGE_BYTES = 2 * 1024 * 1024
const MAX_IMAGES = 2

type DraftImage = { key: string; file: File; url: string; upload?: ConversationImage; state: 'uploading' | 'ready' | 'failed'; error?: string }
type PendingSend = { text: string; attachment_ids: string[]; client_message_id: string }

/** Accept only the project's own authorized image route, never a remote or data: URL from metadata. */
function imageUrl(projectId: string, image: ConversationImage): string | undefined {
  const expected = `/api/v1/projects/${encodeURIComponent(projectId)}/conversation/images/${encodeURIComponent(image.id)}/content`
  return image.content_url === expected ? expected : undefined
}

export function Conversation(props: { project: Project; agents: Agent[]; tick: number; deltas: Record<string, string> }) {
  // Remounting a project draft keeps attachment IDs, in-flight requests and object URLs from
  // ever crossing a project boundary. The child aborts and revokes all of them on unmount.
  return <ProjectConversation key={props.project.id} {...props} />
}

function ProjectConversation({ project, agents, tick, deltas }: { project: Project; agents: Agent[]; tick: number; deltas: Record<string, string> }) {
  const [entries, setEntries] = useState<ConversationEntry[]>([])
  const [text, setText] = useState('')
  const [images, setImages] = useState<DraftImage[]>([])
  const imagesRef = useRef<DraftImage[]>([])
  const [error, setError] = useState<string>()
  const [sending, setSending] = useState(false)
  const [pendingSend, setPendingSend] = useState<PendingSend>()
  const pendingRef = useRef<PendingSend | undefined>(undefined)
  const sendingRef = useRef(false)
  const active = useRef(true)
  const uploads = useRef(new Map<string, AbortController>())
  const sendController = useRef<AbortController | undefined>(undefined)
  const bottom = useRef<HTMLDivElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const root = agents.find((a) => a.id === project.root_agent_id)
  const locked = sending || !!pendingSend
  const uploading = images.some((image) => image.state === 'uploading')

  const setDraftImages = (update: (current: DraftImage[]) => DraftImage[]) => {
    const next = update(imagesRef.current)
    imagesRef.current = next
    if (active.current) setImages(next)
  }
  useEffect(() => {
    active.current = true
    return () => {
      active.current = false
      sendController.current?.abort()
      uploads.current.forEach((controller) => controller.abort())
      imagesRef.current.forEach((image) => {
        URL.revokeObjectURL(image.url)
        // Leaving a project abandons its unsent draft: release server-side uploads as
        // well as local blobs. A lost receipt can still consume the documented quota.
        if (image.upload && !pendingRef.current) void api.deleteConversationImage(project.id, image.upload.id).catch(() => {})
      })
    }
  }, [project.id])
  useEffect(() => {
    let current = true
    api.get<ConversationEntry[]>(`/projects/${project.id}/conversation`).then((items) => {
      if (!current) return
      // A poll may have started before a successful send. Never let a stale history
      // response erase its confirmed receipt while the next poll is still pending.
      setEntries((existing) => [...items, ...existing.filter((entry) => !items.some((item) => item.id === entry.id))])
    }).catch((e) => { if (current) setError((e as Error).message) })
    return () => { current = false }
  }, [project.id, tick])
  useEffect(() => { bottom.current?.scrollIntoView({ behavior: 'smooth' }) }, [entries.length])

  const upload = async (draft: DraftImage): Promise<void> => {
    const controller = new AbortController()
    uploads.current.set(draft.key, controller)
    setDraftImages((current) => current.map((image) => image.key === draft.key ? { ...image, state: 'uploading', error: undefined } : image))
    try {
      const uploaded = await api.uploadConversationImage(project.id, draft.file, controller.signal)
      if (!active.current || !imagesRef.current.some((image) => image.key === draft.key)) {
        // An upload may have committed before a local removal/abort. Reclaim it when its
        // receipt still arrives; server quota can remain if a response is lost entirely.
        void api.deleteConversationImage(project.id, uploaded.id).catch(() => {})
        return
      }
      if (!imageUrl(project.id, uploaded)) {
        void api.deleteConversationImage(project.id, uploaded.id).catch(() => {})
        throw new Error('Upload returned an invalid project image URL')
      }
      setDraftImages((current) => current.map((image) => image.key === draft.key ? { ...image, upload: uploaded, state: 'ready' } : image))
      setError(undefined)
    } catch (e) {
      if (controller.signal.aborted || !active.current) return
      const message = (e as Error).message
      setDraftImages((current) => current.map((image) => image.key === draft.key ? { ...image, state: 'failed', error: message } : image))
      setError(`Image upload failed: ${message}. Retry the image without losing your draft.`)
    } finally {
      uploads.current.delete(draft.key)
    }
  }

  const selectImages = (event: ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(event.target.files ?? [])
    event.target.value = '' // selecting the same file again must work after removal
    if (locked) return
    if (files.length + imagesRef.current.length > MAX_IMAGES) { setError(`Choose at most ${MAX_IMAGES} PNG images per message.`); return }
    const invalid = files.find((file) => file.type !== 'image/png' || file.size > MAX_IMAGE_BYTES || file.size === 0)
    if (invalid) { setError(`${invalid.name}: choose a PNG image no larger than 2 MiB (non-empty).`); return }
    setError(undefined)
    const staged = files.map((file) => ({ key: crypto.randomUUID(), file, url: URL.createObjectURL(file), state: 'uploading' as const }))
    setDraftImages((current) => [...current, ...staged])
    staged.forEach((image) => { void upload(image) })
  }
  const removeImage = (image: DraftImage) => {
    if (locked) return
    uploads.current.get(image.key)?.abort()
    setDraftImages((current) => current.filter((item) => item.key !== image.key))
    URL.revokeObjectURL(image.url)
    if (image.upload) void api.deleteConversationImage(project.id, image.upload.id).catch(() => {
      if (active.current) setError('Image removed from draft, but server cleanup failed. Retained upload quota may still be used.')
    })
  }

  const send = async (event?: FormEvent) => {
    event?.preventDefault()
    if (sendingRef.current || uploading) return
    const failed = imagesRef.current.find((image) => image.state !== 'ready' || !image.upload)
    if (failed) { setError('Retry or remove failed image uploads before sending.'); return }
    const body = pendingRef.current ?? { text: text.trim(), attachment_ids: imagesRef.current.map((image) => image.upload!.id), client_message_id: crypto.randomUUID() }
    if (!body.text && body.attachment_ids.length === 0) return
    // On failed/lost-response retries, reuse the *exact* body and key. Lock all draft edits
    // until success, avoiding a server 409 or an accidental duplicate dispatch.
    pendingRef.current = body
    setPendingSend(body)
    sendingRef.current = true
    setSending(true)
    setError(undefined)
    const controller = new AbortController()
    sendController.current = controller
    try {
      const receipt = await api.sendConversation(project.id, body, controller.signal)
      if (!active.current) return
      setEntries((current) => current.some((item) => item.id === receipt.id) ? current : [...current, receipt])
      imagesRef.current.forEach((image) => URL.revokeObjectURL(image.url))
      imagesRef.current = []
      setImages([])
      setText('')
      pendingRef.current = undefined
      setPendingSend(undefined)
      // A failed history refresh must never turn a successful send back into a retryable draft.
      void api.get<ConversationEntry[]>(`/projects/${project.id}/conversation`).then((items) => {
        if (active.current) setEntries((existing) => [...items, ...existing.filter((entry) => !items.some((item) => item.id === entry.id))])
      }).catch(() => {})
    } catch (e) {
      if (active.current && !controller.signal.aborted) setError(`Message not confirmed: ${(e as Error).message}. Retry uses the same message ID; your draft is intact.`)
    } finally {
      sendingRef.current = false
      if (active.current) setSending(false)
      if (sendController.current === controller) sendController.current = undefined
    }
  }

  const working = root && root.status && root.status !== 'idle'
  const streaming = root?.primary_session_id ? deltas[root.primary_session_id] : undefined
  return (
    <div className="conversation">
      <div className="messages">
        {entries.length === 0 && <div className="empty">Tell {root?.name ?? 'the manager'} what you want built. The manager is your only point of contact; the team works in the background.</div>}
        {entries.map((entry) => (
          <div key={entry.id} className={`message ${entry.author}`}>
            <div className="message-meta">{entry.author === 'user' ? 'You' : entry.author === 'system' ? 'Runtime' : root?.name ?? 'Manager'} · {ago(entry.created_at)}</div>
            {entry.body && <div className="message-body"><Markdown text={entry.body} /></div>}
            {!!entry.attachments?.length && <div className="conversation-images">{entry.attachments.map((image) => {
              const url = imageUrl(project.id, image)
              return url && <a key={image.id} href={url} target="_blank" rel="noreferrer" aria-label={`Open attached PNG ${image.width} by ${image.height}`}><img src={url} alt={`Attached PNG image, ${image.width} × ${image.height}`} loading="lazy" /></a>
            })}</div>}
          </div>
        ))}
        {working && <div className="message manager pending"><div className="message-meta">{root?.name} · {root?.status}</div>{streaming && <div className="message-body streaming">{streaming}</div>}</div>}
        <div ref={bottom} />
      </div>
      <form className="composer" onSubmit={send}>
        <textarea value={text} disabled={locked} onChange={(event) => setText(event.target.value)} placeholder={`Message ${root?.name ?? 'the manager'}…`}
          onKeyDown={(event) => { if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) { event.preventDefault(); void send() } }} rows={3} />
        {!!images.length && <div className="draft-images">{images.map((image) => (
          <div className="draft-image" key={image.key}>
            <img src={image.url} alt={`Selected PNG ${image.file.name}`} />
            <div className="small"><span>{image.file.name}</span> · {image.state === 'ready' ? 'Ready' : image.state === 'uploading' ? 'Uploading…' : `Upload failed: ${image.error}`}</div>
            {image.state === 'failed' && <button type="button" disabled={locked} onClick={() => { void upload(image) }}>Retry upload</button>}
            <button type="button" disabled={locked} onClick={() => removeImage(image)} aria-label={`Remove ${image.file.name}`}>Remove</button>
          </div>
        ))}</div>}
        <div className="composer-actions">
          <label className="image-picker">Attach PNG (max 2, 2 MiB each)<input ref={fileInput} type="file" accept="image/png,.png" multiple disabled={locked || images.length >= MAX_IMAGES} onChange={selectImages} /></label>
          {error && <span className="error" role="alert">{error}</span>}
          <span className="muted small">⌘↩ to send</span>
          <button disabled={sending || uploading || (!pendingSend && !text.trim() && images.length === 0)}>{sending ? 'Sending…' : pendingSend ? 'Retry send' : 'Send'}</button>
        </div>
      </form>
    </div>
  )
}
