export type PullRequestState = 'all' | 'open' | 'closed'
export interface PullRequestSummary {
  number: number; title: string; description: string | null; state: string; isDraft: boolean
  author: string; webUrl: string; baseReference: string; headReference: string
  createdAt: string; updatedAt: string; closedAt: string | null; mergedAt: string | null
}
export interface ChangedFile {
  path: string; status: string; additions: number; deletions: number; changes: number
  previousPath: string | null; patch: string | null
}
export interface Review {
  ai?: AiReport | null
  id: string; status: 'Processing' | 'Completed' | 'Failed'; summary: string | null; error: string | null
  pullRequest: {
    number: number; title: string; description: string | null; author: string; webUrl: string
    baseReference: string; headReference: string; additions: number; deletions: number
    changedFileCount: number; commitCount: number; files: ChangedFile[]
  } | null
  findings: { category: string; message: string; filePath: string | null; line: number | null; severity: string; confidence: number }[]
}
export interface AiSelection { provider: string; classificationModel?: string; reviewModel?: string }
export interface AiModel { id: string; classification: boolean; review: boolean; tools: boolean }
export interface AiProvider { provider: string; classificationModel: string; reviewModel: string; models: AiModel[]; hasServerCredentials?: boolean }
export interface AiCatalog { defaultProvider: string; providers: AiProvider[] }
export interface AiReport {
  selection: AiSelection | null
  classification: { category: string; rationale: string; scope: string; evidence: string[]; confidence: number } | null
  technologies: string[]
  skills: { id: string; version: string; hash: string }[]
  specialists: { specialist: string; summary: string; findings: Review['findings']; limitations: string[] }[]
  execution: { status: string; reason: string; steps: { name: string; status: string; exitCode: number | null; log: string }[] } | null
  limitations: string[]
  recommendation: 'Approve' | 'RequestChanges' | 'NeedsHumanReview'
  baseSha: string | null; headSha: string | null
  usage: { stage: string; provider: string; model: string; inputTokens: number | null; cachedInputTokens: number | null; outputTokens: number | null; estimatedCost: number | null; durationMs: number }[]
}
export interface ReviewEvent { type: 'started' | 'progress' | 'result'; reviewId: string; stage: string; message: string; review?: Review }
export async function getAiCatalog(signal: AbortSignal): Promise<AiCatalog> {
  const response = await fetch('/api/ai/models', { signal })
  if (!response.ok) throw new Error('No se pudo cargar el catálogo de IA.')
  return response.json()
}
export async function streamReview(location: string, pullRequestNumber: number, gitHubToken: string,
  ai: AiSelection, signal: AbortSignal, onEvent: (event: ReviewEvent) => void, aiApiKey?: string): Promise<Review> {
  const response = await fetch('/api/reviews/stream', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, signal,
    body: JSON.stringify({ provider: 'GitHub', location, pullRequestNumber, gitHubToken: gitHubToken || null, baseReference: null, headReference: null, ai, aiApiKey: aiApiKey?.trim() || null }),
  })
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail || (problem?.errors ? Object.values(problem.errors).flat().join(' ') : `Error de revisión (${response.status}).`))
  }
  if (!response.body) throw new Error('El servidor no devolvió un flujo de revisión.')
  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let pending = ''
  let result: Review | undefined
  const consume = (line: string) => {
    if (!line.trim()) return
    const event = JSON.parse(line) as ReviewEvent
    if (!['started', 'progress', 'result'].includes(event.type)) throw new Error('Evento de revisión inválido.')
    onEvent(event)
    if (event.type === 'result' && event.review) result = event.review
  }
  try {
    while (true) {
      const { value, done } = await reader.read()
      pending += decoder.decode(value, { stream: !done })
      if (pending.length > 8 * 1024 * 1024) throw new Error('Respuesta de revisión demasiado grande.')
      let newline: number
      while ((newline = pending.indexOf('\n')) >= 0) { consume(pending.slice(0, newline)); pending = pending.slice(newline + 1) }
      if (done) { consume(pending); break }
    }
    if (!result) throw new Error('Se interrumpió la conexión antes del resultado final. La revisión está incompleta.')
    return result
  } finally { await reader.cancel().catch(() => {}); reader.releaseLock() }
}
async function post<T>(path: string, body: unknown, signal: AbortSignal): Promise<T> {
  let response: Response
  try {
    response = await fetch(path, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal,
    })
  } catch (error) {
    if (signal.aborted) throw error
    throw new Error('No se pudo conectar con el backend. Comprueba que esté ejecutándose.')
  }
  const data = await response.json().catch(() => null)
  if (!response.ok) {
    const validation = data?.errors ? Object.values(data.errors).flat().join(' ') : null
    throw new Error(validation || data?.detail || `No se pudo completar la solicitud (${response.status}).`)
  }
  if (data === null) throw new Error('El backend devolvió una respuesta vacía o inválida.')
  return data as T
}
export const listPullRequests = (location: string, state: PullRequestState, gitHubToken: string, signal: AbortSignal) =>
  post<PullRequestSummary[]>('/api/pull-requests/list', { location, state, gitHubToken: gitHubToken || null }, signal)
export const createReview = (location: string, pullRequestNumber: number, gitHubToken: string, signal: AbortSignal) =>
  post<Review>('/api/reviews', { provider: 'GitHub', location, pullRequestNumber, baseReference: null, headReference: null, gitHubToken: gitHubToken || null }, signal)

export function safeGitHubUrl(value: string): string | undefined {
  try {
    const url = new URL(value)
    return url.protocol === 'https:' && url.hostname === 'github.com' ? url.href : undefined
  } catch { return undefined }
}
