import { useEffect, useRef, useState, type ChangeEvent, type FormEvent } from 'react'
import { api, ago, type Agent, type ConversationEntry, type ConversationImage, type Project } from './api'
import { Markdown } from './Markdown'

const MAX_IMAGE_BYTES = 2 * 1024 * 1024
const MAX_IMAGES = 2

type DraftImage = { key: string; file: File; url: string; upload?: ConversationImage; state: 'uploading' | 'ready' | 'failed'; error?: string }
type PendingSend = { text: string; attachment_ids: string[]; client_message_id: string }
type StoredImage = Omit<DraftImage, 'url'>
export type StoredDraft = { text: string; images: StoredImage[]; pending?: PendingSend; error?: string; owner?: symbol }

/** Accept only the project's own authorized image route, never a remote or data: URL from metadata. */
function imageUrl(projectId: string, image: ConversationImage): string | undefined {
  const expected = `/api/v1/projects/${encodeURIComponent(projectId)}/conversation/images/${encodeURIComponent(image.id)}/content`
  return image.content_url === expected ? expected : undefined
}

export function Conversation(props: { project: Project; agents: Agent[]; tick: number; deltas: Record<string, string>; drafts: Map<string, StoredDraft> }) {
  // App owns this in-memory map ABOVE the conditional project view. Old sessions are
  // never rebound to a new primary session; they remain inert until the page reloads.
  const sessionId = props.agents.find((agent) => agent.id === props.project.root_agent_id)?.primary_session_id ?? 'unknown'
  const scope = `${props.project.id}:${props.project.root_agent_id}:${sessionId}`
  // An earlier mount may finish a request after A → B → A. Only the newest mount
  // owns this scope's in-memory draft; its token survives in the Map, not storage.
  const claim = (owner: symbol) => { props.drafts.set(scope, { ...props.drafts.get(scope) ?? { text: '', images: [] }, owner }) }
  const store = (snapshot: StoredDraft, owner: symbol) => {
    if (props.drafts.get(scope)?.owner === owner) props.drafts.set(scope, { ...snapshot, owner })
  }
  return <ProjectConversation key={scope} {...props} saved={props.drafts.get(scope)} claim={claim} store={store} />
}

function ProjectConversation({ project, agents, tick, deltas, saved, claim, store }: { project: Project; agents: Agent[]; tick: number; deltas: Record<string, string>; saved?: StoredDraft; claim: (owner: symbol) => void; store: (draft: StoredDraft, owner: symbol) => void }) {
  // Allocate object URLs after commit: StrictMode can discard a render before effects
  // attach, and render-time allocations would leak on that discarded render.
  const [entries, setEntries] = useState<ConversationEntry[]>([])
  const [text, setText] = useState(saved?.text ?? '')
  const textRef = useRef(saved?.text ?? '')
  const [images, setImages] = useState<DraftImage[]>([])
  const imagesRef = useRef<DraftImage[]>([])
  const [error, setError] = useState<string | undefined>(saved?.error)
  const errorRef = useRef(saved?.error)
  const [sending, setSending] = useState(false)
  const [pendingSend, setPendingSend] = useState<PendingSend | undefined>(saved?.pending)
  const pendingRef = useRef<PendingSend | undefined>(saved?.pending)
  const sendingRef = useRef(false)
  const active = useRef(true)
  const owner = useRef(Symbol('conversation draft mount'))
  const uploads = useRef(new Map<string, AbortController>())
  const sendController = useRef<AbortController | undefined>(undefined)
  const bottom = useRef<HTMLDivElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const root = agents.find((a) => a.id === project.root_agent_id)
  const locked = sending || !!pendingSend
  const uploading = images.some((image) => image.state === 'uploading')

  const persist = () => store({ text: textRef.current, images: imagesRef.current.map(({ url: _url, ...image }) => image), pending: pendingRef.current, error: errorRef.current }, owner.current)
  const setDraftImages = (update: (current: DraftImage[]) => DraftImage[]) => {
    const next = update(imagesRef.current)
    imagesRef.current = next
    persist()
    if (active.current) setImages(next)
  }
  const setDraftText = (next: string) => { textRef.current = next; persist(); if (active.current) setText(next) }
  const setDraftError = (next?: string) => { errorRef.current = next; persist(); if (active.current) setError(next) }
  useEffect(() => {
    active.current = true
    claim(owner.current)
    if (saved?.images.length) {
      imagesRef.current = saved.images.map((image) => ({ ...image, url: URL.createObjectURL(image.file) }))
      setImages(imagesRef.current)
      persist()
    }
    return () => {
      active.current = false
      sendController.current?.abort()
      uploads.current.forEach((controller) => controller.abort())
      // Abort/unknown upload receipts become retryable Files, not possibly-bound DELETEs.
      // A lost send receipt keeps its exact key/payload until explicitly confirmed.
      imagesRef.current = imagesRef.current.map((image) => image.state === 'uploading'
        ? { ...image, state: 'failed', error: 'Upload interrupted by project switch' } : image)
      persist()
      imagesRef.current.forEach((image) => URL.revokeObjectURL(image.url))
    }
    // This view is keyed by project/root/session; cleanup and URLs are mount-owned.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project.id])
  useEffect(() => {
    let current = true
    api.get<ConversationEntry[]>(`/projects/${project.id}/conversation`).then((items) => {
      if (!current) return
      // A poll may have started before a successful send. Never let a stale history
      // response erase its confirmed receipt while the next poll is still pending.
      setEntries((existing) => [...items, ...existing.filter((entry) => !items.some((item) => item.id === entry.id))])
    }).catch((e) => { if (current) setDraftError((e as Error).message) })
    return () => { current = false }
  }, [project.id, tick])
  useEffect(() => { bottom.current?.scrollIntoView({ behavior: 'smooth' }) }, [entries.length])

  const upload = async (draft: DraftImage): Promise<void> => {
    const controller = new AbortController()
    uploads.current.set(draft.key, controller)
    setDraftImages((current) => current.map((image) => image.key === draft.key ? { ...image, state: 'uploading', error: undefined } : image))
    try {
      const uploaded = await api.uploadConversationImage(project.id, draft.file, controller.signal)
      // An old completion must not change either a newer mount's draft or its quota.
      // Keep uncertain uploads for explicit retry/cleanup; never DELETE on switch.
      if (!active.current || controller.signal.aborted) return
      if (!imagesRef.current.some((image) => image.key === draft.key)) {
        // Only explicit removal while mounted can make an upload orphan; switching
        // never deletes an uncertain receipt that the server may have bound.
        if (active.current) void api.deleteConversationImage(project.id, uploaded.id).catch(() => {})
        return
      }
      if (!imageUrl(project.id, uploaded)) {
        if (!pendingRef.current) void api.deleteConversationImage(project.id, uploaded.id).catch(() => {})
        throw new Error('Upload returned an invalid project image URL')
      }
      setDraftImages((current) => current.map((image) => image.key === draft.key ? { ...image, upload: uploaded, state: 'ready' } : image))
      setDraftError(undefined)
    } catch (e) {
      if (!active.current || controller.signal.aborted) return
      const message = (e as Error).message
      setDraftImages((current) => current.map((image) => image.key === draft.key ? { ...image, state: 'failed', error: message } : image))
      setDraftError(`Image upload failed: ${message}. Retry the image without losing your draft.`)
    } finally {
      uploads.current.delete(draft.key)
    }
  }

  const selectImages = (event: ChangeEvent<HTMLInputElement>) => {
    const files = Array.from(event.target.files ?? [])
    event.target.value = '' // selecting the same file again must work after removal
    if (locked) return
    if (files.length + imagesRef.current.length > MAX_IMAGES) { setDraftError(`Choose at most ${MAX_IMAGES} PNG images per message.`); return }
    const invalid = files.find((file) => file.type !== 'image/png' || file.size > MAX_IMAGE_BYTES || file.size === 0)
    if (invalid) { setDraftError(`${invalid.name}: choose a PNG image no larger than 2 MiB (non-empty).`); return }
    setDraftError(undefined)
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
      if (active.current) setDraftError('Image removed from draft, but server cleanup failed. Retained upload quota may still be used.')
    })
  }

  const send = async (event?: FormEvent) => {
    event?.preventDefault()
    if (sendingRef.current || uploading) return
    const failed = imagesRef.current.find((image) => image.state !== 'ready' || !image.upload)
    if (failed) { setDraftError('Retry or remove failed image uploads before sending.'); return }
    const body = pendingRef.current ?? { text: textRef.current.trim(), attachment_ids: imagesRef.current.map((image) => image.upload!.id), client_message_id: crypto.randomUUID() }
    if (!body.text && body.attachment_ids.length === 0) return
    // On failed/lost-response retries, reuse the *exact* body and key. Lock all draft edits
    // until success, avoiding a server 409 or an accidental duplicate dispatch.
    pendingRef.current = body
    persist()
    setPendingSend(body)
    sendingRef.current = true
    setSending(true)
    setDraftError(undefined)
    const controller = new AbortController()
    sendController.current = controller
    try {
      const receipt = await api.sendConversation(project.id, body, controller.signal)
      // A stale mount cannot consume a receipt that the new owner must reconcile.
      // Its immutable pending key survives; an explicit retry resolves the server's
      // receipt idempotently, without auto-resending or erasing newer edits.
      if (!active.current || controller.signal.aborted || pendingRef.current?.client_message_id !== body.client_message_id) return
      imagesRef.current.forEach((image) => URL.revokeObjectURL(image.url))
      imagesRef.current = []
      textRef.current = ''
      pendingRef.current = undefined
      errorRef.current = undefined
      persist()
      if (!active.current) return
      setEntries((current) => current.some((item) => item.id === receipt.id) ? current : [...current, receipt])
      setImages([])
      setText('')
      setPendingSend(undefined)
      setError(undefined)
      // A failed history refresh must never turn a successful send back into a retryable draft.
      void api.get<ConversationEntry[]>(`/projects/${project.id}/conversation`).then((items) => {
        if (active.current) setEntries((existing) => [...items, ...existing.filter((entry) => !items.some((item) => item.id === entry.id))])
      }).catch(() => {})
    } catch (e) {
      if (active.current && !controller.signal.aborted) setDraftError(`Message not confirmed: ${(e as Error).message}. Retry uses the same message ID; your draft is intact.`)
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
        <textarea value={text} disabled={locked} onChange={(event) => setDraftText(event.target.value)} placeholder={`Message ${root?.name ?? 'the manager'}…`}
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
          <span className="muted small">⌘↩ to send · Unsent drafts exist only in this tab; reloading loses them.</span>
          <button disabled={sending || uploading || (!pendingSend && !text.trim() && images.length === 0)}>{sending ? 'Sending…' : pendingSend ? 'Retry send' : 'Send'}</button>
        </div>
      </form>
    </div>
  )
}
